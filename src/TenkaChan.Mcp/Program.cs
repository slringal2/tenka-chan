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
    .WithRequestFilters(filters => filters.AddCallToolFilter(next =>
        async (RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await next(request, cancellationToken).ConfigureAwait(false);

                // Defensive: if the SDK ever surfaces a binding failure as a result rather than an exception.
                if (result.IsError == true && services is not null &&
                    result.Content.Count == 1 && result.Content[0] is TextContentBlock text &&
                    AgentToolErrorAdvice.IsGenericInvocationFailure(request.Params.Name, text.Text))
                {
                    var missing = MissingRequired(services, request.Params);
                    if (missing.Count > 0) return AgentToolErrorAdvice.Failure(request.Params.Name, missing);
                }

                return result;
            }
            catch (ArgumentException) when (services is not null)
            {
                // Argument binding failed. The SDK's own exception is not an McpException, so it would be
                // reported to the client as a generic "An error occurred invoking 'name'." with no detail.
                // If the cause is a missing/null required argument, name it from the tool's input schema.
                var missing = MissingRequired(services, request.Params);
                if (missing.Count == 0) throw;
                return AgentToolErrorAdvice.Failure(request.Params.Name, missing);
            }
        }));

var app = builder.Build();
services = app.Services;
await app.RunAsync();

static IReadOnlyList<string> MissingRequired(IServiceProvider services, CallToolRequestParams request)
{
    var tool = services.GetServices<McpServerTool>()
        .FirstOrDefault(candidate => candidate.ProtocolTool.Name == request.Name);
    return tool is null
        ? Array.Empty<string>()
        : AgentToolErrorAdvice.MissingRequired(tool.ProtocolTool.InputSchema, request.Arguments);
}
