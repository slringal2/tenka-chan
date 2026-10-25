#requires -version 5.1
<#
Read-only, best-effort PUBLIC release preflight. No uploads, Git mutations,
network fetches, Docker, or CI. No-match is NOT proof of release readiness.
Only locally available commits/refs are inspected; use a fully fetched clone.
#>
[CmdletBinding()]
param([string]$RepositoryPath = '')
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryPath)) {
    $RepositoryPath = Join-Path $PSScriptRoot '..'
}
$root = [IO.Path]::GetFullPath($RepositoryPath)
$git = Get-Command git -CommandType Application -ErrorAction Stop
$errors = 0
$warnings = 0
$oldLocks = $env:GIT_OPTIONAL_LOCKS
$oldLazy = $env:GIT_NO_LAZY_FETCH
$env:GIT_OPTIONAL_LOCKS = '0'
$env:GIT_NO_LAZY_FETCH = '1'

function Run-Git([string[]]$Arguments, [int[]]$Accepted = @(0)) {
    $old = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $git.Source --no-pager -c core.quotePath=false -C $root @Arguments 2>&1 | ForEach-Object { [string]$_ })
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $old }
    if ($Accepted -notcontains $code) {
        # Native stderr may contain paths or tokens; never echo it unredacted.
        throw "git command failed (exit $code): $($Arguments[0]); inspection incomplete"
    }
    return $output
}
function Warn([string]$text) { $script:warnings++; Write-Host "REVIEW  $text" -ForegroundColor Yellow }
function Fail([string]$text) { $script:errors++; Write-Host "BLOCKER $text" -ForegroundColor Red }
function Ok([string]$text) { Write-Host "CHECK   $text" -ForegroundColor Green }

