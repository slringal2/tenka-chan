param([switch]$Restore)
$ErrorActionPreference = 'Stop'
Push-Location (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
try {
    Write-Host '先に甜花ちゃん本体とMCPアダプターを終了してください。CIは使用しません。'
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDKが必要です。' }
    if ($Restore) {
        foreach ($project in @('src/TenkaChan.Web/TenkaChan.Web.csproj', 'tests/TenkaChan.Tests/TenkaChan.Tests.csproj')) {
            dotnet restore $project
            if ($LASTEXITCODE -ne 0) { throw "復元に失敗しました: $project" }
        }
    }
    dotnet build src/TenkaChan.Web/TenkaChan.Web.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw '本体ビルドに失敗しました。最初のエラーを確認してください。' }
    dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ReviewProgressTests|FullyQualifiedName~ReviewSourceCatalogTests|FullyQualifiedName~ReviewAttributionTests'
    if ($LASTEXITCODE -ne 0) { throw '確認記録のC#テストに失敗しました。' }
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw '画面の開発テストにはNode.jsが必要です。通常利用には不要です。' }
    $uiTests = @(Get-ChildItem tests/Fixtures -Filter '*.test.cjs' | ForEach-Object { $_.FullName })
    node --test @uiTests
    if ($LASTEXITCODE -ne 0) { throw 'JavaScriptのテストに失敗しました。' }
    Write-Host 'ローカルテストが完了しました。CIとDockerは使用していません。'
} finally { Pop-Location }
