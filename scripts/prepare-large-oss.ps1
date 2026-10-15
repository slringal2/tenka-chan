#requires -version 5.1
param([ValidateSet('Menu','Fetch','Build','Smoke','Info')][string]$Mode = 'Menu')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lock = Get-Content -LiteralPath (Join-Path $repo 'samples\oss\ilspy.lock.json') -Raw -Encoding UTF8 | ConvertFrom-Json
# Fixed recipe: a modified manifest is not permission to clone or execute an arbitrary project.
if ($lock.schema -ne 1 -or $lock.commit -cne '5deb6fb4258a09ba0ff5f63fa6180b1f8d6fb6ad' -or
    $lock.repository -cne 'https://github.com/icsharpcode/ILSpy.git' -or $lock.destination -cne 'samples/External/ILSpy-10.0') {
    throw '取得レシピが想定と違います。処理は開始していません。'
}
$base = Join-Path $repo 'samples\External\ILSpy-10.0'
$source = Join-Path $base 'source'
$solution = Join-Path $base 'ILSpy.Analysis.slnx'
$entryRelative = 'ICSharpCode.ILSpyCmd\ICSharpCode.ILSpyCmd.csproj'
$projectFiles = @('ICSharpCode.Decompiler\ICSharpCode.Decompiler.csproj','ICSharpCode.ILSpyX\ICSharpCode.ILSpyX.csproj',$entryRelative)
$hooks = Join-Path $base 'empty-hooks'
$transcribing = $false; $pushed = $false; $code = 0
function Assert-RegularPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    for ($item = $full; $item; $item = [IO.Path]::GetDirectoryName($item)) {
        if (Test-Path -LiteralPath $item) {
            if (((Get-Item -LiteralPath $item -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "リンクやジャンクションの保存先は使用しません: $item"
            }
        }
    }
}
function Git-Lines([string[]]$Arguments) {
    $old = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $lines = @(& $script:git -c "core.hooksPath=$hooks" -c core.autocrlf=false -c core.fsmonitor=false -c protocol.file.allow=never @Arguments 2>&1 | ForEach-Object { [string]$_ })
        $exit = $LASTEXITCODE
    } finally { $ErrorActionPreference = $old }
    if ($exit -ne 0) { throw ('Git処理に失敗しました: ' + ($lines -join "`n")) }
    return $lines
}
function Invoke-Dotnet([string[]]$Arguments) {
    $old = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $script:dotnet @Arguments 2>&1 | ForEach-Object { Write-Host ([string]$_) }
        $exit = $LASTEXITCODE
    } finally { $ErrorActionPreference = $old }
    if ($exit -ne 0) { throw ".NETの処理が失敗しました（終了コード $exit）。最初のerrorを確認してください。" }
}
function Check-Checkout {
    Assert-RegularPath $source
    Assert-RegularPath (Join-Path $source '.git')
    if (-not (Test-Path -LiteralPath (Join-Path $source '.git') -PathType Container)) {
        throw '取得済みの専用Gitリポジトリがありません。最初に1を実行してください。'
    }
    $top = @(Git-Lines @('-C',$source,'rev-parse','--show-toplevel'))[-1]
    if ([IO.Path]::GetFullPath($top) -ine [IO.Path]::GetFullPath($source)) { throw '別のリポジトリを検出しました。何も上書きしません。' }
    $remote = @(Git-Lines @('-C',$source,'remote','get-url','origin'))[-1]
    if ($remote -cne $lock.repository) { throw '取得元が固定した公式リポジトリと違います。何も上書きしません。' }
    $head = @(Git-Lines @('-C',$source,'rev-parse','HEAD'))[-1]
    if ($head -cne $lock.commit) { throw 'HEADが固定コミットと違います。既存の編集やコミットを残して停止します。resetは行いません。' }
    return @(Git-Lines @('-C',$source,'status','--porcelain','--untracked-files=normal'))
}
function Write-AnalysisSolution {
    if (Test-Path -LiteralPath $solution) { return } # Never replace an operator-edited solution.
    foreach ($file in $projectFiles) {
        $path = Join-Path $source $file; Assert-RegularPath $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "元プロジェクトがありません: $file" }
    }
    $doc = New-Object System.Xml.XmlDocument
    $root = $doc.CreateElement('Solution'); [void]$doc.AppendChild($root)
    foreach ($file in $projectFiles) {
        $node = $doc.CreateElement('Project'); $node.SetAttribute('Path',('source/' + $file.Replace('\','/'))); [void]$root.AppendChild($node)
    }
    $doc.Save($solution)
    # This wrapper is OUTSIDE the upstream checkout. No source/build file is patched.
}
function Write-Statistics {
    $state = @(Check-Checkout)
    $files = @(Git-Lines @('-C',$source,'-c','core.quotepath=false','ls-files','--','*.cs'))
    $projects = @(Git-Lines @('-C',$source,'ls-files','--','*.csproj'))
    $lines = [long]0; $count = 0; $coreFiles = 0; $coreLines = [long]0
    foreach ($relative in $files) {
        $path = Join-Path $source $relative
        Assert-RegularPath $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $n = [long]0
        foreach ($line in [IO.File]::ReadLines($path)) { $n++ }
        $count++; $lines += $n
        if ($relative.StartsWith('ICSharpCode.Decompiler/')) { $coreFiles++; $coreLines += $n }
    }
    $report = [ordered]@{ repository=$lock.repository; tag=$lock.tag; commit=$lock.commit;
        trackedCsFiles=$count; physicalCsLines=$lines; trackedProjects=$projects.Count;
        decompilerCsFiles=$coreFiles; decompilerPhysicalLines=$coreLines;
        workingTreeChanged=($state.Count -gt 0); inspectedUtc=[DateTime]::UtcNow.ToString('o');
        scope='Tracked C# including upstream tests and generated sources; not code executed or fully reviewed.' }
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $base 'size-report.json') -Encoding UTF8
    Write-Host "C#ファイル $count / 物理行数 $lines / csproj $($projects.Count)" -ForegroundColor Cyan
    Write-Host "うちDecompiler本体: $coreFiles ファイル / $coreLines 行"
    Write-Host 'コメント・空行・上流のテスト/生成済みソースを含みます。解析済み件数ではありません。'
    if ($state.Count) { Write-Warning '取得後の変更があります。元の状態として扱いません。'; $state | ForEach-Object { Write-Host $_ } }
}
try {
    [Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
    Write-Host '大規模OSS: ILSpy v10.0（固定コミット・元ソースは改変しません）' -ForegroundColor Cyan
    Write-Host '甜花ちゃんのための不具合や改善答案は追加しません。上流のテスト・ライセンスは残します。'
    if ($Mode -eq 'Menu') {
        Write-Host '1  初回取得のみ（GitHubからダウンロード。ビルド・実行はしない）'
        Write-Host '2  CLIと依存ライブラリを復元・Releaseビルド'
        Write-Host '3  ビルド済みCLIの起動確認（--help。性能テストではない）'
        Write-Host '4  ソース規模・変更有無・読み込む場所を表示'
        Write-Host '0  終了'
        $choice = (Read-Host '番号 [Enter=1]').Trim()
        if ($choice -eq '0') { return }; if (-not $choice) { $choice='1' }
        $choices=@{'1'='Fetch';'2'='Build';'3'='Smoke';'4'='Info'}
        if (-not $choices.ContainsKey($choice)) { throw '0～4を選んでください。' }; $Mode=$choices[$choice]
    }
    if ($Mode -ne 'Info') {
        Write-Host '取得はネット接続、ビルドはNuGet復元と上流のMSBuild/PowerShell処理を使います。'
        Write-Host '現在のユーザー権限で動きます。SDKの自動導入・Docker・GitHub CI・ソース削除はしません。'
        if ((Read-Host '選んだ処理を開始しますか？ [Y=開始 / Enter=中止]') -notmatch '^(y|yes)$') { return }
    }
    $command = Get-Command git -CommandType Application -ErrorAction SilentlyContinue
    if (-not $command) { throw 'Git for Windowsが必要です。インストール後に再実行してください。' }; $script:git=$command.Source
    Assert-RegularPath $base
    foreach ($path in @($hooks,$solution,(Join-Path $base 'logs'),(Join-Path $base 'size-report.json'))) { Assert-RegularPath $path }
    if ($Mode -eq 'Info' -and -not (Test-Path -LiteralPath $source)) { throw '未取得です。最初に1を実行してください。' }
    New-Item -ItemType Directory -Force -Path @($base,$hooks,(Join-Path $base 'logs')) | Out-Null
    if (@(Get-ChildItem -LiteralPath $hooks -Force).Count) { throw '安全用の空hooksフォルダーにファイルがあります。何も実行しません。' }
    $log=Join-Path $base ('logs/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $PID + '.log')
    Start-Transcript -Path $log | Out-Null; $transcribing=$true
    if ($Mode -eq 'Fetch') {
        if (Test-Path -LiteralPath $source) {
            [void](Check-Checkout); Write-Host '取得済みです。pull・checkout・reset・cleanをせず、そのまま残します。'
        } else {
            $pending=Join-Path $base ('source.partial-' + [Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory $pending | Out-Null
            Write-Host "一時取得先: $pending（失敗時も調査用に残します）"
            [void](Git-Lines @('-c','init.templateDir=','init',$pending))
            [void](Git-Lines @('-C',$pending,'remote','add','origin',[string]$lock.repository))
            [void](Git-Lines @('-C',$pending,'fetch','--no-tags','origin',[string]$lock.commit))
            $got=@(Git-Lines @('-C',$pending,'rev-parse','FETCH_HEAD'))[-1]
            if ($got -cne $lock.commit) { throw '取得コミットが一致しません。展開しません。' }
            [void](Git-Lines @('-C',$pending,'checkout','--detach',[string]$lock.commit))
            if (@(Git-Lines @('-C',$pending,'status','--porcelain','--untracked-files=normal')).Count) { throw '取得直後の作業ツリーに変更があります。採用しません。' }
            if (Test-Path -LiteralPath $source) { throw '他の処理が先に取得を完了しました。上書きしません。' }
            Move-Item -LiteralPath $pending -Destination $source
        }
        Write-AnalysisSolution
    } else {
        $state=@(Check-Checkout)
        if ($state.Count -and $Mode -ne 'Info') {
            $state | ForEach-Object { Write-Host $_ }
            if ((Read-Host '変更済みコードのまま処理しますか？ [Y=開始]') -notmatch '^(y|yes)$') { return }
        }
        if ($Mode -in @('Build','Smoke')) {
            $command=Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
            if (-not $command) { throw '.NET 10 SDKが必要です。SDKは勝手にインストールしません。' }; $script:dotnet=$command.Source
            Push-Location $source; $pushed=$true
            $version=& $dotnet --version
            if ($LASTEXITCODE -ne 0 -or $version -notmatch '^10\.0\.\d+$') { throw '固定したILSpyの評価には.NET 10 SDKを使用してください。元のglobal.jsonは書き換えません。' }
            if ($Mode -eq 'Build') {
                Invoke-Dotnet @('build',(Join-Path $source $entryRelative),'-c','Release','--nologo','--disable-build-servers','-m:1')
            } else {
                $dll=Join-Path $source 'ICSharpCode.ILSpyCmd\bin\Release\net10.0\ilspycmd.dll'
                if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw '先に2のビルドを実行してください。' }
                Invoke-Dotnet @($dll,'--help')
            }
        }
    }
    Write-Statistics
    Write-Host "甜花ちゃん用: $solution" -ForegroundColor Green
    Write-Host "上流の全体構成（追加のWindows/旧.NET等が必要な場合あり）: $(Join-Path $source 'ILSpy.sln')"
    Write-Host '全体ソースは取得しますが、初期解析構成はDecompiler・ILSpyX・CLIの3プロジェクトです。'
    Write-Host '元の単体テスト/サブモジュールは別条件のため自動実行していません。'
    Write-Host 'プロジェクト調査 → 準備 → 付属サンプル → ILSpy を選んでください。'
} catch {
    $code=1; Write-Host '停止しました。成功とは扱っていません。' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Yellow
    Write-Host '既存ソースは自動リセット/削除しません。ネットワーク、SDK、ログの最初のerrorを確認してください。'
} finally {
    if ($pushed) { Pop-Location }
    if ($transcribing) { Stop-Transcript | Out-Null; Write-Host "ログ: $log" }
}
exit $code
