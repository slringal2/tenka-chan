#requires -version 5.1
<#
Read-only, best-effort local PUBLIC release preflight. No upload, no git
mutation, no network requests, no Docker, no GitHub CI. A pass is NOT
proof that secrets, rights issues or vulnerable dependencies are absent.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$git = Get-Command git.exe -CommandType Application -ErrorAction Stop
$errors = 0
$warnings = 0

function Run-Git([string[]]$Arguments, [int[]]$Accepted = @(0)) {
    $old = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $git.Source -C $root @Arguments 2>&1 | ForEach-Object { [string]$_ })
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $old }
    if ($Accepted -notcontains $code) {
        throw "git command failed (exit $code): $($Arguments[0])"
    }
    return $output
}
function Warn([string]$text) { $script:warnings++; Write-Host "REVIEW  $text" -ForegroundColor Yellow }
function Fail([string]$text) { $script:errors++; Write-Host "BLOCKER $text" -ForegroundColor Red }
function Ok([string]$text) { Write-Host "CHECK   $text" -ForegroundColor Green }

$top = @(Run-Git @('rev-parse','--show-toplevel'))[-1]
if ([IO.Path]::GetFullPath($top) -ine $root) { throw 'Run from a complete Tenka-chan Git checkout.' }

foreach ($needed in @('LICENSE','THIRD-PARTY-NOTICES.md','SECURITY.md',
    'CONTRIBUTING.md','docs/PUBLIC-RELEASE-CHECKLIST.md')) {
    if (Test-Path -LiteralPath (Join-Path $root $needed) -PathType Leaf) { Ok "$needed exists" }
    else { Fail "Missing $needed" }
}
$tracked = @(Run-Git @('ls-files'))
$localEdits = @(Run-Git @('status','--porcelain','--untracked-files=all'))
if ($localEdits.Count -gt 0) { Warn "Working tree contains $($localEdits.Count) changed/untracked paths; inspect and commit intentionally." }
else { Ok 'Working tree clean' }

if (@($tracked | Where-Object { $_ -like 'samples/External/*' }).Count) {
    Fail 'A separately downloaded external-source checkout is tracked under samples/External.'
} else { Ok 'No tracked samples/External checkout' }
if (@($tracked | Where-Object { $_ -like '.github/workflows/*' }).Count) {
    Fail 'Active GitHub Actions workflows are tracked. Owner requested CI remain paused.'
} else { Ok 'No active GitHub Actions workflow tracked on this checkout' }

$possibleSensitiveName = '(?i)(^|/)(mcp-config\.json|settings\.json|\.env(?:\..*)?|.*-connection\.json|.*\.(?:pfx|p12|pem|key|sqlite|sqlite3|db|dmp|nettrace|etlx|nupkg|dll|exe))$'
$trackedSensitive = @($tracked | Where-Object { $_ -match $possibleSensitiveName })
if ($trackedSensitive.Count) { Fail "Potentially sensitive or generated tracked files: $($trackedSensitive.Count). Inspect git ls-files manually." }
else { Ok 'No obviously private binary/config paths are tracked (filename heuristic)' }

# Print file names, never the raw match (which might be a credential).
$pattern = '(AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{30,}|sk-[A-Za-z0-9_-]{32,}|-----BEGIN ([A-Z ]+ )?PRIVATE KEY-----)'
$currentMatches = @(Run-Git @('grep','-I','-l','-E',$pattern,'--','.') @(0,1))
if ($currentMatches.Count) { Warn "Potential token patterns in $($currentMatches.Count) tracked current files. Inspect using git grep; no token values printed." }
else { Ok 'No high-confidence token-pattern strings found in current tracked text' }

# Unlike git grep on HEAD, history below includes other reachable branches.
$historicPaths = @(Run-Git @('log','--all','--name-only','--pretty=format:'))
$historicSensitive = @($historicPaths | Where-Object { $_ -match $possibleSensitiveName } | Sort-Object -Unique)
if ($historicSensitive.Count) {
    Warn "$($historicSensitive.Count) sensitive-looking filename(s) occurred in reachable Git history. Review before making ANY branch public."
} else { Ok 'No sensitive-looking filenames detected in reachable history (heuristic)' }
$matchingCommits = @(Run-Git @('log','--all','--extended-regexp','-G',$pattern,'--pretty=format:%h','--no-patch'))
if ($matchingCommits.Count) {
    Warn "$($matchingCommits.Count) reachable commit(s) changed lines matching token patterns. Manually triage and rotate any exposed credentials."
} else { Ok 'No high-confidence token-pattern changes detected in reachable history (heuristic)' }

$authorEmails = @(Run-Git @('log','--all','--format=%ae') | Sort-Object -Unique)
$nonNoreply = @($authorEmails | Where-Object { $_ -and $_ -notmatch '(?i)@users\.noreply\.github\.com$' })
if ($nonNoreply.Count) {
    Warn "$($nonNoreply.Count) distinct historical commit-author email(s) are not GitHub noreply addresses; check author privacy before publication."
}
Write-Host ''
Write-Host "Preflight: blockers=$errors, review-items=$warnings" -ForegroundColor Cyan
Write-Host 'Not checked: full semantic secrets, untracked local data, NuGet transitive licenses, dependency vulnerabilities, company IP rights, trademark/image ownership, or private PR/issue discussions.'
Write-Host 'Publishing the repository can expose prior commits and branches. This command NEVER changes visibility.'
if ($errors) { exit 2 }
if ($warnings) { exit 1 }
exit 0
