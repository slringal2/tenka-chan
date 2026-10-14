#requires -version 5.1
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tokens = $null
$errors = $null
[void][Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'tenka-windows.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { $errors | Format-List; throw 'PowerShell syntax check failed.' }
Push-Location $repo
try {
    node --test tests/Fixtures/path-picker.test.cjs
    if ($LASTEXITCODE -ne 0) { throw 'JavaScript tests failed.' }
    dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Release --filter 'FullyQualifiedName~NativePathDialogTests'
    if ($LASTEXITCODE -ne 0) { throw 'C# build or tests failed.' }
} finally { Pop-Location }
Write-Host 'Syntax/unit checks passed. Real Windows dialog interaction still requires manual testing.'
