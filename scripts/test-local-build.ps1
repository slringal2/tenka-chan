param([switch]$Restore)
$ErrorActionPreference = "Stop"
$repository = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Push-Location $repository
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw ".NET SDKが必要です。" }
    $testProject = "tests/TenkaChan.Tests/TenkaChan.Tests.csproj"
    if ($Restore) {
        dotnet restore $testProject
        if ($LASTEXITCODE -ne 0) { throw "テスト依存関係の復元に失敗しました。" }
    }
    dotnet test $testProject -c Release --no-restore --filter "FullyQualifiedName~WorkspaceBuildTests" --logger "console;verbosity=normal"
    if ($LASTEXITCODE -ne 0) { throw "C#ビルドまたはWorkspaceBuildTestsに失敗しました。" }
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw "画面側のテストにはNode.js 18以降が必要です。" }
    node --test tests/Fixtures/workspace-build-ui.test.cjs
    if ($LASTEXITCODE -ne 0) { throw "画面側のビルド手順テストに失敗しました。" }
    Write-Host "ローカルテストが完了しました。GitHub ActionsとDockerは使っていません。"
}
finally { Pop-Location }
