using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace AzureMcpAgents.Functions.Tools;

public class AzurePipelinesTool
{
    private readonly ILogger<AzurePipelinesTool> _logger;
    private readonly VssConnection _connection;

    public AzurePipelinesTool(ILogger<AzurePipelinesTool> logger, VssConnection connection)
    {
        _logger = logger;
        _connection = connection;
    }

    [Function("ListPipelines")]
    public async Task<string> ListPipelines(
        [McpToolTrigger("list_azure_pipelines", "Lists Azure DevOps pipelines for a specific project.")] ToolInvocationContext context,
        [McpToolProperty("projectName", "string", "Name of the Azure DevOps project.")] string projectName)
    {
        _logger.LogInformation("Listing Azure DevOps pipelines for project {ProjectName}", projectName);

        if (string.IsNullOrEmpty(projectName))
        {
            _logger.LogError("Project name not provided or is empty.");
            return JsonSerializer.Serialize(new { error = "Project name is required and cannot be empty." });
        }

        try
        {
            var buildClient = _connection.GetClient<BuildHttpClient>();
            var definitions = await buildClient.GetDefinitionsAsync(project: projectName);

            if (definitions == null || !definitions.Any())
            {
                _logger.LogInformation("No pipelines found in project {ProjectName}", projectName);
                return JsonSerializer.Serialize(new List<object>()); // Return empty list
            }

            var result = definitions.Select(d => new
            {
                Id = d.Id,
                Name = d.Name,
                Path = d.Path,
                Url = d.Url, // This is the API URL
                WebUrl = (d.Links?.Links.TryGetValue("web", out var webLink) == true && webLink is ReferenceLink rl) ? rl.Href : string.Empty,
                Folder = d.Path?.Trim('\\') // Get folder from path
            }).ToList();
            
            _logger.LogInformation("Retrieved {Count} pipelines from project {ProjectName}", result.Count, projectName);
            return JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing pipelines in project {ProjectName}: {Message}", projectName, ex.Message);
            return JsonSerializer.Serialize(new { error = $"Error listing pipelines: {ex.Message}" });
        }
    }

