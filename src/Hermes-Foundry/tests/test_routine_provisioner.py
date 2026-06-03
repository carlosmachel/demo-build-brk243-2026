from __future__ import annotations

import unittest
from unittest.mock import patch

from agent import routine_provisioner


class _FakeResponse:
    status = 200

    def __enter__(self) -> "_FakeResponse":
        return self

    def __exit__(self, *args: object) -> None:
        return None

    def read(self) -> bytes:
        return b"{}"


class RoutineProvisionerIsolationHeaderTests(unittest.TestCase):
    def test_maps_hosted_agent_isolation_headers_to_routine_headers(self) -> None:
        headers = {
            "X-Agent-User-Isolation-Key": " user-key ",
            "x-agent-chat-isolation-key": "chat-key",
        }

        self.assertEqual(
            routine_provisioner._routine_isolation_headers(headers),
            {
                "x-ms-user-isolation-key": "user-key",
                "x-ms-chat-isolation-key": "chat-key",
            },
        )

    def test_prefers_resolved_hosted_agent_isolation_headers(self) -> None:
        headers = {
            "X-Ms-User-Isolation-Key": " raw-user ",
            "x-ms-chat-isolation-key": "raw-chat",
            "x-agent-user-isolation-key": "agent-user",
            "x-agent-chat-isolation-key": "agent-chat",
        }

        self.assertEqual(
            routine_provisioner._routine_isolation_headers(headers),
            {
                "x-ms-user-isolation-key": "agent-user",
                "x-ms-chat-isolation-key": "agent-chat",
            },
        )

    def test_ignores_raw_routine_isolation_headers_without_resolved_keys(self) -> None:
        headers = {
            "X-Ms-User-Isolation-Key": " raw-user ",
            "x-ms-chat-isolation-key": "raw-chat",
        }

        self.assertEqual(routine_provisioner._routine_isolation_headers(headers), {})
        self.assertEqual(
            routine_provisioner._routine_isolation_header_source(headers),
            "x-ms-unresolved",
        )

    def test_reports_resolved_hosted_agent_isolation_header_source(self) -> None:
        self.assertEqual(
            routine_provisioner._routine_isolation_header_source(
                {
                    "x-ms-user-isolation-key": "raw-user",
                    "x-ms-chat-isolation-key": "raw-chat",
                    "x-agent-user-isolation-key": "agent-user",
                    "x-agent-chat-isolation-key": "agent-chat",
                }
            ),
            "x-agent",
        )

    def test_schedule_skips_unresolved_raw_routine_isolation_headers(self) -> None:
        headers = {
            "x-ms-user-isolation-key": "raw-user",
            "x-ms-chat-isolation-key": "raw-chat",
        }

        with (
            patch.object(routine_provisioner, "ensure_maintenance_routine") as ensure,
            self.assertLogs("hermes.maintenance", level="WARNING") as logs,
        ):
            routine_provisioner.schedule_maintenance_routine("buildbuild", headers)

        ensure.assert_not_called()
        self.assertIn("source=x-ms-unresolved", "\n".join(logs.output))

    def test_requires_both_routine_isolation_headers(self) -> None:
        self.assertFalse(
            routine_provisioner._has_required_routine_isolation_headers(
                {"x-ms-user-isolation-key": "user-key"}
            )
        )
        self.assertTrue(
            routine_provisioner._has_required_routine_isolation_headers(
                {
                    "x-ms-user-isolation-key": "user-key",
                    "x-ms-chat-isolation-key": "chat-key",
                }
            )
        )

    def test_request_forwards_routine_isolation_headers(self) -> None:
        captured_headers: dict[str, str] = {}

        def fake_urlopen(request: object, timeout: float) -> _FakeResponse:
            del timeout
            captured_headers.update(dict(request.header_items()))
            return _FakeResponse()

        with patch.object(routine_provisioner.urllib.request, "urlopen", fake_urlopen):
            status, body = routine_provisioner._request(
                "PUT",
                "https://example.test/routines/hermes-maint-test",
                "token",
                {"enabled": True},
                {
                    "x-ms-user-isolation-key": "user-key",
                    "x-ms-chat-isolation-key": "chat-key",
                },
            )

        normalized_headers = {
            name.lower(): value for name, value in captured_headers.items()
        }
        self.assertEqual(status, 200)
        self.assertEqual(body, "{}")
        self.assertEqual(normalized_headers["x-ms-user-isolation-key"], "user-key")
        self.assertEqual(normalized_headers["x-ms-chat-isolation-key"], "chat-key")


class RoutineProvisionerDesiredRoutineTests(unittest.TestCase):
    def test_desired_routine_marks_resolved_isolation_context_version(self) -> None:
        desired = routine_provisioner._desired_routine("buildbuild")

        self.assertEqual(
            desired["action"]["input"]["isolation_context_version"],
            "resolved-agent-headers-v1",
        )

    def test_routine_without_isolation_context_version_is_repaired(self) -> None:
        existing = routine_provisioner._desired_routine("buildbuild")
        existing["action"]["input"] = {
            "kind": "hermes.maintenance",
            "jobs": ["all"],
        }

        self.assertFalse(
            routine_provisioner._routine_matches(
                existing,
                routine_provisioner._desired_routine("buildbuild"),
            )
        )


if __name__ == "__main__":
    unittest.main()
