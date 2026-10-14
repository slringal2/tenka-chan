#requires -version 5.1
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    Write-Host 'GameLab: 1=結果の比較 / 2=正しさの検証 / 3=xUnit / 4=BenchmarkDotNet / 0=終了'
    Write-Host 'この教材のコードをローカルで実行します。初回は公式NuGetパッケージの復元を行う場合があります。'
    $choice = Read-Host '番号を入力 [Enter=1]'
    if ($choice -eq '0') { exit 0 }
    if ($choice -notin @('', '1', '2', '3', '4')) { throw '1～4 または 0 を選択してください。' }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDKが必要です。本体のsetup.batで準備してください。' }
    switch ($choice) {
        '2' { & dotnet run --project ./Tenka.Sample.GameLab.csproj -c Release -- --verify }
        '3' { & dotnet test ./Tests/Tenka.Sample.GameLab.Tests.csproj -c Release }
        '4' {
            Write-Host '通常は数分かかります。時間と割り当て量を比較します。Dockerは使用しません。'
            & dotnet run --project ./Benchmarks/Tenka.Sample.GameLab.Benchmarks.csproj -c Release -- --filter '*InventoryBenchmarks*' --job short --exporters FullJSON
        }
        default { & dotnet run --project ./Tenka.Sample.GameLab.csproj -c Release }
    }
    if ($LASTEXITCODE -ne 0) { throw "処理が失敗しました（終了コード $LASTEXITCODE）。上の.NETエラーを確認してください。" }
    Write-Host '処理が完了しました。比較結果が速くなることや、特定の命令になることを保証する教材ではありません。'
} catch { Write-Host $_.Exception.Message -ForegroundColor Yellow; exit 1 }
finally { Pop-Location }