try {
    $top = @(Run-Git @('rev-parse','--show-toplevel'))[-1]
    if ([IO.Path]::GetFullPath($top) -ine $root) {
        throw 'Run from the root of a complete Git checkout, not its parent or a subdirectory.'
    }
    [void](Run-Git @('rev-parse','--verify','HEAD'))
    $shallow = @(Run-Git @('rev-parse','--is-shallow-repository'))[-1]
    $partial = @(Run-Git @('config','--get-regexp','^(extensions\.partialclone|remote\..*\.promisor)$') @(0,1))
    if ($shallow -ne 'false' -or $partial.Count -gt 0) {
        Fail 'Shallow/partial clone: history coverage is incomplete. Use a fully fetched clone; this checker will NOT fetch objects or claim history is clean.'
        exit 2
    }
    # Missing objects are errors, not no-match; do not continue into lazy fetch.
    [void](Run-Git @('rev-list','--objects','--all','--missing=error'))
    $replacements = @(Run-Git @('for-each-ref','--format=%(refname)','refs/replace'))
    if ($replacements.Count) {
        Fail 'Replacement refs can mask original history. Inspect in a clean clone without replacements; no automatic rewriting is performed.'
        exit 2
    }
    Ok 'Available local reachable objects passed completeness checks (does not prove every remote ref was fetched).'

    foreach ($needed in @('LICENSE','THIRD-PARTY-NOTICES.md','SECURITY.md',
        'CONTRIBUTING.md','docs/PUBLIC-RELEASE-CHECKLIST.md')) {
        if (Test-Path -LiteralPath (Join-Path $root $needed) -PathType Leaf) { Ok "$needed exists" }
        else { Fail "Missing $needed" }
    }
    $tracked = @(Run-Git @('ls-files'))
    $localEdits = @(Run-Git @('status','--porcelain','--untracked-files=all'))
    if ($localEdits.Count) { Warn "Working tree has $($localEdits.Count) changed/untracked paths; review intentionally." }
    else { Ok 'Working tree clean' }

    if (@($tracked | Where-Object { $_ -like 'samples/External/*' }).Count) {
        Fail 'An external-source checkout is tracked under samples/External.'
    } else { Ok 'No tracked samples/External checkout' }
    if (@($tracked | Where-Object { $_ -like '.github/workflows/*' }).Count) {
        Fail 'Active workflow files are tracked. Owner requested CI remain paused.'
    } else { Ok 'No active GitHub Actions workflow tracked on this checkout' }

    $possibleSensitiveName = '(?i)(^|/)(mcp-config\.json|settings\.json|\.env(?:\..*)?|.*-connection\.json|.*\.(?:pfx|p12|pem|key|sqlite|sqlite3|db|dmp|nettrace|etlx|nupkg|dll|exe))$'
    $trackedSensitive = @($tracked | Where-Object { $_ -match $possibleSensitiveName })
    if ($trackedSensitive.Count) { Fail "Sensitive-looking tracked filenames: $($trackedSensitive.Count). Inspect locally; names are withheld from this report." }
    else { Ok 'No sensitive-looking current filenames detected (heuristic only)' }

    # High-confidence patterns only. Print counts, NEVER matching secret values.
    $pattern = '(AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{30,}|sk-[A-Za-z0-9_-]{32,}|-----BEGIN ([A-Z ]+ )?PRIVATE KEY-----)'
    $currentMatches = @(Run-Git @('grep','-I','-l','-E',$pattern,'--','.') @(0,1))
    if ($currentMatches.Count) { Warn "Token-pattern matches in $($currentMatches.Count) current files. Inspect and rotate any real credentials." }
    else { Ok 'No token-pattern matches in current tracked text (not a secret-free certification)' }

    # -m makes merge-only changes visible; default log pickaxe ignores merges.
    # Disable configured external diff/textconv helpers during this inspection.
    $historicPaths = @(Run-Git @('log','--all','--full-history','-m','--no-renames','--no-ext-diff','--no-textconv','--name-only','--pretty=format:'))
    $historicSensitive = @($historicPaths | Where-Object { $_ -match $possibleSensitiveName } | Sort-Object -Unique)
    if ($historicSensitive.Count) { Warn "Sensitive-looking filenames occurred in reachable history: $($historicSensitive.Count). Review ALL intended public refs." }
    else { Ok 'No sensitive-looking historical filenames detected (local reachable history only)' }
    $matchingCommits = @(Run-Git @('log','--all','--full-history','-m','--no-renames','--no-ext-diff','--no-textconv','--extended-regexp','-G',$pattern,'--pretty=format:%H','--no-patch') | Where-Object { $_ } | Sort-Object -Unique)
    if ($matchingCommits.Count) { Warn "Token-pattern changes in $($matchingCommits.Count) reachable commits. Removing a current file does NOT clear its history." }
    else { Ok 'No token-pattern changes detected in local reachable history; other leak types are not covered.' }

    $authorEmails = @(Run-Git @('log','--all','--format=%ae') | Sort-Object -Unique)
    $nonNoreply = @($authorEmails | Where-Object { $_ -and $_ -notmatch '(?i)@users\.noreply\.github\.com$' })
    if ($nonNoreply.Count) { Warn "Historical non-noreply author emails: $($nonNoreply.Count). Check identity/privacy; values are withheld." }

    Write-Host ''
    Write-Host "Preflight: blockers=$errors, review-items=$warnings" -ForegroundColor Cyan
    Write-Host 'Unverified: unfetched remote/PR refs, Actions logs/artifacts, semantic secrets, untracked/ignored files, transitive licenses, dependency security, employer IP and branding rights.'
    Write-Host 'This checker NEVER changes visibility or rewrites history. No-match does not authorize publication.'
    if ($errors) { exit 2 }
    if ($warnings) { exit 1 }
    exit 0
} catch {
    Write-Host 'BLOCKER Inspection failed or was incomplete. Do NOT interpret this as no findings.' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Yellow
    exit 2
} finally {
    $env:GIT_OPTIONAL_LOCKS = $oldLocks
    $env:GIT_NO_LAZY_FETCH = $oldLazy
}
