using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TenkaChan.Mcp.Agent;

// External LLM clients own inference and investigation planning. This MCP process
// only forwards tools to the running dashboard, so all clients see the SAME workspace.
var options = DashboardAgentOptions.Parse(args);
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddConsole(logging => logging.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<DashboardAgentClient>();

IServiceProvider? services = null;

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DashboardAgentTools>()
    .WithTools<ReviewProgressTools>()
    .WithTools<AgentWorkflowTools>()
    .WithTools<InvestigationTools>()
    .WithRequestFilters(filters => filters.AddCallToolFilter(next =>
        async (RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken) =>
        {
            // Validate known argument types before any tool can execute.
            // A nullable .NET binding exception is otherwise rendered as a generic
            // MCP error, and the actual invalid field is visible only in stderr.
            if (services is not null && ValidateArguments(services, request.Params) is { } failure)
                return failure;
            try
            {
                var result = await next(request, cancellationToken).ConfigureAwait(false);
                if (result.IsError == true && services is not null &&
                    result.Content.Count == 1 && result.Content[0] is TextContentBlock text &&
                    AgentToolErrorAdvice.IsGenericInvocationFailure(request.Params.Name, text.Text))
                    return ValidateArguments(services, request.Params) ?? result;
                return result;
            }
            catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
            {
                // Only classify a failure as an input error if the advertised
                // schema independently proves it. Do not hide genuine tool errors.
                if (services is not null && ValidateArguments(services, request.Params) is { } inputFailure)
                    return inputFailure;
                throw;
            }
        }));

var app = builder.Build();
services = app.Services;
await app.RunAsync();

static CallToolResult? ValidateArguments(IServiceProvider services, CallToolRequestParams request)
{
    var tool = services.GetServices<McpServerTool>()
        .FirstOrDefault(candidate => candidate.ProtocolTool.Name == request.Name);
    if (tool is null) return null;
    var missing = AgentToolErrorAdvice.MissingRequired(tool.ProtocolTool.InputSchema, request.Arguments);
    if (missing.Count > 0) return AgentToolErrorAdvice.Failure(request.Name, missing);
    var invalid = AgentToolErrorAdvice.InvalidTypes(tool.ProtocolTool.InputSchema, request.Arguments);
    return invalid.Count > 0 ? AgentToolErrorAdvice.TypeFailure(request.Name, invalid) : null;
}
