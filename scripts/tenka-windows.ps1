#requires -version 5.1
param([ValidateSet('Setup','Start')][string]$Mode = 'Setup', [switch]$VerifyStartup)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$web = Join-Path $repo 'src\TenkaChan.Web\TenkaChan.Web.csproj'
$sample = Join-Path $repo 'samples\Tenka.Sample.GameLab\Tenka.Sample.GameLab.csproj'
$legacySample = Join-Path $repo 'tests\Fixtures\WorkspaceBuildSample\WorkspaceBuildSample.csproj'
$stateRoot = Join-Path $env:LOCALAPPDATA 'TenkaChan\launcher'
$hash = [Security.Cryptography.SHA256]::Create()
try { $key = ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($repo.ToLowerInvariant())))).Replace('-','').Substring(0,16) }
finally { $hash.Dispose() }
$launcherHome = Join-Path $stateRoot $key
$settingsFile = Join-Path $launcherHome 'settings.json'
$logs = Join-Path $launcherHome 'logs'
$server = $null
$verifiedInstanceId = $null
$transcribing = $false
$exitCode = 0

function Say([string]$text) { Write-Host $text -ForegroundColor Cyan }
function Ask([string]$text, [bool]$defaultYes = $true) {
    $hint = if ($defaultYes) { '[Enter = はい / N = いいえ]' } else { '[Y = はい / Enter = いいえ]' }
    while ($true) {
        $value = (Read-Host "$text $hint").Trim()
        if ($value -eq '') { return $defaultYes }
        if ($value -match '^(y|yes)$') { return $true }
        if ($value -match '^(n|no)$') { return $false }
        Write-Host 'Y または N を入力してください。' -ForegroundColor Yellow
    }
}
function Invoke-Tool([string]$exe, [string[]]$arguments, [string]$title) {
    Say $title
    $old = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = -1
        & $exe @arguments 2>&1 | ForEach-Object { Write-Host ([string]$_) }
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $old }
    return ($code -eq 0)
}
function Find-Dotnet {
    $candidates = @()
    foreach ($root in @($env:ProgramW6432, $env:ProgramFiles)) {
        if ($root) { $candidates += Join-Path $root 'dotnet\dotnet.exe' }
    }
    $found = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($found) { $candidates += $found.Source }
    $candidates += Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $old = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $sdks = & $candidate --list-sdks 2>&1
            if ($LASTEXITCODE -eq 0 -and ($sdks | Where-Object { [string]$_ -match '^10\.0\.\d+\s' })) { return $candidate }
        } finally { $ErrorActionPreference = $old }
    }
    return $null
}
function Install-Sdk([string]$major) {
    Say ".NET $major SDKを準備します。Microsoftのパッケージを使用します。"
    Write-Host 'インストールの確認画面（UAC）が表示されることがあります。会社PCでは管理者に確認してください。'
    Write-Host '既存のSDKは削除しません。WinGetのソースとパッケージの利用条件への同意が必要です。'
    if (-not (Ask 'ダウンロード・インストールを進めますか？')) { return $false }
    $winget = Get-Command winget.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($winget) {
        if (Invoke-Tool $winget.Source @('install','--id',"Microsoft.DotNet.SDK.$major",'--exact','--source','winget','--accept-source-agreements','--accept-package-agreements') ".NET $major SDKをインストール中…") { return $true }
        Write-Warning '自動インストールが完了しませんでした。既存SDKが登録されている場合もあるため、再確認します。'
    } else { Write-Warning 'WinGetが利用できないため、公式ダウンロードページからの導入をご案内します。' }
    Write-Host "公式ページ: https://dotnet.microsoft.com/ja-jp/download/dotnet/$major.0"
    Write-Host '「SDK」のWindows用インストーラーを選んでください。Runtimeだけではビルドできません。'
    if (Ask '公式ページをブラウザーで開きますか？') { Start-Process "https://dotnet.microsoft.com/ja-jp/download/dotnet/$major.0" }
    [void](Read-Host 'インストールが終わったら、この画面に戻ってEnterを押してください')
    return $true
}
function Require-Root([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path) -or $path.StartsWith('\\') -or $path.Contains(';')) {
        throw '解析フォルダーにはローカルの通常のフォルダーを選んでください（ネットワークパス・区切り文字は使用できません）。'
    }
    $full = (Get-Item -LiteralPath $path -Force).FullName
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw '選択した解析フォルダーが見つかりません。setup.batで選び直してください。' }
    $current = Get-Item -LiteralPath $full -Force
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'リンク・ジャンクション経由ではなく、元のフォルダーを選んでください。' }
        $current = $current.Parent
    }
    return $full
}
function Choose-Workspace {
    Add-Type -AssemblyName System.Windows.Forms
    while ($true) {
        Say 'どのコードを解析しますか？'
        Write-Host '  1  自分のプロジェクトが入ったフォルダーを選ぶ'
        Write-Host '  2  まず付属サンプルで試す（設定が分からない場合はこちら）'
        $choice = (Read-Host '番号を入力してください [Enter = 1]').Trim()
        if ($choice -eq '2') { return [pscustomobject]@{ root = $repo; project = $sample } }
        if ($choice -notin @('','1')) { continue }
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = '信頼できるプロジェクト全体が入ったフォルダーを選択してください。C:\ 全体は選ばないでください。'
        $dialog.ShowNewFolderButton = $false
        try {
            if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { Write-Host '選択を取り消しました。'; continue }
            $root = Require-Root $dialog.SelectedPath
        } finally { $dialog.Dispose() }
        if ($root -eq [IO.Path]::GetPathRoot($root)) { Write-Warning 'ドライブ全体ではなく、プロジェクト用フォルダーを選び直してください。'; continue }
        Write-Host "選択したフォルダー: $root"
        Write-Host 'プロジェクトを読み込むとMSBuildの独自処理が動く場合があります。ご自身が信頼するコードだけで利用してください。'
        if (-not (Ask 'このフォルダーを解析の許可範囲にしますか？')) { continue }
        $files = @(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Extension -in @('.sln','.slnx','.csproj') })
        $project = if ($files.Count -eq 1) { $files[0].FullName } else { '' }
        return [pscustomobject]@{ root = $root; project = $project }
    }
}
function Assert-BuildOutputsAvailable {
    $locked = @()
    foreach ($file in @(
        (Join-Path $repo 'src\TenkaChan.Web\bin\Release\net10.0\TenkaChan.Web.dll'),
        (Join-Path $repo 'src\TenkaChan.Web\bin\Release\net10.0\TenkaChan.Mcp.dll'),
        (Join-Path $repo 'src\TenkaChan.Mcp\bin\Release\net10.0\TenkaChan.Mcp.dll'))) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
        $stream = $null
        try {
            $stream = [System.IO.File]::Open($file, [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        } catch [System.IO.IOException] { $locked += $file }
        catch [System.UnauthorizedAccessException] {
            throw "DLLのアクセス権を確認してください: $file"
        } finally { if ($null -ne $stream) { $stream.Dispose() } }
    }
    if ($locked.Count -eq 0) { return }
    Write-Warning 'DLLが使用中のためビルドを開始しません。'
    foreach ($file in $locked) { Write-Host "使用中: $file" -ForegroundColor Yellow }
    Write-Host '先に起動した甜花ちゃんの黒い画面でEnterを押し、終了してから再起動してください。'
    Write-Host 'ほかのdotnetやアプリは自動終了しません。'
    throw '使用中のDLLを解放してからsetup.bat/start.batを再実行してください。'
}
function Quote-Argument([string]$value) {
    # Windows CreateProcess quoting; never uses cmd.exe or Invoke-Expression.
    return '"' + [regex]::Replace([regex]::Replace($value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
function Find-Port([int]$preferred) {
    if ($preferred -lt 1024 -or $preferred -gt 65515) { $preferred = 5077 }
    for ($port = $preferred; $port -lt ($preferred + 20); $port++) {
        $probe = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, $port)
        try { $probe.Start(); return $port } catch [Net.Sockets.SocketException] { } finally { $probe.Stop() }
    }
    throw '空いているポートが見つかりません。他の甜花ちゃんが起動していないか確認してください。既存プロセスは停止していません。'
}

try {
    if ($VerifyStartup -and $Mode -ne 'Start') {
        throw '実起動検証は -Mode Start -VerifyStartup で指定してください。setup.bat での初回設定を先に完了してください。'
    }
    if ($env:OS -ne 'Windows_NT') { throw 'このセットアップはWindows専用です。' }
    [Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
    $OutputEncoding = [Console]::OutputEncoding
    $Host.UI.RawUI.WindowTitle = '甜花ちゃん | セットアップと起動'
    New-Item -ItemType Directory -Force $logs | Out-Null
    $log = Join-Path $logs ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $PID + '.log')
    Start-Transcript -Path $log -Force | Out-Null
    $transcribing = $true
    Say '================================================'
    Say '  甜花ちゃんへようこそ'
    Say '================================================'
    Write-Host 'Docker・Node.js・有料AI契約は不要です。CIやGitの設定は変更しません。'
    Write-Host "本体フォルダー: $repo"
    Write-Host "今回のログ: $log"
    if (-not (Test-Path -LiteralPath $web)) { throw '本体ファイルがありません。ZIPは「すべて展開」し、setup.batをtenka-chanのルートフォルダーに置いてください。' }
    [void](Require-Root $repo)
    Push-Location $repo
    $settings = $null
    if (Test-Path -LiteralPath $settingsFile) {
        try {
            $settings = Get-Content -LiteralPath $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($settings.schema -ne 1) { throw 'unknown schema' }
        } catch {
            Write-Warning '保存設定を読めませんでした。元ファイルを残して初回設定をやり直します。'
            Copy-Item -LiteralPath $settingsFile -Destination ($settingsFile + '.backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
            $settings = $null
        }
    }
    if ($VerifyStartup -and $null -eq $settings) {
        throw '実起動検証には保存済み設定が必要です。最初に setup.bat を実行してください。検証は初回設定・ダウンロードを自動実行しません。'
    }
    $setup = $Mode -eq 'Setup' -or $null -eq $settings
    if ($setup) {
        Say '[1/5] 初回設定・修復'
        Write-Host '必要な.NET、NuGet依存関係、計測ツールを準備し、このリポジトリをビルドします。'
        Write-Host '外部ダウンロードを使用します。ローカルでMSBuildの処理が実行されます。'
        if (-not (Ask 'この甜花ちゃんのソースを信頼してセットアップを開始しますか？')) { throw 'セットアップを中止しました。' }
        if ($null -ne $settings) {
            Write-Host "前回の解析フォルダー: $($settings.workspaceRoot)"
            $reuse = Ask '前回と同じ解析フォルダーを使いますか？'
        } else { $reuse = $false }
        if (-not $reuse) {
            $choice = Choose-Workspace
            $settings = [pscustomobject]@{ schema = 1; workspaceRoot = $choice.root; defaultProject = $choice.project; port = 5077; generatedExperiments = $false }
        }
        Write-Host 'JIT・計測・ビルドはローカル実行です。生成xUnit／BenchmarkDotNetもDockerなしで使えますが、隔離はありません。'
        $settings.generatedExperiments = Ask '生成テスト・ベンチマークの「ホストで実行」を有効にしますか？（実行時にも毎回確認）'
    }
    # Migrate only the old built-in sample selection, not an operator's own project.
    if ($setup -and $settings.defaultProject -eq $legacySample) { $settings.defaultProject = $sample }
    foreach ($option in @('agentBuild', 'agentExecution')) {
        if (-not $settings.PSObject.Properties[$option]) { $settings | Add-Member -NotePropertyName $option -NotePropertyValue $false }
    }
    if ($setup) {
        Say '外部LLMとの連携（ブラウザーからモデルに接続しません）'
        Write-Host 'クラス・ソース・ILの調査は外部エージェントから利用できます。以下は追加で許可する操作です。'
        Write-Host '許可範囲のコードはユーザー権限で動きます。実行の承認は外部エージェント側でも必要です。'
        $settings.agentBuild = Ask '外部エージェントによるプロジェクト読み込み・ビルドを許可しますか？' ([bool]$settings.agentBuild)
        $settings.agentExecution = Ask '外部エージェントによるJIT・計測・テスト等のローカル実行を許可しますか？' ([bool]$settings.agentExecution)
    }
    $workspace = Require-Root ([string]$settings.workspaceRoot)
    Say '[2/5] .NETの確認'
    $dotnet = Find-Dotnet
    if (-not $dotnet -and $setup) { [void](Install-Sdk '10'); $dotnet = Find-Dotnet }
    if (-not $dotnet) { throw '.NET 10 SDKが見つかりません。setup.batを実行し、SDKのインストールを完了してください。' }
    $dotnetDir = Split-Path -Parent $dotnet
    $env:PATH = $dotnetDir + ';' + $env:PATH
    $env:DOTNET_ROOT = $dotnetDir
    $env:DOTNET_ROOT_X64 = $dotnetDir
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:TENKA_NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\packages'
    $version = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or $version -notmatch '^10\.0\.\d+$') { throw 'このフォルダーのglobal.jsonに対応する.NET 10 SDKを選択できません。表示されたSDKエラーを確認してください。' }
    Write-Host "使用するSDK: $version ($dotnet)"
    $runtimes = & $dotnet --list-runtimes
    if (-not ($runtimes | Where-Object { $_ -match '^Microsoft.NETCore.App 8\.' })) {
        if ($setup -and (Ask '.NET 8アプリのJIT・テスト実行用に.NET 8 SDKも準備しますか？')) { [void](Install-Sdk '8') }
        $runtimes = & $dotnet --list-runtimes
        if (-not ($runtimes | Where-Object { $_ -match '^Microsoft.NETCore.App 8\.' })) {
            Write-Warning '.NET 8ランタイムが未準備です。静的解析は使えますが、.NET 8のアプリ・生成実験は実行できません。'
        }
    }
    $env:TENKA_ALLOWED_ROOTS = (@($repo, $workspace) | Select-Object -Unique) -join ';'
    $env:TENKA_TRUST_MSBUILD = '1'
    $env:TENKA_ENABLE_DOCKER = '0'
    $env:TENKA_ENABLE_LOCAL_EXEC = '1'
    $env:TENKA_ENABLE_NATIVE_DIALOGS = '1'
    $env:TENKA_ENABLE_UNSANDBOXED_EXPERIMENTS = if ($settings.generatedExperiments -eq $true) { '1' } else { '0' }
    $env:TENKA_ENABLE_AGENT_GATEWAY = '1'
    $env:TENKA_AGENT_CONNECTION_FILE = Join-Path $launcherHome 'agent-connection.json'
    $env:TENKA_AGENT_ALLOW_BUILD = if ($settings.agentBuild -eq $true) { '1' } else { '0' }
    $env:TENKA_AGENT_ALLOW_EXECUTION = if ($settings.agentExecution -eq $true) { '1' } else { '0' }
    $toolDir = Join-Path $env:LOCALAPPDATA 'TenkaChan\tools'
    $trace = Join-Path $toolDir 'dotnet-trace.exe'
    $warnings = New-Object 'Collections.Generic.List[string]'
    Say '[3/5] 依存関係・計測ツールの準備'
    if ($setup) {
        if (-not (Invoke-Tool $dotnet @('restore',$web,'--nologo') '甜花ちゃん本体のパッケージを準備中…')) { throw '本体のパッケージ復元に失敗しました。ネット接続・プロキシ・NuGetエラーを確認し、setup.batを再実行してください。' }
        foreach ($spec in @(
            @{ path = 'tests\Fixtures\Net8XunitCache\Net8XunitCache.csproj'; name = 'xUnit' },
            @{ path = 'tests\Fixtures\BenchmarkCache\BenchmarkCache.csproj'; name = 'BenchmarkDotNet' }
        )) {
            if (-not (Invoke-Tool $dotnet @('restore',(Join-Path $repo $spec.path),'-p:TargetFramework=net8.0','-p:NuGetAudit=false') "$($spec.name)の.NET 8用キャッシュを準備中…")) { $warnings.Add("$($spec.name)のキャッシュが未準備です。ホスト実験を使う前にsetup.batを再実行してください。") }
        }
        if (-not (Test-Path -LiteralPath $trace)) {
            New-Item -ItemType Directory -Force $toolDir | Out-Null
            if (-not (Invoke-Tool $dotnet @('tool','install','--tool-path',$toolDir,'dotnet-trace','--version','9.0.661903','--allow-roll-forward') 'プロファイル計測ツールを準備中…')) { $warnings.Add('dotnet-traceの導入が未完了です。静的解析・JITは使えますが、トレース採取はまだ利用できません。') }
        }
    } else { Write-Host '通常起動ではダウンロード・パッケージ復元を行いません。修復はsetup.batから行えます。' }
    if (Test-Path -LiteralPath $trace) { $env:TENKA_DOTNET_TRACE_PATH = $trace }
    Say '[4/5] 本体のReleaseビルド'
    Assert-BuildOutputsAvailable
    if (-not (Invoke-Tool $dotnet @('build',$web,'-c','Release','--no-restore','--nologo','--disable-build-servers') '変更を確認してビルドしています…')) { throw '本体のビルドに失敗しました。CSから始まるエラーはC#のコンパイルエラーです。ソースと参照の整合性を確認してください。setup.batの再実行だけでは解決しない場合があります。NETSDK1004など依存関係の不足が原因の場合はsetup.batで復元してください。MSB3027/MSB3021の場合はDLLを使用中の甜花ちゃん・MCPアダプターを終了してください。判断できない場合はログの最初のerror行を共有してください。' }
    if ($setup -and $settings.defaultProject -eq $sample) {
        if (-not (Invoke-Tool $dotnet @('build',$sample,'-c','Release','--nologo') '比較教材GameLabをビルド中（実行はしません）…')) { $warnings.Add('付属サンプルのビルドは未完了です。ブラウザー上の「ビルドと解析の準備」からやり直せます。') }
    }
    $settings.workspaceRoot = $workspace
    $temp = $settingsFile + '.tmp'
    $settings | ConvertTo-Json | Set-Content -LiteralPath $temp -Encoding UTF8
    Move-Item -LiteralPath $temp -Destination $settingsFile -Force
    foreach ($warning in $warnings) { Write-Warning $warning }
    Say '[5/5] ブラウザーから利用する準備'
    Write-Host "設定の保存先: $settingsFile"
    Write-Host '次回はstart.batをダブルクリックするだけです。解析フォルダーの変更・修復はsetup.batで行えます。'
    Write-Host 'AIモデルの起動は必須ではありません。静的解析・IL/JIT・テストはLLMなしで利用できます。'
    if ($setup -and -not (Ask '今すぐ甜花ちゃんを起動しますか？')) { Say 'セットアップを保存しました。start.batで起動できます。' }
    else {
        $port = Find-Port ([int]$settings.port)
        if ($VerifyStartup -and $port -ne [int]$settings.port) {
            throw "検証を中止しました。設定済みポート$($settings.port)は使用中です。既存サーバーは停止せず、新しいポートへの切替も行いません。"
        }
        if ($port -ne [int]$settings.port) { Write-Warning "ポート$($settings.port)は使用中のため、今回は$portを使用します。既存プロセスは終了しません。" }
        $env:TENKA_WEB_PORT = [string]$port
        $mcpConfig = Join-Path $launcherHome 'mcp-config.json'
        $adapter = Join-Path $repo 'src\TenkaChan.Mcp\bin\Release\net10.0\TenkaChan.Mcp.dll'
        @{ mcpServers = @{ tenka = @{ command = $dotnet; args = @($adapter, '--connection-file', $env:TENKA_AGENT_CONNECTION_FILE) } } } |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $mcpConfig -Encoding UTF8
        Write-Host "外部エージェント側に登録するMCP設定例: $mcpConfig"
        Write-Host 'モデルURLやAPIキーを甜花ちゃんに登録する必要はありません。'

        $stdout = Join-Path $logs ('server-' + $PID + '.out.log')
        $stderr = Join-Path $logs ('server-' + $PID + '.err.log')
        $arguments = @('run','--project',$web,'-c','Release','--no-build','--no-restore','--no-launch-profile')
        $commandLine = ($arguments | ForEach-Object { Quote-Argument $_ }) -join ' '
        $server = Start-Process -FilePath $dotnet -ArgumentList $commandLine -WorkingDirectory $repo -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        $base = "http://127.0.0.1:$port"
        $ready = $false
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (-not $server.HasExited -and $watch.Elapsed.TotalSeconds -lt 120) {
            try {
                $session = Invoke-RestMethod -Uri ($base + '/api/session') -TimeoutSec 2
                if ($session.listening -eq 'loopback' -and $session.csrfToken -and [int]$session.port -eq $port) { $ready = $true; break }
            } catch { }
            Start-Sleep -Milliseconds 400
            $server.Refresh()
        }
        if (-not $ready) { throw "サーバーの起動を確認できませんでした。詳細ログ: $stdout / $stderr" }
        if ($VerifyStartup) {
            # Probe the ACTUAL server launched above. Never print its CSRF or
            # machine bearer token; the transcript must remain safe to share.
            $probe = Invoke-RestMethod -Uri ($base + '/api/session') -TimeoutSec 5
            if ($probe.listening -ne 'loopback' -or [int]$probe.port -ne $port -or
                [string]::IsNullOrWhiteSpace([string]$probe.csrfToken) -or
                $probe.msbuildEnabled -ne $true -or $probe.dockerEnabled -ne $false -or
                $probe.trustedLocalExecutionEnabled -ne $true -or
                $probe.hostExperimentsEnabled -ne ([bool]$settings.generatedExperiments)) {
                throw '検証したHTTPサーバーの設定が保存済みランチャー設定と一致しません。'
            }
            # The agent connection descriptor is published only after ASP.NET
            # startup. Its credential is read locally, never written to a log.
            $connectionFile = $env:TENKA_AGENT_CONNECTION_FILE
            $descriptor = $null
            # A previous forced shutdown may leave a stale descriptor. Match the
            # public live instanceId to the private local credential before use.
            for ($attempt = 0; $attempt -lt 20; $attempt++) {
                $server.Refresh()
                if ($server.HasExited) { break }
                try {
                    if (Test-Path -LiteralPath $connectionFile -PathType Leaf) {
                        $candidate = Get-Content -LiteralPath $connectionFile -Raw -Encoding UTF8 | ConvertFrom-Json
                        if ($candidate.schema -eq 1 -and $candidate.endpoint -eq $base -and
                            -not [string]::IsNullOrWhiteSpace([string]$candidate.instanceId) -and
                            [string]$candidate.token -match '^[a-fA-F0-9]{64}$') {
                            $monitor = Invoke-RestMethod -Uri ($base + '/api/agent/monitor') -TimeoutSec 2
                            if ($monitor.enabled -eq $true -and
                                [string]$monitor.instanceId -eq [string]$candidate.instanceId) {
                                $descriptor = $candidate
                                break
                            }
                        }
                    }
                } catch {
                    # The server may still be publishing its descriptor. Do not
                    # echo credentials or start a second process to recover.
                }
                Start-Sleep -Milliseconds 250
            }
            if ($null -eq $descriptor) {
                throw 'MCP接続ファイルと稼働サーバーの一致が確認できませんでした。古い接続情報やサーバーログを確認してください。'
            }
            $verifiedInstanceId = [string]$descriptor.instanceId
            $headers = @{ Authorization = ('Bearer ' + [string]$descriptor.token) }
            $probeSessionId = $null
            try {
                $started = Invoke-RestMethod -Uri ($base + '/agent/v1/begin') -Method Post `
                    -ContentType 'application/json' -Headers $headers `
                    -Body '{"clientName":"launcher-startup-probe","goal":"read-only connectivity check"}' -TimeoutSec 5
                $probeSessionId = [string]$started.id
                $parsedSessionId = [Guid]::Empty
                if (-not [Guid]::TryParse($probeSessionId, [ref]$parsedSessionId)) {
                    throw 'MCP接続テストでセッションIDを確認できませんでした。'
                }
            } finally {
                if (-not [string]::IsNullOrWhiteSpace($probeSessionId)) {
                    # Explicitly finish the test session; do not keep a slot occupied.
                    try {
                        $endBody = @{ sessionId = $probeSessionId } | ConvertTo-Json -Compress
                        [void](Invoke-RestMethod -Uri ($base + '/agent/v1/end') -Method Post `
                            -ContentType 'application/json' -Headers $headers -Body $endBody -TimeoutSec 5)
                    } catch { Write-Warning 'MCP検証セッションの終了を確認できませんでした。起動プロセスは終了します。' }
                }
            }
            $server.Refresh()
            if ($server.HasExited) { throw '検証中にサーバーが終了しました。ログを確認してください。' }
            Say "実起動検証に成功: HTTP / MCP認証 / ポート$port"
            Write-Host 'ブラウザー・外部モデル・解析対象アプリは起動していません。'
            Write-Host '検証で開始したWebプロセスはこのあと終了します。'
        } else {
            $url = $base + '/agent.html'
            Start-Process $url
            Say "起動しました: $url"
            Write-Host '外部エージェント側で調査を依頼してください。ブラウザーは活動と結果を表示します。手動の調査画面もサイドバーに残しています。'
            Write-Host 'ブラウザーを閉じてもサーバーは動作します。終了するときはこの画面でEnterを押してください。'
            Write-Host "サーバーログ: $stdout"
            while (-not $server.HasExited) {
                if ([Console]::KeyAvailable -and [Console]::ReadKey($true).Key -eq [ConsoleKey]::Enter) { break }
                Start-Sleep -Milliseconds 300
                $server.Refresh()
            }
            if ($server.HasExited -and $server.ExitCode -ne 0) { throw "サーバーが異常終了しました。ログ: $stderr" }
        }
    }
} catch {
    $exitCode = 1
    Write-Host ''
    Write-Host '途中で停止しました。成功としては扱っていません。' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Yellow
    Write-Host '画面はすぐ閉じません。必要ならこのメッセージをコピーしてください。'
    Write-Host "ログ保存フォルダー: $logs"
    Write-Host 'ソースコードやパスがログに含まれる場合があります。共有前に内容を確認してください。'
} finally {
    if ($null -ne $server) {
        try {
            if (-not $server.HasExited) {
                # Only terminate the exact process tree started by this launcher, never all dotnet processes.
                & "$env:SystemRoot\System32\taskkill.exe" /PID $server.Id /T /F 2>&1 | ForEach-Object { Write-Host ([string]$_) }
            }
        } catch { Write-Warning '起動したサーバーの終了を確認できませんでした。' }
        $server.Dispose()
    }
    if ($VerifyStartup -and -not [string]::IsNullOrWhiteSpace($verifiedInstanceId)) {
        # taskkill /F may not run ASP.NET ApplicationStopped callbacks.
        # Remove ONLY our own connection descriptor, never another instance's.
        try {
            $ownFile = $env:TENKA_AGENT_CONNECTION_FILE
            if (Test-Path -LiteralPath $ownFile -PathType Leaf) {
                $lastDescriptor = Get-Content -LiteralPath $ownFile -Raw -Encoding UTF8 | ConvertFrom-Json
                if ([string]$lastDescriptor.instanceId -eq $verifiedInstanceId) {
                    Remove-Item -LiteralPath $ownFile -Force
                }
            }
        } catch { Write-Warning '検証用MCP接続ファイルの後片付けを確認できませんでした。ファイルのinstanceIdを確認してください。' }
    }
    if ($transcribing) { Stop-Transcript | Out-Null }
    if ((Get-Location).Path -eq $repo) { Pop-Location }
}
exit $exitCode
