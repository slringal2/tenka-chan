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
    Write-Warning "dotnet-traceは未検出です。必要になったら専用画面の案内に従ってください。静的・IL・JITは使用できます。"
}

Write-Host "Tenka-chan: http://127.0.0.1:5077"
Write-Host "許可済みフォルダー: $env:TENKA_ALLOWED_ROOTS"
Write-Host "Dockerは無効です。ローカル実行・生成実験は隔離なし、毎回承認が必要です。"
Write-Host "ビルド済みDLLとJIT対象メソッドはプロジェクト読み込み後に自動設定されます。"
Push-Location $repository
try {
    dotnet run --project "src/TenkaChan.Web/TenkaChan.Web.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "甜花ちゃんの起動に失敗しました。上の.NETエラーを確認してください。" }
}
finally { Pop-Location }
