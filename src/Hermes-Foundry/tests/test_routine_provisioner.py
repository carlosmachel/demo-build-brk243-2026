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

    def test_prefers_raw_routine_isolation_headers(self) -> None:
        headers = {
            "X-Ms-User-Isolation-Key": " raw-user ",
            "x-ms-chat-isolation-key": "raw-chat",
            "x-agent-user-isolation-key": "agent-user",
            "x-agent-chat-isolation-key": "agent-chat",
        }

        self.assertEqual(
            routine_provisioner._routine_isolation_headers(headers),
            {
                "x-ms-user-isolation-key": "raw-user",
                "x-ms-chat-isolation-key": "raw-chat",
            },
        )

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


if __name__ == "__main__":
    unittest.main()
