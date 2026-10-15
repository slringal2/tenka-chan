param([switch]$Restore)
$ErrorActionPreference = 'Stop'
Push-Location (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
try {
    Write-Host 'Stop Tenka AND its external MCP adapter before rebuilding their loaded DLLs.'
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required. Use setup.bat to prepare it.' }
    $web = 'src/TenkaChan.Web/TenkaChan.Web.csproj'
    $tests = 'tests/TenkaChan.Tests/TenkaChan.Tests.csproj'
    if ($Restore) {
        foreach ($project in @($web, $tests)) {
            dotnet restore $project
            if ($LASTEXITCODE -ne 0) { throw "Package restore failed: $project" }
        }
    }
    dotnet build $web -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Web/MCP build failed. Read the first error; do not run a stale binary.' }
    $filter = 'FullyQualifiedName~ReviewQueuePagingTests|FullyQualifiedName~ReviewCacheTests|FullyQualifiedName~StandardLibraryDiscoveryTests|FullyQualifiedName~ReviewProgressTests|FullyQualifiedName~WorkspaceBinaryDiscoveryTests'
    dotnet test $tests -c Release --no-restore --filter $filter --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) { throw 'C# regression tests failed.' }
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js 18+ is needed for the UI development tests only, not for using Tenka.' }
    node --test tests/Fixtures/review-progress.test.cjs tests/Fixtures/large-review.test.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Review UI tests failed.' }
    Write-Host 'Local checks completed. No CI, model inference, target program or Docker was started.'
} finally { Pop-Location }
