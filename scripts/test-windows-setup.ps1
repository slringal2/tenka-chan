#requires -version 5.1
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# Windows PowerShell 5.1 requires UTF-8 BOM when the source contains Japanese text.
# Both the canonical launcher and legacy shim must parse correctly WITHOUT invoking it.
foreach ($scriptName in @('tenka-windows.ps1', 'start-tenka-local.ps1', 'check-tenka.ps1')) {
    $script = Join-Path $PSScriptRoot $scriptName
    $bytes = [IO.File]::ReadAllBytes($script)
    if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or
        $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
        throw "UTF-8 BOM is required for PowerShell 5.1: $scriptName"
    }
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { $errors | Format-List; throw "PowerShell syntax check failed: $scriptName" }
}
# These checks examine the real-startup opt-in path without starting a server.
$canonical = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tenka-windows.ps1') -Raw -Encoding UTF8
$smoke = Get-Content -LiteralPath (Join-Path $repo 'verify-start.bat') -Raw
if ($canonical -notmatch '\[switch\]\$VerifyStartup' -or
    $canonical -notmatch 'if \(\$VerifyStartup\)' -or
    $canonical -notmatch 'agent/v1/begin' -or
    $canonical -notmatch 'agent/v1/end' -or
    $canonical -notmatch '\$verifiedInstanceId' -or
    $smoke -notmatch '\-Mode Start \-VerifyStartup') {
    throw 'Real startup smoke route is incomplete. Verify launcher and verify-start.bat.'
}
Push-Location $repo
try {
    node --test tests/Fixtures/path-picker.test.cjs
    if ($LASTEXITCODE -ne 0) { throw 'JavaScript tests failed.' }
    dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Release --filter 'FullyQualifiedName~NativePathDialogTests'
    if ($LASTEXITCODE -ne 0) { throw 'C# build or tests failed.' }
} finally { Pop-Location }
Write-Host 'Syntax/unit checks passed. Real Windows dialog interaction still requires manual testing.'
