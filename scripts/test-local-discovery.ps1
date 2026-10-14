[CmdletBinding()]
param([switch]$NoRestore)
$ErrorActionPreference = "Stop"
$repository = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK が見つかりません。甜花ちゃん本体のビルドに必要な .NET 10 SDK をインストールしてください。"
}
Push-Location $repository
try {
    # This runs only the unconditional, read-only discovery regression tests.
    # It does not start Docker, GitHub Actions, generated experiments or a reviewed app.
    $testArguments = @(
        "test", "tests/TenkaChan.Tests/TenkaChan.Tests.csproj",
        "-c", "Release",
        "--filter", "FullyQualifiedName~TenkaChan.Tests.WorkspaceBinaryDiscoveryTests",
        "--logger", "console;verbosity=normal",
        "--logger", "trx;LogFileName=workspace-discovery.trx",
        "--results-directory", "artifacts/local-discovery-tests"
    )
    if ($NoRestore) { $testArguments += "--no-restore" }
    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) { throw "DLL 自動検出のローカル検証に失敗しました (exit $LASTEXITCODE)。" }
}
finally { Pop-Location }
