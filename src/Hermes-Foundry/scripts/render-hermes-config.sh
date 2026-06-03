#!/usr/bin/env sh
set -eu

script_dir="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
out_dir="$repo_root/agent/hermes-defaults"
out_file="$out_dir/config.yaml"

trim() {
    sed 's/^[[:space:]]*//;s/[[:space:]]*$//'
}

get_azd_value() {
    if value="$(azd env get-value "$1" 2>/dev/null)"; then
        printf '%s' "$value" | trim
    fi
}

require_azd_value() {
    name="$1"
    value="$(get_azd_value "$name")"
    if [ -z "$value" ]; then
        echo "$name is not set in the azd environment. Run azd provision before packaging or deploying." >&2
        exit 1
    fi
    printf '%s' "$value"
}

normalize_foundry_base_url() {
    normalized="$(printf '%s' "$1" | trim)"
    while [ "${normalized%/}" != "$normalized" ]; do
        normalized="${normalized%/}"
    done

    lower="$(printf '%s' "$normalized" | tr '[:upper:]' '[:lower:]')"
    case "$lower" in
        */openai/v1)
            ;;
        */openai)
            normalized="${normalized}/v1"
            ;;
        *".openai.azure.com"*)
            normalized="${normalized}/openai/v1"
            ;;
    esac

    printf '%s' "$normalized"
}

yaml_double_quote() {
    printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'
}

deployment_name="$(require_azd_value AZURE_FOUNDRY_MODEL_DEPLOYMENT_NAME)"
base_url="$(normalize_foundry_base_url "$(require_azd_value AZURE_FOUNDRY_BASE_URL)")"
api_mode="$(require_azd_value AZURE_FOUNDRY_MODEL_API_MODE)"
auth_mode="$(get_azd_value AZURE_FOUNDRY_AUTH_MODE)"
if [ -z "$auth_mode" ]; then
    auth_mode="entra_id"
fi
aux_deployment_name="$(get_azd_value AZURE_FOUNDRY_AUX_MODEL_DEPLOYMENT_NAME)"
case "$auth_mode" in
    entra_id|api_key)
        ;;
    *)
        echo "Unsupported AZURE_FOUNDRY_AUTH_MODE '$auth_mode'. Expected 'entra_id' or 'api_key'." >&2
        exit 1
        ;;
esac

if [ -z "$base_url" ]; then
    echo "AZURE_FOUNDRY_BASE_URL resolved to an empty base URL." >&2
    exit 1
fi

toolbox_mcp_url="$(get_azd_value HERMES_FOUNDRY_TOOLBOX_MCP_URL)"

mkdir -p "$out_dir"
rm -f "$out_file"
{
    printf 'model:\n'
    printf '  provider: "%s"\n' "$(yaml_double_quote azure-foundry)"
    printf '  default: "%s"\n' "$(yaml_double_quote "$deployment_name")"
    printf '  base_url: "%s"\n' "$(yaml_double_quote "$base_url")"
    printf '  api_mode: "%s"\n' "$(yaml_double_quote "$api_mode")"
    printf '  auth_mode: "%s"\n' "$(yaml_double_quote "$auth_mode")"
    printf 'providers:\n'
    printf '  azure-foundry:\n'
    printf '    stale_timeout_seconds: 300\n'
    if [ -n "$aux_deployment_name" ]; then
        printf 'auxiliary:\n'
        for aux_task in vision web_extract compression approval mcp title_generation skills_hub triage_specifier kanban_decomposer profile_describer curator; do
            printf '  %s:\n' "$aux_task"
            printf '    provider: "%s"\n' "$(yaml_double_quote azure-foundry)"
            printf '    model: "%s"\n' "$(yaml_double_quote "$aux_deployment_name")"
            printf '    api_mode: "%s"\n' "$(yaml_double_quote chat_completions)"
        done
    fi
    if [ -n "$toolbox_mcp_url" ]; then
        printf 'mcp_servers:\n'
        printf '  ftb:\n'
        printf '    url: "%s"\n' "$(yaml_double_quote "$toolbox_mcp_url")"
        printf '    auth: "%s"\n' "$(yaml_double_quote "entra_id")"
        printf '    headers:\n'
        printf '      Foundry-Features: "%s"\n' "$(yaml_double_quote "Toolboxes=V1Preview")"
        printf '    entra:\n'
        printf '      scope: "%s"\n' "$(yaml_double_quote "https://ai.azure.com/.default")"
        printf '    timeout: 120\n'
        printf '    connect_timeout: 60\n'
        printf '    supports_parallel_tool_calls: false\n'
        printf '    tools:\n'
        printf '      resources: false\n'
        printf '      prompts: false\n'
    fi
} >"$out_file"

echo "Rendered Hermes config: agent/hermes-defaults/config.yaml"
