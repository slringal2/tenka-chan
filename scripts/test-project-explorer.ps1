param([switch]$Restore)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    Write-Host 'Stop the running Tenka server before building (press Enter in its launch console).'
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required.' }
    $projects = @('src/TenkaChan.Web/TenkaChan.Web.csproj', 'tests/TenkaChan.Tests/TenkaChan.Tests.csproj')
    if ($Restore) {
        foreach ($project in $projects) {
            dotnet restore $project
            if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
        }
    }
    dotnet build $projects[0] -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Web build failed. Review the first error and any running Tenka process.' }
    dotnet test $projects[1] -c Release --no-restore --filter 'FullyQualifiedName~SourceExplorerIndexTests'
    if ($LASTEXITCODE -ne 0) { throw 'SourceExplorerIndexTests failed.' }
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js 18+ is needed for the UI tests, not for running Tenka.' }
    node --test tests/Fixtures/project-explorer.test.cjs tests/Fixtures/common-shell.test.cjs tests/Fixtures/dashboard-sync.test.cjs tests/Fixtures/path-picker.test.cjs
    if ($LASTEXITCODE -ne 0) { throw 'JavaScript regression tests failed.' }
    Write-Host 'Local checks completed. CI and Docker were not used.'
}
finally { Pop-Location }
