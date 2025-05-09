using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Logging;

namespace AzureMcpAgents.Functions.Tools;

public class SayHelloTool
{
    private readonly ILogger<SayHelloTool> _logger;

    public SayHelloTool(ILogger<SayHelloTool> logger)
    {
        _logger = logger;
    }
    
    [Function("SayHello")]
    public string Run([McpToolTrigger("say_hello", "Simple hello world MCP Tool that responses with a hello message.")] ToolInvocationContext context)
    {
        _logger.LogInformation("Saying hello");
        return "Hello Azure Global Lisboa 2025!";
    }
}