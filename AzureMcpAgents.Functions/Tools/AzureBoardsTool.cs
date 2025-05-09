using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

namespace AzureMcpAgents.Functions.Tools;

public class AzureBoardsTool
{
    private readonly ILogger<AzureBoardsTool> _logger;
    private readonly VssConnection _connection;

    public AzureBoardsTool(ILogger<AzureBoardsTool> logger, VssConnection connection)
    {
        _logger = logger;
        _connection = connection;
    }
    
    [Function("ListProjects")]
    public async Task<string> ListProjects(
        [McpToolTrigger("list_azure_boards_projects", "Lists Azure DevOps projects")] ToolInvocationContext context)
    {
        _logger.LogInformation("Listing Azure DevOps projects");
        
        try
        {
            var projectClient = _connection.GetClient<ProjectHttpClient>();
            var projects = await projectClient.GetProjects();
            var projectNames = projects.Select(p => p.Name).ToList();
            
            return JsonSerializer.Serialize(projectNames);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing projects: {Message}", ex.Message);
            throw;
        }
    }

    [Function("ListWorkItems")]
    public async Task<string> ListWorkItems(
        [McpToolTrigger("list_azure_boards_work_items", "Lists Azure DevOps work items for a specific project and type.")] ToolInvocationContext context,
        [McpToolProperty("projectName", "string", "Name of the Azure DevOps project.")] string projectName,
        [McpToolProperty("workItemType", "string", "Type of work items to retrieve (e.g., Bug, Task, User Story). Defaults to Task if not specified.")] string? workItemTypeRaw,
        [McpToolProperty("maxCount", "string", "Maximum number of work items to retrieve. Defaults to 10 if not specified or invalid.")] string? maxCountRaw)
    {
        _logger.LogInformation("Listing Azure DevOps work items.");

        if (string.IsNullOrEmpty(projectName))
        {
            _logger.LogError("Project name not provided or is empty.");
            return JsonSerializer.Serialize(new { error = "Project name is required and cannot be empty." });
        }

        string workItemType = !string.IsNullOrEmpty(workItemTypeRaw) ? workItemTypeRaw : "Task";

        int maxCount = 10; // Default value
        if (!string.IsNullOrEmpty(maxCountRaw) && int.TryParse(maxCountRaw, out int parsedMaxCount) && parsedMaxCount > 0)
        {
            maxCount = parsedMaxCount;
        }
        else if (!string.IsNullOrEmpty(maxCountRaw))
        {
            _logger.LogWarning("Invalid maxCount value '{MaxCountRaw}' provided. Defaulting to {DefaultMaxCount}.", maxCountRaw, maxCount);
        }

        try
        {
            var witClient = _connection.GetClient<WorkItemTrackingHttpClient>();

            var wiqlQuery = new Wiql
            {
                Query = $"SELECT [System.Id], [System.Title], [System.State], [System.AssignedTo], [System.Tags] " +
                        $"FROM WorkItems " +
                        $"WHERE [System.TeamProject] = '{projectName}' " +
                        (string.IsNullOrEmpty(workItemType) ? "" : $"AND [System.WorkItemType] = '{workItemType}' ") +
                        $"ORDER BY [System.ChangedDate] DESC"
            };
            
            _logger.LogInformation("Executing WIQL query: {Query}", wiqlQuery.Query);

            var queryResult = await witClient.QueryByWiqlAsync(wiqlQuery);

            if (queryResult.WorkItems == null || !queryResult.WorkItems.Any())
            {
                _logger.LogInformation("No work items found in project {Project} of type {Type}", projectName, workItemType);
                return JsonSerializer.Serialize(new List<object>()); // Return empty list
            }

            var workItemIds = queryResult.WorkItems
                .Take(maxCount)
                .Select(wi => wi.Id)
                .ToList();

            if (!workItemIds.Any())
            {
                 _logger.LogInformation("No work items to fetch details for after taking maxCount {MaxCount}.", maxCount);
                return JsonSerializer.Serialize(new List<object>());
            }
            
            _logger.LogInformation("Fetching details for {Count} work items.", workItemIds.Count);

            var workItemDetailsList = await witClient.GetWorkItemsAsync(
                workItemIds,
                new List<string> { "System.Id", "System.Title", "System.State", "System.AssignedTo", "System.Tags" },
                queryResult.AsOf);

            if (workItemDetailsList == null || !workItemDetailsList.Any())
            {
                _logger.LogWarning("Retrieved work item IDs but could not fetch details.");
                return JsonSerializer.Serialize(new { error = "Retrieved work item IDs but could not fetch details." });
            }

            var result = workItemDetailsList.Select(item =>
            {
                var assignedTo = "Unassigned";
                if (item.Fields.TryGetValue("System.AssignedTo", out var assignedToObj) && assignedToObj != null)
                {
                    if (assignedToObj is IdentityRef identityRef)
                    {
                        assignedTo = identityRef.DisplayName ?? "Unassigned";
                    }
                    else
                    {
                         assignedTo = assignedToObj.ToString();
                    }
                }
                return new
                {
                    Id = item.Id,
                    Title = item.Fields.TryGetValue("System.Title", out var title) ? title?.ToString() : "No Title",
                    State = item.Fields.TryGetValue("System.State", out var state) ? state?.ToString() : "Unknown",
                    AssignedTo = assignedTo,
                    Tags = item.Fields.TryGetValue("System.Tags", out var tags) ? tags?.ToString() : ""
                };
            }).ToList();
            
            _logger.LogInformation("Retrieved {Count} work items from project {Project}", result.Count, projectName);
            return JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing work items: {Message}", ex.Message);
            // Consider how to best return error information. For now, returning a JSON object with an error message.
            return JsonSerializer.Serialize(new { error = $"Error listing work items: {ex.Message}" });
        }
    }

