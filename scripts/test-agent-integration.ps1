param([switch]$Restore)
$ErrorActionPreference = 'Stop'
Push-Location (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
try {
    Write-Host 'Stop Tenka AND its external MCP adapter before rebuilding source-tree DLLs.'
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required.' }
    $testProject = 'tests/TenkaChan.Tests/TenkaChan.Tests.csproj'
    if ($Restore) {
        dotnet restore $testProject
        if ($LASTEXITCODE -ne 0) { throw 'Test restore failed.' }
        dotnet restore src/TenkaChan.Web/TenkaChan.Web.csproj
        if ($LASTEXITCODE -ne 0) { throw 'Web restore failed.' }
    }
    dotnet build src/TenkaChan.Web/TenkaChan.Web.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Web/MCP build failed.' }
    dotnet test $testProject -c Release --no-restore --filter 'FullyQualifiedName~AgentJournalTests|FullyQualifiedName~AgentConnectionTests'
    if ($LASTEXITCODE -ne 0) { throw 'Agent regression tests failed.' }
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js is needed for UI tests only.' }
    node --test tests/Fixtures/agent-monitor.test.cjs tests/Fixtures/common-shell.test.cjs tests/Fixtures/dashboard-sync.test.cjs tests/Fixtures/path-picker.test.cjs tests/Fixtures/project-explorer.test.cjs
    if ($LASTEXITCODE -ne 0) { throw 'UI regression tests failed.' }
    Write-Host 'Local checks completed. Docker and GitHub CI were not used.'
} finally { Pop-Location }
