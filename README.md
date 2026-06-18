<a name="start-building"></a>
<br>
<p align="center">
<img src="img/banner-build-26.png" alt="Microsoft Build 2026" width="1200"/>
</p>

# [Microsoft Build 2026](https://build.microsoft.com)

## 🔥 BRK243: Claw and Agent Harness in Microsoft Foundry

### Session Description

Go deep on multi-agent systems built on Microsoft Foundry, featuring Claw agent patterns and the hosted agents architecture. Explore long-running agents with triggers, state management, and file access—all natively supported on Foundry. See how coding agents built with GitHub Copilot SDK and Claude Agent SDK integrate into multi-agent workflows using Microsoft Agent Framework. Learn how to coordinate, host, and operate these systems with observability and continuous evals.

### 🏠 Getting started in your own environment

If you're following these steps at your own pace:
- Clone this repository
- Set up your development environment
- Configure Azure AI environment variables (see "Run the Agent Harness samples" below)

### 🧪 Run the Agent Harness samples

This repository includes runnable .NET samples under `src/Agent-Harness`.

1. Install prerequisites:
     - .NET 10 SDK
     - Azure CLI (`az login`)
2. Set environment variables:

```powershell
$env:AZURE_AI_PROJECT_ENDPOINT = "https://<your-project>.services.ai.azure.com/api/projects/<your-project-name>"
$env:AZURE_AI_MODEL_DEPLOYMENT_NAME = "gpt-5.4"
$env:AZURE_AI_API_KEY="YOUR_API_KEY"
```

3. Build all sample projects from the repo root:

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

4. Run individual steps:

```powershell
dotnet run --project .\src\Agent-Harness\Harness_Step01_Research\Harness_Step01_Research.csproj
dotnet run --project .\src\Agent-Harness\Harness_Step02_Research_WithBackgroundAgents\Harness_Step02_Research_WithBackgroundAgents.csproj
dotnet run --project .\src\Agent-Harness\Harness_Step03_DataProcessing\Harness_Step03_DataProcessing.csproj
dotnet run --project .\src\Agent-Harness\Harness_Step04_CodeExecution\Harness_Step04_CodeExecution.csproj
```

For per-sample details and prompts to try, see `src/Agent-Harness/README.md`.


### 📚 Resources and Next Steps

| Resource | Description |
|:---------|:------------|
| [https://aka.ms/build26-next-steps](https://aka.ms/build26-next-steps) | Explore lab and session repos to further your learning from Microsoft Build |


### 🌟 Microsoft Learn MCP Server

The Microsoft Learn MCP Server gives your AI agent direct access to Microsoft's official documentation — grounded, up-to-date answers about the products and services covered in this session.

**VS Code** — One click installation: 

[![Install in VS Code](https://img.shields.io/badge/VS_Code-Install_Microsoft_Learn_MCP-0098FF?style=flat-square&logo=visualstudiocode&logoColor=white)](https://vscode.dev/redirect/mcp/install?name=microsoft-learn&config=%7B%22type%22%3A%22http%22%2C%22url%22%3A%22https%3A%2F%2Flearn.microsoft.com%2Fapi%2Fmcp%22%7D)


**GitHub Copilot CLI** — Run this to install the Learn MCP Server as a plugin:
```
/plugin install microsoftdocs/mcp
```

For more info, other clients, and to post questions, visit the [Learn MCP Server repo](https://aka.ms/learnmcp).

## Content Owners


<table>
<tr>
    <td align="center"><a href="http://github.com/sphenry">
        <img src="https://github.com/sphenry.png" width="100px;" alt="Shawn Henry"/><br />
        <sub><b>Shawn Henry</b></sub></a><br />
            <a href="https://github.com/sphenry" title="talk">📢</a>
    </td>
</tr></table>

## Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit [Contributor License Agreements](https://cla.opensource.microsoft.com).

When you submit a pull request, a CLA bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft
trademarks or logos is subject to and must follow
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos are subject to those third-party's policies.
