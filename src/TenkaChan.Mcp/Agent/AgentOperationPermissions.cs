namespace TenkaChan.Mcp.Agent;

/// <summary>Host policy is permission to REQUEST an operation, not per-call consent.</summary>
public static class AgentOperationPermissions
{
    public static void RequireBuild(bool hostAllowsBuild, bool approved)
    {
        if (!hostAllowsBuild)
            throw new UnauthorizedAccessException("host_build_disabled: The server disallows agent build/open. Enable agentBuild in setup (TENKA_AGENT_ALLOW_BUILD on the server), then separately approve the invocation.");
        if (!approved)
            throw new UnauthorizedAccessException("approval_required: The server already permits agent build/open. This invocation has no approval. Obtain explicit client approval, then pass approved:true. Do not change the server settings for this error.");
    }
    public static void RequireExecution(bool hostAllowsExecution, bool approved)
    {
        if (!hostAllowsExecution)
            throw new UnauthorizedAccessException("host_execution_disabled: The server disallows agent execution. Enable the execution policy in setup (TENKA_AGENT_ALLOW_EXECUTION on the server), then separately approve the invocation.");
        if (!approved)
            throw new UnauthorizedAccessException("approval_required: The server already permits agent execution. This invocation has no approval. Obtain explicit client approval, then pass approved:true. Do not change the server settings for this error.");
    }
    public static object Describe(bool hostAllowsBuild, bool hostAllowsExecution, bool trustMsBuild, bool localExecution) => new
    {
        // Compatibility aliases: explicitly labelled as gateway switches below.
        build = hostAllowsBuild, execution = hostAllowsExecution,
        hostPolicy = new { allowBuild = hostAllowsBuild, allowExecution = hostAllowsExecution },
        invocation = new
        {
            approvalRequired = true, approvalArgument = "approved", canExecuteWithoutApproval = false,
            toolsRequiringApproval = new[] { "tenka_open_project", "tenka_build", "tenka_jit", "tenka_trace", "tenka_run_companion", "tenka_run_experiment", "tenka_session_end" }
        },
        localPrerequisites = new { trustedMsBuild = trustMsBuild, localExecutionEnabled = localExecution },
        executionIsSandboxed = false,
        note = "build/execution are host-policy switches, NOT approval of an invocation or a guarantee that all local prerequisites are ready. Obtain explicit approval and pass approved:true for each operation listed above. Generated host experiments run as the current OS user, not in a security sandbox."
    };
}