    [Function("RunPipeline")]
    public async Task<string> RunPipeline(
        [McpToolTrigger("run_azure_pipeline", "Runs a specific Azure DevOps pipeline.")] ToolInvocationContext context,
        [McpToolProperty("projectName", "string", "Name of the Azure DevOps project.")] string projectName,
        [McpToolProperty("pipelineId", "string", "ID of the pipeline to run.")] string pipelineIdRaw,
        [McpToolProperty("branchName", "string", "Name of the branch to run the pipeline on (optional).")] string? branchName,
        [McpToolProperty("pipelineParameters", "string", "JSON string of key-value pairs for pipeline parameters (optional).")] string? pipelineParametersJson)
    {
        _logger.LogInformation("Attempting to run pipeline ID {PipelineIdRaw} in project {ProjectName}", pipelineIdRaw, projectName);

        if (string.IsNullOrEmpty(projectName))
        {
            _logger.LogError("Project name not provided or is empty.");
            return JsonSerializer.Serialize(new { error = "Project name is required and cannot be empty." });
        }

        if (string.IsNullOrEmpty(pipelineIdRaw) || !int.TryParse(pipelineIdRaw, out int pipelineId) || pipelineId <= 0)
        {
            _logger.LogError("Invalid Pipeline ID '{PipelineIdRaw}' provided. It must be a positive integer.", pipelineIdRaw);
            return JsonSerializer.Serialize(new { error = "Pipeline ID is required and must be a positive integer." });
        }

        try
        {
            var buildClient = _connection.GetClient<BuildHttpClient>();

            // Get the pipeline definition to ensure it exists and to use its default branch if no branch is specified.
            BuildDefinition definition;
            try
            {
                definition = await buildClient.GetDefinitionAsync(project: projectName, definitionId: pipelineId);
                if (definition == null)
                {
                    _logger.LogError("Pipeline with ID {PipelineId} not found in project {ProjectName}.", pipelineId, projectName);
                    return JsonSerializer.Serialize(new { error = $"Pipeline with ID {pipelineId} not found in project '{projectName}'." });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving pipeline definition for ID {PipelineId} in project {ProjectName}: {Message}", pipelineId, projectName, ex.Message);
                return JsonSerializer.Serialize(new { error = $"Error retrieving pipeline definition: {ex.Message}" });
            }

            var build = new Build
            {
                Definition = new DefinitionReference { Id = pipelineId },
                Project = definition.Project // Use the project from the definition
            };

            if (!string.IsNullOrEmpty(branchName))
            {
                build.SourceBranch = branchName;
            }
            // If no branchName is provided, it will typically use the pipeline's default branch.

            if (!string.IsNullOrEmpty(pipelineParametersJson))
            {
                try
                {
                    var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(pipelineParametersJson);
                    if (parameters != null)
                    {
                        build.Parameters = JsonSerializer.Serialize(parameters); // Build.Parameters expects a JSON string
                    }
                }
                catch (JsonException jsonEx)
                {
                    _logger.LogError(jsonEx, "Error deserializing pipeline parameters JSON: {Json}", pipelineParametersJson);
                    return JsonSerializer.Serialize(new { error = $"Invalid pipeline parameters JSON format: {jsonEx.Message}" });
                }
            }

            _logger.LogInformation("Queueing build for pipeline {PipelineId} in project {ProjectName} with SourceBranch: '{SourceBranch}' and Parameters: '{Parameters}'", 
                pipelineId, projectName, build.SourceBranch ?? "default", build.Parameters ?? "none");

            Build queuedBuild = await buildClient.QueueBuildAsync(build, project: projectName);

            _logger.LogInformation("Successfully queued build ID {BuildId} for pipeline {PipelineId} with status {Status}", queuedBuild.Id, pipelineId, queuedBuild.Status?.ToString() ?? "Unknown");

            return JsonSerializer.Serialize(new
            {
                BuildId = queuedBuild.Id,
                PipelineId = queuedBuild.Definition?.Id,
                ProjectName = queuedBuild.Project?.Name,
                Status = queuedBuild.Status?.ToString(),
                QueueTime = queuedBuild.QueueTime?.ToString("o"),
                Reason = queuedBuild.Reason.ToString(),
                RequestedFor = queuedBuild.RequestedFor?.DisplayName,
                WebUrl = (queuedBuild.Links?.Links.TryGetValue("web", out var webLink) == true && webLink is ReferenceLink rl) ? rl.Href : string.Empty,
                ApiUrl = queuedBuild.Url
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running pipeline ID {PipelineId} in project {ProjectName}: {Message}", pipelineId, projectName, ex.Message);
            return JsonSerializer.Serialize(new { error = $"Error running pipeline: {ex.Message}" });
        }
    }

    [Function("GetPipelineRunStatus")]
    public async Task<string> GetPipelineRunStatus(
        [McpToolTrigger("get_azure_pipeline_run_status", "Gets the status of a specific Azure DevOps pipeline run (build).")] ToolInvocationContext context,
        [McpToolProperty("projectName", "string", "Name of the Azure DevOps project.")] string projectName,
        [McpToolProperty("buildId", "string", "ID of the pipeline run (build) to get status for.")] string buildIdRaw)
    {
        _logger.LogInformation("Attempting to get status for pipeline run (build) ID {BuildIdRaw} in project {ProjectName}", buildIdRaw, projectName);

        if (string.IsNullOrEmpty(projectName))
        {
            _logger.LogError("Project name not provided or is empty for getting pipeline run status.");
            return JsonSerializer.Serialize(new { error = "Project name is required and cannot be empty." });
        }

        if (string.IsNullOrEmpty(buildIdRaw) || !int.TryParse(buildIdRaw, out int buildId) || buildId <= 0)
        {
            _logger.LogError("Invalid Build ID '{BuildIdRaw}' provided. It must be a positive integer.", buildIdRaw);
            return JsonSerializer.Serialize(new { error = "Build ID is required and must be a positive integer." });
        }

        try
        {
            var buildClient = _connection.GetClient<BuildHttpClient>();
            Build build;
            try
            {
                build = await buildClient.GetBuildAsync(project: projectName, buildId: buildId);
                if (build == null)
                {
                    _logger.LogWarning("Pipeline run (build) with ID {BuildId} not found in project {ProjectName}.", buildId, projectName);
                    return JsonSerializer.Serialize(new { error = $"Pipeline run (build) with ID {buildId} not found in project '{projectName}'." });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving pipeline run (build) with ID {BuildId} in project {ProjectName}: {Message}", buildId, projectName, ex.Message);
                return JsonSerializer.Serialize(new { error = $"Error retrieving pipeline run status: {ex.Message}" });
            }
            
            _logger.LogInformation("Successfully retrieved status for build ID {BuildId}: Status - {Status}, Result - {Result}", build.Id, build.Status?.ToString() ?? "Unknown", build.Result?.ToString() ?? "Unknown");

            return JsonSerializer.Serialize(new
            {
                BuildId = build.Id,
                PipelineId = build.Definition?.Id,
                PipelineName = build.Definition?.Name,
                ProjectName = build.Project?.Name,
                Status = build.Status?.ToString(),
                Result = build.Result?.ToString(),
                QueueTime = build.QueueTime?.ToString("o"),
                StartTime = build.StartTime?.ToString("o"),
                FinishTime = build.FinishTime?.ToString("o"),
                SourceBranch = build.SourceBranch,
                SourceVersion = build.SourceVersion,
                Reason = build.Reason.ToString(),
                RequestedFor = build.RequestedFor?.DisplayName,
                LastChangedDate = build.LastChangedDate.ToString("o"),
                WebUrl = (build.Links?.Links.TryGetValue("web", out var webLink) == true && webLink is ReferenceLink rl) ? rl.Href : string.Empty,
                ApiUrl = build.Url
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting status for pipeline run (build) ID {BuildId} in project {ProjectName}: {Message}", buildId, projectName, ex.Message);
            return JsonSerializer.Serialize(new { error = $"Error getting pipeline run status: {ex.Message}" });
        }
    }
} 