    [Function("CreateWorkItem")]
    public async Task<string> CreateWorkItem(
        [McpToolTrigger("create_azure_boards_work_item", "Creates a new work item in Azure DevOps.")] ToolInvocationContext context,
        [McpToolProperty("projectName", "string", "Name of the Azure DevOps project where the work item will be created.")] string projectName,
        [McpToolProperty("workItemType", "string", "Type of the work item to create (e.g., Bug, Task, User Story).")] string workItemType,
        [McpToolProperty("title", "string", "Title of the new work item.")] string title,
        [McpToolProperty("description", "string", "Description for the new work item (optional).")] string? description,
        [McpToolProperty("assignedTo", "string", "Display name or email of the user to assign the work item to (optional).")] string? assignedTo,
        [McpToolProperty("tags", "string", "Semicolon-separated list of tags for the work item (optional).")] string? tags)
    {
        _logger.LogInformation("Attempting to create a new work item in project {ProjectName} of type {WorkItemType} with title {Title}", projectName, workItemType, title);

        if (string.IsNullOrEmpty(projectName))
        {
            _logger.LogError("Project name not provided or is empty for creating a work item.");
            return JsonSerializer.Serialize(new { error = "Project name is required and cannot be empty." });
        }
        if (string.IsNullOrEmpty(workItemType))
        {
            _logger.LogError("Work item type not provided or is empty for creating a work item.");
            return JsonSerializer.Serialize(new { error = "Work item type is required and cannot be empty." });
        }
        if (string.IsNullOrEmpty(title))
        {
            _logger.LogError("Title not provided or is empty for creating a work item.");
            return JsonSerializer.Serialize(new { error = "Title is required and cannot be empty." });
        }

        try
        {
            var witClient = _connection.GetClient<WorkItemTrackingHttpClient>();
            var patchDocument = new Microsoft.VisualStudio.Services.WebApi.Patch.Json.JsonPatchDocument();

            patchDocument.Add(
                new Microsoft.VisualStudio.Services.WebApi.Patch.Json.JsonPatchOperation()
                {
                    Operation = Microsoft.VisualStudio.Services.WebApi.Patch.Operation.Add,
                    Path = "/fields/System.Title",
                    Value = title
                }
            );

            if (!string.IsNullOrEmpty(description))
            {
                patchDocument.Add(
                    new Microsoft.VisualStudio.Services.WebApi.Patch.Json.JsonPatchOperation()
                    {
                        Operation = Microsoft.VisualStudio.Services.WebApi.Patch.Operation.Add,
                        Path = "/fields/System.Description",
                        Value = description
                    }
                );
            }

            if (!string.IsNullOrEmpty(assignedTo))
            {
                // Azure DevOps expects IdentityRef for AssignedTo, but API might resolve display names.
                // For robust assignment, a lookup to get IdentityRef might be needed.
                // For now, we pass the string directly.
                patchDocument.Add(
                    new Microsoft.VisualStudio.Services.WebApi.Patch.Json.JsonPatchOperation()
                    {
                        Operation = Microsoft.VisualStudio.Services.WebApi.Patch.Operation.Add,
                        Path = "/fields/System.AssignedTo",
                        Value = assignedTo 
                    }
                );
            }

            if (!string.IsNullOrEmpty(tags))
            {
                patchDocument.Add(
                    new Microsoft.VisualStudio.Services.WebApi.Patch.Json.JsonPatchOperation()
                    {
                        Operation = Microsoft.VisualStudio.Services.WebApi.Patch.Operation.Add,
                        Path = "/fields/System.Tags",
                        Value = tags // Semicolon-separated tags are typically handled correctly by the API
                    }
                );
            }
            
            _logger.LogInformation("Sending request to create work item with patch: {PatchDocument}", JsonSerializer.Serialize(patchDocument));


            WorkItem resultWorkItem = await witClient.CreateWorkItemAsync(patchDocument, projectName, workItemType);

            if (resultWorkItem?.Id != null)
            {
                _logger.LogInformation("Successfully created work item with ID: {WorkItemId} in project {ProjectName}", resultWorkItem.Id, projectName);
                return JsonSerializer.Serialize(new
                {
                    id = resultWorkItem.Id,
                    url = resultWorkItem.Url, // This URL is the API URL, not the web UI URL.
                    message = $"Successfully created work item {resultWorkItem.Id}."
                });
            }
            else
            {
                _logger.LogError("Failed to create work item in project {ProjectName}. The result was null or did not contain an ID.", projectName);
                return JsonSerializer.Serialize(new { error = "Failed to create work item. Result was null or ID was missing." });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating work item in project {ProjectName}: {Message}", projectName, ex.Message);
            // Check for specific VssServiceException details if available
            if (ex is VssServiceException vssEx)
            {
                 // Using properties available on VssServiceException. 
                 // VssServiceException itself contains a message, and inner exceptions might have more details.
                 _logger.LogError("VSS Service Exception Details: {VssExceptionMessage}", vssEx.Message);
                 return JsonSerializer.Serialize(new { error = $"Error creating work item (VSS): {vssEx.Message}" });
            }
            return JsonSerializer.Serialize(new { error = $"Error creating work item: {ex.Message}" });
        }
    }
} 