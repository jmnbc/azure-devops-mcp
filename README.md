# Azure MCP Agents Functions

This project implements Azure Functions that act as "MCP (Model Context Protocol) Tools". These tools expose various Azure DevOps functionalities, enabling interaction with Azure Boards and Azure Pipelines through a conversational AI interface.

## Project Structure

The core logic resides within the `AzureMcpAgents.Functions` directory.

- **`Program.cs`**: The entry point for the Azure Functions application. It handles the configuration of services, including:
    - Functions Web Application setup.
    - MCP Tool Metadata enablement.
    - Connection to Azure DevOps (VSS Connection) using configuration from `local.settings.json`.
- **`Tools/`**: This directory contains the individual MCP Tools.
    - **`AzureBoardsTool.cs`**: Provides functionalities for interacting with Azure Boards.
        - `ListProjects`: Lists Azure DevOps projects.
        - `ListWorkItems`: Lists work items within a specified project, with options to filter by work item type and limit the count.
        - `CreateWorkItem`: Creates a new work item (e.g., Bug, Task, User Story) in a specified project with details like title, description, assignee, and tags.
    - **`AzurePipelinesTool.cs`**: Provides functionalities for interacting with Azure Pipelines.
        - `ListPipelines`: Lists Azure DevOps pipelines for a given project.
        - `RunPipeline`: Triggers a specific Azure DevOps pipeline, with optional parameters for branch and pipeline variables.
        - `GetPipelineRunStatus`: Retrieves the status of a specific pipeline run (build).
    - **`SayHelloTool.cs`**: A simple tool that returns a "Hello" message, likely used for testing or demonstration.
- **`local.settings.json`**: Contains local development settings, including:
    - Azure Functions worker runtime configuration.
    - Connection details for Azure DevOps:
        - `Vss__OrgUrl`: The URL of the Azure DevOps organization.
        - `Vss__TenantId`: The Azure Active Directory tenant ID.
        - `Vss__ClientId`: The Client ID of the application registration used for authentication with Azure DevOps.
- **`Extensions/`**: (Assumed based on `using AzureMcpAgents.Functions.Extensions;` in `Program.cs`) This directory likely contains extension methods for service configuration or other shared utilities.

## Features

- **Azure Boards Integration**:
    - List available Azure DevOps projects.
    - Retrieve work items from a project, filterable by type.
    - Create new work items with detailed information.
- **Azure Pipelines Integration**:
    - List pipelines within a project.
    - Trigger pipeline runs, specifying branches and parameters.
    - Check the status of pipeline runs.
- **MCP Tool Framework**:
    - Functions are exposed as MCP Tools, identifiable by `[McpToolTrigger]` and `[McpToolProperty]` attributes.
    - This allows a conversational AI or similar platform to discover and invoke these functions with appropriate parameters.

## Getting Started

### Prerequisites

- .NET SDK (version compatible with `FUNCTIONS_WORKER_RUNTIME` specified in `local.settings.json`, .NET 8 or later for "dotnet-isolated").
- Azure Functions Core Tools.
- An Azure DevOps organization and a project within it.
- An Azure Active Directory application registration with permissions to access Azure DevOps.

### Configuration

1.  **Clone the repository.**
2.  **Update `local.settings.json`**:
    - Fill in `Vss__OrgUrl` with your Azure DevOps organization URL (e.g., `https://dev.azure.com/your-org`).
    - Update `Vss__TenantId` with your Azure AD tenant ID.
    - Update `Vss__ClientId` with the Client ID of your AAD app registration.
    - **Note**: For local development, `AzureWebJobsStorage` is set to `UseDevelopmentStorage=true`, which utilizes the Azure Storage Emulator. Ensure it's running or configure it to point to an actual Azure Storage account.
3.  **Set up Authentication**: Ensure the AAD application registration has the necessary API permissions for Azure DevOps (e.g., "Work Items Read & Write", "Build Read & Execute"). Grant admin consent if required. The authentication mechanism likely uses the provided ClientId and TenantId, possibly through a mechanism like Managed Identity or a client secret (though a secret is not explicitly visible in the provided files, it's a common pattern for `VssConnection`).

### Running Locally

1.  Navigate to the `AzureMcpAgents.Functions` directory.
2.  Run the functions host:
    ```bash
    func start
    ```
    The tools will then be available for an MCP-compatible platform to call.

## How It Works

The application uses the `Microsoft.TeamFoundationServer.Client` and `Microsoft.VisualStudio.Services.Client` libraries to interact with the Azure DevOps REST APIs.
Each "Tool" is an Azure Function triggered by the MCP framework. The `[McpToolTrigger]` attribute defines the tool's name and description, while `[McpToolProperty]` attributes define the expected input parameters for each tool.

When a tool is invoked (e.g., by a Copilot):
1. The Azure Function is triggered.
2. It uses the injected `VssConnection` (configured in `Program.cs`) to connect to Azure DevOps.
3. The respective client (e.g., `WorkItemTrackingHttpClient`, `BuildHttpClient`) is obtained from the `VssConnection`.
4. The client makes API calls to Azure DevOps to perform the requested action (e.g., list projects, create work item, run pipeline).
5. The results are typically serialized to JSON and returned.
