using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
builder.Services.AddSingleton<AgentSessionClient>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<DashboardAgentTools>().WithTools<ReviewProgressTools>();
await builder.Build().RunAsync();
