# Harness Agent Samples

Samples demonstrating the [Harness AIContextProviders](../../../src/Microsoft.Agents.AI/Harness/) — reusable providers that add planning, task management, and mode tracking to any `ChatClientAgent`.

These are based on sample from the Microsoft Agent Framework repo here: 
https://github.com/microsoft/agent-framework/

## Samples

| Sample | Description |
| --- | --- |
| [Harness_Step01_Research](./Harness_Step01_Research/README.md) | Using a ChatClientAgent with TodoProvider and AgentModeProvider for research, showcasing planning mode and todo management |
| [Harness_Step02_Research_WithBackgroundAgents](./Harness_Step02_Research_WithBackgroundAgents/README.md) | Using BackgroundAgentsProvider to delegate stock price lookups to a web-search background agent concurrently |
| [Harness_Step03_DataProcessing](./Harness_Step03_DataProcessing/README.md) | Using FileAccessProvider to give an agent access to CSV data files for reading, analysis, and output generation |
| [Harness_Step04_CodeExecution](./Harness_Step04_CodeExecution/README.md) | Using HyperlightCodeActProvider and AgentSkillsProvider to run sandboxed Python code and execute skill-driven workflows |

## Prerequisites

- .NET 10 SDK
- Azure CLI signed in (`az login`) or another credential source supported by `DefaultAzureCredential` — or an `AZURE_AI_API_KEY` (see below)
- Azure AI Foundry project endpoint and model deployment name

For `Harness_Step04_CodeExecution`:

- A host that supports Hyperlight execution requirements for sandboxed code execution

## Configure Environment Variables

All samples in this folder use the same variables:

- `AZURE_AI_PROJECT_ENDPOINT`
- `AZURE_AI_MODEL_DEPLOYMENT_NAME` (optional, defaults to `gpt-5.4`)
- `AZURE_AI_API_KEY` (optional) — if set, samples authenticate to the Foundry project's OpenAI-compatible endpoint with this static key instead of Microsoft Entra ID. Leave unset to keep using `DefaultAzureCredential` (`az login`, managed identity, etc.), which is recommended for anything beyond local development.

PowerShell:

```powershell
$env:AZURE_AI_PROJECT_ENDPOINT = "https://<your-project>.services.ai.azure.com/api/projects/<your-project-name>"
$env:AZURE_AI_MODEL_DEPLOYMENT_NAME = "gpt-5.4"
# Optional: use an API key instead of Microsoft Entra ID
$env:AZURE_AI_API_KEY = "<your-api-key>"
```

Bash:

```bash
export AZURE_AI_PROJECT_ENDPOINT="https://<your-project>.services.ai.azure.com/api/projects/<your-project-name>"
export AZURE_AI_MODEL_DEPLOYMENT_NAME="gpt-5.4"
# Optional: use an API key instead of Microsoft Entra ID
export AZURE_AI_API_KEY="<your-api-key>"
```

## Build All Samples

From repo root:

```powershell
$projects = @(
	".\\src\\Agent-Harness\\ConsoleReactiveFramework\\ConsoleReactiveFramework.csproj",
	".\\src\\Agent-Harness\\ConsoleReactiveComponents\\ConsoleReactiveComponents.csproj",
	".\\src\\Agent-Harness\\Harness_Shared_Console\\Harness_Shared_Console.csproj",
	".\\src\\Agent-Harness\\Harness_Shared_Console_OpenAI\\Harness_Shared_Console_OpenAI.csproj",
	".\\src\\Agent-Harness\\Harness_Step01_Research\\Harness_Step01_Research.csproj",
	".\\src\\Agent-Harness\\Harness_Step02_Research_WithBackgroundAgents\\Harness_Step02_Research_WithBackgroundAgents.csproj",
	".\\src\\Agent-Harness\\Harness_Step03_DataProcessing\\Harness_Step03_DataProcessing.csproj",
	".\\src\\Agent-Harness\\Harness_Step04_CodeExecution\\Harness_Step04_CodeExecution.csproj"
)

foreach ($p in $projects) {
	dotnet build $p -nologo
}
```

## Run The Samples

Run from repo root:

```powershell
dotnet run --project .\src\Agent-Harness\Harness_Step01_Research\Harness_Step01_Research.csproj
dotnet run --project .\src\Agent-Harness\Harness_Step02_Research_WithBackgroundAgents\Harness_Step02_Research_WithBackgroundAgents.csproj
dotnet run --project .\src\Agent-Harness\Harness_Step03_DataProcessing\Harness_Step03_DataProcessing.csproj
dotnet run --project .\src\Agent-Harness\Harness_Step04_CodeExecution\Harness_Step04_CodeExecution.csproj
```

## What To Try

- Step 01 (Research): Ask for a deep-dive topic and use `/todos` and `/mode` during planning/execution.
- Step 02 (Background Agents): Enter ticker symbols like `BAC, MSFT, BA` and let the parent agent fan out lookups.
- Step 03 (Data Processing): Ask for summaries of `working/sales.csv` and request output files.
- Step 04 (Code Execution): Ask the agent to run Python code (for example regex validation or numeric computations).

## Notes

- Most samples support `/exit`; Step 01 and Step 04 also support `/todos` and `/mode`.
- Trace output is written by the harness tracing configuration during sample execution.
- Step-specific details are documented in each sample's README linked in the table above.
