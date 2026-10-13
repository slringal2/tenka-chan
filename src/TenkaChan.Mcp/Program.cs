using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;

var builder = Host.CreateApplicationBuilder(args);
// stdout is reserved exclusively for the MCP stdio protocol.
builder.Logging.AddConsole(options =>
    options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<WorkspaceGuard>();
builder.Services.AddSingleton<RoslynReviewService>();
builder.Services.AddSingleton<ExperimentStore>();
builder.Services.AddSingleton<DockerExecutor>();
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

// Register MSBuild before any MSBuildWorkspace is constructed.
if (Environment.GetEnvironmentVariable("TENKA_TRUST_MSBUILD") == "1"
    && !MSBuildLocator.IsRegistered)
{
    MSBuildLocator.RegisterDefaults();
}

await builder.Build().RunAsync();
