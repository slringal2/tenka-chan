using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Review;

var builder = Host.CreateApplicationBuilder(args);
// stdout is reserved exclusively for the MCP stdio protocol.
builder.Logging.AddConsole(options =>
    options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<WorkspaceGuard>();
builder.Services.AddSingleton<RoslynReviewService>();
builder.Services.AddSingleton<IlInspector>();
builder.Services.AddSingleton<ExperimentStore>();
builder.Services.AddSingleton<BenchmarkRunComparer>();
builder.Services.AddSingleton<NativeExperimentComparer>();
builder.Services.AddSingleton<JitBinarySourceService>();
builder.Services.AddSingleton<PerformanceEvidenceService>();
builder.Services.AddSingleton<ReviewLedger>();
builder.Services.AddSingleton<ReviewRunStore>();
builder.Services.AddSingleton<IReviewContextSource, McpReviewContextSource>();
builder.Services.AddSingleton<IReviewReasoner, LocalLlmReviewReasoner>();
builder.Services.AddSingleton<AutonomousReviewRunner>();
builder.Services.AddSingleton<DockerExecutor>();
builder.Services.AddSingleton<TrustedLocalJitRunner>();
builder.Services.AddSingleton<TrustedLocalProcessProfiler>();
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
