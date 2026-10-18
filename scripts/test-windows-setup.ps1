#requires -version 5.1
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# Windows PowerShell 5.1 requires UTF-8 BOM when the source contains Japanese text.
# Both the canonical launcher and legacy shim must parse correctly WITHOUT invoking it.
foreach ($scriptName in @('tenka-windows.ps1', 'start-tenka-local.ps1')) {
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
Push-Location $repo
try {
    node --test tests/Fixtures/path-picker.test.cjs
    if ($LASTEXITCODE -ne 0) { throw 'JavaScript tests failed.' }
    dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Release --filter 'FullyQualifiedName~NativePathDialogTests'
    if ($LASTEXITCODE -ne 0) { throw 'C# build or tests failed.' }
} finally { Pop-Location }
Write-Host 'Syntax/unit checks passed. Real Windows dialog interaction still requires manual testing.'
