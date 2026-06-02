#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! command -v azd >/dev/null 2>&1; then
  echo "Foundry remote TUI requires azd on PATH." >&2
  exit 1
fi

cd "$ROOT_DIR"

# Optional first positional arg is a session name. It overrides the
# per-user derived "tui-<sha256(oid)>" workspace key so you can target a
# fresh, named Foundry session (e.g. `run-foundry-tui-remote.sh barry`).
if [[ $# -gt 0 && "$1" != -* ]]; then
  export HERMES_FOUNDRY_WORKSPACE_KEY="$1"
  shift
fi

azd_env_get() {
  local name="$1"
  local value

  if ! value="$(azd env get-value "$name" 2>/dev/null)"; then
    echo "azd environment value $name is not set. Run 'azd up' or select the deployed environment." >&2
    exit 1
  fi

  if [[ -z "$value" ]]; then
    echo "azd environment value $name is empty. Run 'azd up' or select the deployed environment." >&2
    exit 1
  fi

  printf '%s\n' "$value"
}

azd_env_try_get() {
  local name="$1"
  local value

  if ! value="$(azd env get-value "$name" 2>/dev/null)"; then
    return 1
  fi

  [[ -n "$value" ]] || return 1
  printf '%s\n' "$value"
}

build_project_endpoint() {
  local account_endpoint="$1"
  local project_name="$2"

  account_endpoint="${account_endpoint%/}"
  case "$account_endpoint" in
    */openai/v1)
      account_endpoint="${account_endpoint%/openai/v1}"
      ;;
    */openai)
      account_endpoint="${account_endpoint%/openai}"
      ;;
  esac

  case "$account_endpoint" in
    */api/projects/*)
      printf '%s\n' "$account_endpoint"
      ;;
    *)
      printf '%s/api/projects/%s\n' "$account_endpoint" "$project_name"
      ;;
  esac
}

resolve_agent_project_endpoint() {
  local project_name
  local account_endpoint
  local project_endpoint

  project_name="$(azd_env_get AZURE_AI_PROJECT_NAME)"

  if project_endpoint="$(azd_env_try_get AZURE_AI_PROJECT_ENDPOINT)"; then
    printf '%s\n' "$project_endpoint"
    return
  fi

  if account_endpoint="$(azd_env_try_get AZURE_AI_SERVICES_ENDPOINT)"; then
    build_project_endpoint "$account_endpoint" "$project_name"
    return
  fi

  if account_endpoint="$(azd_env_try_get AZURE_OPENAI_ENDPOINT)"; then
    build_project_endpoint "$account_endpoint" "$project_name"
    return
  fi

  if account_endpoint="$(azd_env_try_get AZURE_FOUNDRY_BASE_URL)"; then
    build_project_endpoint "$account_endpoint" "$project_name"
    return
  fi

  echo "No Foundry project endpoint is available in the active azd environment." >&2
  exit 1
}

extract_api_version() {
  local endpoint="$1"
  local api_version

  api_version="$(printf '%s\n' "$endpoint" | sed -n 's/.*[?&]api-version=\([^&]*\).*/\1/p')"
  if [[ -z "$api_version" ]]; then
    echo "Could not parse api-version from AGENT_HERMES_FOUNDRY_AGENT_INVOCATIONS_ENDPOINT." >&2
    echo "Set HERMES_FOUNDRY_API_VERSION explicitly or redeploy the agent." >&2
    exit 1
  fi

  printf '%s\n' "$api_version"
}

export HERMES_FOUNDRY_ENDPOINT="$(resolve_agent_project_endpoint)"
export HERMES_FOUNDRY_AGENT_NAME="$(azd_env_get AGENT_HERMES_FOUNDRY_AGENT_NAME)"
export HERMES_FOUNDRY_API_VERSION="$(extract_api_version "$(azd_env_get AGENT_HERMES_FOUNDRY_AGENT_INVOCATIONS_ENDPOINT)")"

unset HERMES_FOUNDRY_INVOCATIONS_PATH
unset HERMES_FOUNDRY_INVOCATIONS_URL

exec "$ROOT_DIR/scripts/run-foundry-tui.sh" "$@"
