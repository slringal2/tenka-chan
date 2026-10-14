param(
    [string]$WorkspaceRoot = "",
    [string]$ModelEndpoint = "http://127.0.0.1:8080/v1/chat/completions",
    [string]$ModelName = "local"
)
$ErrorActionPreference = "Stop"
$repository = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) { $WorkspaceRoot = $repository }
if (-not (Test-Path $WorkspaceRoot -PathType Container)) {
    throw "指定フォルダーがありません: $WorkspaceRoot"
}
$env:TENKA_ALLOWED_ROOTS = (Resolve-Path $WorkspaceRoot).Path
$env:TENKA_TRUST_MSBUILD = "1"
$env:TENKA_ENABLE_DOCKER = "0"
$env:TENKA_ENABLE_LOCAL_EXEC = "1"
# UNSANDBOXED execution of generated xUnit / BDN remains separately confirmed per run.
$env:TENKA_ENABLE_UNSANDBOXED_EXPERIMENTS = "1"
$env:TENKA_LLM_ENDPOINT = $ModelEndpoint
$env:TENKA_LLM_MODEL = $ModelName
$env:TENKA_WEB_PORT = "5077"
$env:TENKA_NUGET_PACKAGES = Join-Path $env:USERPROFILE ".nuget\packages"

$localTrace = Join-Path $env:LOCALAPPDATA "TenkaChan\tools\dotnet-trace.exe"
$globalTrace = Join-Path $env:USERPROFILE ".dotnet\tools\dotnet-trace.exe"
if (Test-Path $localTrace) { $env:TENKA_DOTNET_TRACE_PATH = $localTrace }
elseif (Test-Path $globalTrace) { $env:TENKA_DOTNET_TRACE_PATH = $globalTrace }
else {
    Write-Host "dotnet-traceの初回セットアップを試します..."
    $toolDir = Join-Path $env:LOCALAPPDATA "TenkaChan\tools"
    New-Item -ItemType Directory -Force $toolDir | Out-Null
    dotnet tool install --tool-path $toolDir dotnet-trace --version 9.0.661903
    if (Test-Path $localTrace) { $env:TENKA_DOTNET_TRACE_PATH = $localTrace }
    else { Write-Warning "dotnet-trace未導入。ネットワークを確認してください。静的/JIT機能は使用できます。" }
}

Write-Host "Tenka-chan: http://127.0.0.1:5077"
Write-Host "許可済みフォルダー: $env:TENKA_ALLOWED_ROOTS"
Write-Host "Dockerは無効です。ローカル実行・生成実験は隔離なし、毎回承認が必要です。"
Write-Host "ビルド済みDLLとJIT対象メソッドはプロジェクト読み込み後に自動設定されます。"
Push-Location $repository
try {
    Write-Host "生成xUnitとBenchmarkDotNetの公式パッケージを準備します（初回だけダウンロードあり）。"
    dotnet restore "tests/Fixtures/Net8XunitCache/Net8XunitCache.csproj"
    if ($LASTEXITCODE -ne 0) { Write-Warning "xUnitキャッシュが未準備です。ネットワーク確認後に再起動してください。" }
    dotnet restore "tests/Fixtures/BenchmarkCache/BenchmarkCache.csproj" -p:TargetFramework=net8.0
    if ($LASTEXITCODE -ne 0) { Write-Warning "BenchmarkDotNetのnet8依存が未準備です。" }
    dotnet run --project "src/TenkaChan.Web/TenkaChan.Web.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "甜花ちゃんの起動に失敗しました。上の.NETエラーを確認してください。" }
}
finally { Pop-Location }
