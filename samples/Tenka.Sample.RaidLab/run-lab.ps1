#requires -version 5.1
param(
    [ValidateSet('Menu','Prepare','Verify','Compare','Baseline','Optimized','Test','Benchmark','PipelineBenchmark')]
    [string]$Mode = 'Menu',
    [ValidateSet('Small','Normal','Large')][string]$Size = 'Normal'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'Host\Tenka.Sample.RaidLab.Host.csproj'
$solution = Join-Path $root 'Tenka.Sample.RaidLab.sln'
$transcribing = $false
$code = 0
function Invoke-Dotnet([string[]]$Arguments) {
    Write-Host ('dotnet ' + ($Arguments -join ' ')) -ForegroundColor DarkGray
    $old = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $script:dotnet @Arguments 2>&1 | ForEach-Object { Write-Host ([string]$_) }
        $exit = $LASTEXITCODE
    } finally { $ErrorActionPreference = $old }
    if ($exit -ne 0) { throw "dotnet処理に失敗しました（終了コード $exit）。最初のerrorとログを確認してください。" }
}
function Build-Host {
    # Implicit restore is intentional. Never use --no-restore on a new sample.
    Invoke-Dotnet @('build', $project, '-c', 'Release', '--nologo', '--disable-build-servers')
}
try {
    [Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
    $OutputEncoding = [Console]::OutputEncoding
    Push-Location $root
    Write-Host 'RaidLab | 複数プロジェクトの実践用サンプル' -ForegroundColor Cyan
    Write-Host '戦闘ログ → JSON読み込み → 入力検査 → 集計 → ランキング → レポートを調査します。'
    Write-Host 'サンプル本体は外部のファイル・データベース・ネットワークを使いません。'
    Write-Host 'ビルド・パッケージ復元・テストツールは別です。復元はネット接続を使う場合があります。'
    if ($Mode -eq 'Menu') {
        Write-Host '  1  初回準備：6プロジェクトの復元 → 本体ビルド → 仕様テスト（推奨）'
        Write-Host '  2  仕様テストだけ（xUnitのパッケージ不要）'
        Write-Host '  3  ベースラインと改善候補の参考比較'
        Write-Host '  4  ベースラインを4秒動かす（単独実行。トレース取得ではありません）'
        Write-Host '  5  改善候補を4秒動かす（同上）'
        Write-Host '  6  xUnitを実行し、TRXレポートを保存'
        Write-Host '  7  集計処理をBenchmarkDotNetで比較'
        Write-Host '  8  JSON～レポート全体をBenchmarkDotNetで比較'
        Write-Host '  9  甜花ちゃんで読み込むソリューションの場所を表示'
        Write-Host '  0  終了'
        $choice = (Read-Host '番号 [Enter = 1]').Trim()
        if ($choice -eq '') { $choice = '1' }
        if ($choice -eq '0') { return }
        if ($choice -eq '9') {
            Write-Host $solution -ForegroundColor Green
            Write-Host '甜花ちゃんの「プロジェクト調査」→「準備」でも、RaidLabを選択できます。'
            return
        }
        $modes = @{'1'='Prepare';'2'='Verify';'3'='Compare';'4'='Baseline';'5'='Optimized';'6'='Test';'7'='Benchmark';'8'='PipelineBenchmark'}
        if (-not $modes.ContainsKey($choice)) { throw '1～9または0を選んでください。' }
        $Mode = $modes[$choice]
    }
    Write-Host '信頼するローカルコードを現在のユーザー権限でビルド・実行します。DockerやGitHub CIは使いません。'
    if ((Read-Host '処理を開始しますか？ [Y = 開始 / Enter = 中止]') -notmatch '^(y|yes)$') {
        Write-Host '処理は開始していません。'; return
    }
    $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    if (-not $command) { throw '.NET SDKが見つかりません。甜花ちゃん本体のsetup.batで準備してください。' }
    $script:dotnet = $command.Source
    $version = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or $version -notmatch '^(\d+)\.' -or [int]$Matches[1] -lt 8) {
        throw '.NET 8以降のSDKが必要です。global.jsonとインストール済みSDKを確認してください。'
    }
    $runtimes = & $dotnet --list-runtimes
    if ($LASTEXITCODE -ne 0 -or -not ($runtimes | Where-Object { $_ -match '^Microsoft.NETCore.App 8\.' })) {
        throw '.NET 8ランタイムが必要です。甜花ちゃんのsetup.batで.NET 8 SDKも準備してください。他のランタイムへ勝手に切り替えません。'
    }
    $artifacts = Join-Path $root 'artifacts'
    $logs = Join-Path $artifacts 'logs'
    New-Item -ItemType Directory -Force $logs | Out-Null
    $log = Join-Path $logs ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $PID + '.log')
    Start-Transcript -Path $log -Force | Out-Null
    $transcribing = $true
    $hostDll = Join-Path $root 'Host\bin\Release\net8.0\Tenka.Sample.RaidLab.Host.dll'
    $players = 200; $events = 6000
    if ($Size -eq 'Small') { $players = 20; $events = 500 }
    if ($Size -eq 'Large') { $players = 800; $events = 20000 }
    switch ($Mode) {
        'Prepare' {
            Invoke-Dotnet @('restore', $solution, '--nologo')
            Build-Host
            Invoke-Dotnet @($hostDll, '--verify')
        }
        'Verify' { Build-Host; Invoke-Dotnet @($hostDll, '--verify') }
        'Compare' {
            Build-Host
            Invoke-Dotnet @($hostDll,'--mode','compare','--scope','pipeline','--players',"$players",'--events',"$events")
        }
        { $_ -in 'Baseline','Optimized' } {
            Build-Host
            Invoke-Dotnet @($hostDll,'--mode',$Mode.ToLowerInvariant(),'--scope','pipeline','--players',"$players",'--events',"$events",'--seconds','4')
        }
        'Test' {
            Invoke-Dotnet @('test',(Join-Path $root 'Tests\Tenka.Sample.RaidLab.Tests.csproj'),'-c','Release','--logger','trx;LogFileName=raid-lab.trx','--results-directory',(Join-Path $artifacts 'tests'))
        }
        { $_ -in 'Benchmark','PipelineBenchmark' } {
            Write-Host 'ベンチマークは数分かかる場合があります。短いジョブの結果は予備測定です。'
            $filter = if ($Mode -eq 'Benchmark') { '*AnalyzerBenchmarks*' } else { '*PipelineBenchmarks*' }
            Invoke-Dotnet @('run','--project',(Join-Path $root 'Benchmarks\Tenka.Sample.RaidLab.Benchmarks.csproj'),'-c','Release','--','--filter',$filter,'--job','short','--exporters','FullJSON','--artifacts',(Join-Path $artifacts 'benchmarks'))
        }
    }
    Write-Host '選択した処理が完了しました。結果や確認できた範囲をログで確認してください。' -ForegroundColor Green
    Write-Host "甜花ちゃんで選ぶファイル: $solution"
    Write-Host '起動用プロジェクトは Tenka.Sample.RaidLab.Host です。集計の調査はApplication内から始めます。'
} catch {
    $code = 1
    Write-Host '停止しました。成功としては扱っていません。' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Yellow
    Write-Host 'NETSDK1004: パッケージ復元を実行してください。使用中DLL: 該当サンプルの実行終了を待ってください。'
} finally {
    if ($transcribing) { Stop-Transcript | Out-Null; Write-Host "ログ: $log" }
    Pop-Location
}
exit $code
