#requires -version 5.1
param(
    [ValidateSet('Menu','Environment','Build','Connection')][string]$Mode = 'Menu',
    [switch]$ApproveBuild,
    [switch]$Restore
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$steps = New-Object 'Collections.Generic.List[object]'
$exitCode = 1
$reportDir = $null
$pushed = $false
$started = [DateTimeOffset]::UtcNow.ToString('O')
function Step([string]$name,[string]$status,[string]$note,[string]$log = '') {
    $steps.Add([pscustomobject]@{ name=$name; status=$status; note=$note; log=$log })
    Write-Host "[$status] $name : $note"
}
function Invoke-Checked([string]$Name,[string]$Exe,[string[]]$Arguments) {
    $log = Join-Path $reportDir ($Name + '.log')
    $old = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = -1
        & $Exe @Arguments 2>&1 | ForEach-Object {
            $line = [string]$_
            Write-Host $line
            Add-Content -LiteralPath $log -Value $line -Encoding UTF8
        }
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $old }
    if ($code -ne 0) {
        Step $Name 'failed' "終了コード $code。以降の必要な工程は実行しません。" $log
        throw "$Name に失敗しました。$log の最初のエラーを確認してください。"
    }
    Step $Name 'passed' 'コマンドが正常終了しました。' $log
}
function Assert-Unlocked {
    foreach ($relative in @(
        'src\TenkaChan.Web\bin\Release\net10.0\TenkaChan.Web.dll',
        'src\TenkaChan.Web\bin\Release\net10.0\TenkaChan.Mcp.dll',
        'src\TenkaChan.Mcp\bin\Release\net10.0\TenkaChan.Mcp.dll')) {
        $file = Join-Path $repo $relative
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
        $stream = $null
        try { $stream = [IO.File]::Open($file,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
        catch { throw "DLLが使用中、または書込権限がありません。甜花ちゃんと外部MCPアダプターを終了してください。自動終了はしません: $file" }
        finally { if ($null -ne $stream) { $stream.Dispose() } }
    }
    Step 'output-locks' 'passed' '検査時点で対象DLLに排他アクセスできました。以後の競合までは防ぎません。'
}
try {
    if ($env:OS -ne 'Windows_NT') { throw 'このBAT用検証はWindows向けです。各テストは個別にも実行できます。' }
    if (-not (Test-Path -LiteralPath (Join-Path $repo 'TenkaChan.slnx'))) { throw 'リポジトリのルートを確認してください。' }
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $key = ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($repo.ToLowerInvariant())))).Replace('-','').Substring(0,16) }
    finally { $hash.Dispose() }
    $launcherHome = Join-Path $env:LOCALAPPDATA ("TenkaChan\launcher\" + $key)
    $reportDir = Join-Path $launcherHome ('checks\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $PID)
    New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
    Push-Location $repo
    $pushed = $true
    Write-Host '甜花ちゃんのローカル検証'
    Write-Host 'CI・Docker・外部LLMは起動しません。ソースの更新・リセット・履歴削除も行いません。'
    if ($Mode -eq 'Menu') {
        Write-Host '  1  SDK・ランタイムの確認だけ'
        Write-Host '  2  本体ビルド＋対象のC#回帰テスト（先に甜花ちゃんとMCPを終了）'
        Write-Host '  3  起動中の甜花ちゃんとMCPの接続確認（先にstart.batで起動）'
        $choice = (Read-Host '番号を選択してください [Enter = 1]').Trim()
        $Mode = switch ($choice) { '' { 'Environment' } '1' { 'Environment' } '2' { 'Build' } '3' { 'Connection' } default { throw '1～3を選んで再実行してください。' } }
    }
    $dotnet = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue
    if (-not $dotnet) { throw '.NET SDKが見つかりません。setup.batで準備してから再実行してください。' }
    Invoke-Checked -Name 'sdk' -Exe $dotnet.Source -Arguments @('--version')
    $version = (Get-Content -LiteralPath (Join-Path $reportDir 'sdk.log') | Where-Object { $_ -match '^\d+\.\d+\.\d+' } | Select-Object -Last 1)
    if ($version -notmatch '^10\.0\.\d+$') { throw 'このチェックアウトには.NET 10 SDKが必要です。global.jsonとsetup.batを確認してください。' }
    Invoke-Checked -Name 'runtimes' -Exe $dotnet.Source -Arguments @('--list-runtimes')
    if ($Mode -eq 'Build') {
        if (-not $ApproveBuild) {
            Write-Host '本体のMSBuild処理とテストコードを現在のユーザー権限で実行します。信頼するソースでのみ実行してください。'
            if ((Read-Host 'ビルドと回帰テストを実行しますか？ [Y / Enter = 中止]') -notmatch '^(y|yes)$') {
                Step 'build-consent' 'cancelled' 'ビルド・テストは開始していません。'; throw '実行を取り消しました。'
            }
            if (-not $Restore) { $Restore = (Read-Host '不足パッケージの復元を許可しますか？ [Y / Enter = 復元しない]') -match '^(y|yes)$' }
        }
        Assert-Unlocked
        $env:TENKA_ENABLE_DOCKER = '0'
        $env:TENKA_ENABLE_LOCAL_EXEC = '0'
        $env:TENKA_ENABLE_UNSANDBOXED_EXPERIMENTS = '0'
        $web = 'src/TenkaChan.Web/TenkaChan.Web.csproj'
        $tests = 'tests/TenkaChan.Tests/TenkaChan.Tests.csproj'
        if ($Restore) {
            Invoke-Checked -Name 'restore-web' -Exe $dotnet.Source -Arguments @('restore',$web,'--nologo')
            Invoke-Checked -Name 'restore-tests' -Exe $dotnet.Source -Arguments @('restore',$tests,'--nologo')
        } else { Step 'restore' 'not_requested' '復元しません。NETSDK1004などの場合は復元を許可して再実行してください。' }
        Invoke-Checked -Name 'build-web' -Exe $dotnet.Source -Arguments @('build',$web,'-c','Release','--no-restore','--nologo')
        $filter = 'FullyQualifiedName~ExperimentFrameworkPolicyTests|FullyQualifiedName~StandardLibraryDiscoveryTests|FullyQualifiedName~ExperimentStoreTests|FullyQualifiedName~WorkspaceBinaryDiscoveryTests|FullyQualifiedName~ReviewQueuePagingTests|FullyQualifiedName~ReviewCacheTests|FullyQualifiedName~DashboardAgentClientFailureTests|FullyQualifiedName~AgentWorkflowDefaultsTests|FullyQualifiedName~AgentSessionLifecycleTests|FullyQualifiedName~AgentSessionClientTests|FullyQualifiedName~AgentToolContractTests|FullyQualifiedName~ReviewSelectionBridgeTests'
        Invoke-Checked -Name 'csharp-regression' -Exe $dotnet.Source -Arguments @('test',$tests,'-c','Release','--no-restore','--filter',$filter,'--logger','trx;LogFileName=regression.trx','--results-directory',$reportDir)
        $trx = Join-Path $reportDir 'regression.trx'
        if (-not (Test-Path -LiteralPath $trx)) { throw 'テスト結果TRXがありません。成功とは扱いません。' }
        $settings = New-Object Xml.XmlReaderSettings
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $settings.MaxCharactersInDocument = 10000000
        $reader = [Xml.XmlReader]::Create($trx,$settings)
        try { $doc = New-Object Xml.XmlDocument; $doc.XmlResolver = $null; $doc.Load($reader) }
        finally { $reader.Dispose() }
        $counters = $doc.SelectSingleNode("//*[local-name()='Counters']")
        if ($null -eq $counters -or [int]$counters.total -le 0 -or [int]$counters.failed -ne 0 -or [int]$counters.passed -ne [int]$counters.total) {
            throw 'テスト未実行・失敗・スキップがあります。TRXを確認してください。全件成功とは扱いません。'
        }
        Step 'test-results' 'passed' ("選択したC#回帰テスト " + $counters.total + '件が全件成功しました。対象アプリの実行検証とは別です。') $trx
    }
    if ($Mode -eq 'Connection') {
        $connection = if ($env:TENKA_AGENT_CONNECTION_FILE) { $env:TENKA_AGENT_CONNECTION_FILE } else { Join-Path $launcherHome 'agent-connection.json' }
        $adapter = Join-Path $repo 'src\TenkaChan.Mcp\bin\Release\net10.0\TenkaChan.Mcp.dll'
        if (-not (Test-Path -LiteralPath $connection)) { throw 'この本体の接続ファイルがありません。start.batで起動してから再実行してください。' }
        $python = Get-Command python.exe -CommandType Application -ErrorAction SilentlyContinue
        $prefix = @()
        if (-not $python) { $python = Get-Command py.exe -CommandType Application -ErrorAction SilentlyContinue; $prefix = @('-3') }
        if (-not $python) { throw '接続検査だけはPython 3.10以降が必要です。通常の甜花ちゃん利用には不要です。自動インストールはしません。' }
        Invoke-Checked -Name 'python-version' -Exe $python.Source -Arguments ($prefix + @('-c','import sys; sys.exit(0 if sys.version_info >= (3,10) else 2)'))
        Invoke-Checked -Name 'mcp-binding' -Exe $python.Source -Arguments ($prefix + @('tests/Fixtures/agent_binding_probe.py','--adapter-dll',$adapter,'--dotnet',$dotnet.Source))
        Write-Host '接続・状態取得・テスト用報告・終了通知を確認します。対象プロジェクトの変更やビルド・実行は行いません。'
        Invoke-Checked -Name 'mcp-connection' -Exe $python.Source -Arguments ($prefix + @('tests/Fixtures/agent_protocol_smoke.py','--connection-file',$connection,'--adapter-dll',$adapter))
    }
    $exitCode = 0
} catch {
    Step 'stopped' 'failed' $_.Exception.Message
} finally {
    if ($reportDir) {
        $result = [ordered]@{ schema=1; mode=$Mode; startedUtc=$started; completedUtc=[DateTimeOffset]::UtcNow.ToString('O'); exitCode=$exitCode;
            scope='選択した検査のみ。実ILSpy解析・性能改善・Windows全機能の完了を保証しません。'; steps=$steps.ToArray() }
        $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $reportDir 'summary.json') -Encoding UTF8
        Write-Host "結果の保存先: $reportDir"
        Write-Host 'ログにはソースのパス・テストの出力が含まれる場合があります。共有前に確認してください。接続トークンや全環境変数は収集しません。'
    }
    if ($pushed) { Pop-Location }
}
exit $exitCode
