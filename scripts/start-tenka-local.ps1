#requires -version 5.1
<#
.Legacy start entry point.
Run setup.bat to select/update TENKA_ALLOWED_ROOTS and execution permissions.
Do not duplicate trusted host execution settings in this wrapper.
#>
[CmdletBinding()]
param(
    [string]$WorkspaceRoot = '',
    [string]$ModelEndpoint = '',
    [string]$ModelName = ''
)
$ErrorActionPreference = 'Stop'
if ($PSBoundParameters.ContainsKey('WorkspaceRoot') -or
    $PSBoundParameters.ContainsKey('ModelEndpoint') -or
    $PSBoundParameters.ContainsKey('ModelName')) {
    throw 'start-tenka-local.ps1 の旧オプションは廃止されました。setup.bat で解析フォルダーと許可を設定し、start.bat で起動してください。外部LLMはMCPクライアント側で設定してください。旧引数を黙って無視しません。'
}
$canonical = Join-Path $PSScriptRoot 'tenka-windows.ps1'
if (-not (Test-Path -LiteralPath $canonical -PathType Leaf)) {
    throw '正式ランチャー scripts/tenka-windows.ps1 が見つかりません。リポジトリ一式を取得してください。'
}
& $canonical -Mode Start
