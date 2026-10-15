param([switch]$Restore)
$ErrorActionPreference = 'Stop'
Push-Location (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
try {
    Write-Host 'Stop Tenka and its MCP adapter before building.'
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK is required.' }
    $web = 'src/TenkaChan.Web/TenkaChan.Web.csproj'
    $tests = 'tests/TenkaChan.Tests/TenkaChan.Tests.csproj'
    if ($Restore) {
        foreach ($project in @($web, $tests)) {
            dotnet restore $project
            if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
        }
    }
    dotnet build $web -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Web/MCP build failed.' }
    dotnet test $tests -c Release --no-restore --filter 'FullyQualifiedName~SolutionExplorerTests|FullyQualifiedName~SourceExplorerIndexTests'
    if ($LASTEXITCODE -ne 0) { throw 'Solution explorer tests failed.' }
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js is required for UI tests only.' }
    node --test tests/Fixtures/organized-ui.test.cjs tests/Fixtures/project-explorer.test.cjs tests/Fixtures/common-shell.test.cjs
    if ($LASTEXITCODE -ne 0) { throw 'UI tests failed.' }
} finally { Pop-Location }
