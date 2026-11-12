# Waits for the latest "macOS build" GitHub Actions run on a branch and prints the result.
# Uses the public GitHub API (no login needed for this public repo).
#   powershell -ExecutionPolicy Bypass -File .claude\skills\ship\check-macos-build.ps1 [-Branch dev] [-Sha <commit>] [-TimeoutMinutes 25]
param([string]$Branch = 'dev', [string]$Sha = '', [int]$TimeoutMinutes = 25)
$ErrorActionPreference = 'Stop'
$repo = 'funwkwk/screenX'
$headers = @{ 'User-Agent' = 'screenX-ship'; 'Accept' = 'application/vnd.github+json' }
function Api($path) { Invoke-RestMethod "https://api.github.com/repos/$repo/$path" -Headers $headers }

if (-not $Sha) { $Sha = (git rev-parse "origin/$Branch").Trim() }
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$run = $null
while ((Get-Date) -lt $deadline) {
    $runs = (Api "actions/runs?branch=$Branch&per_page=10").workflow_runs
    $run = $runs | Where-Object { $_.head_sha -eq $Sha -and $_.name -eq 'macOS build' } | Select-Object -First 1
    if ($run -and $run.status -eq 'completed') { break }
    Start-Sleep -Seconds 45   # stays well inside the 60 requests/hour anonymous limit
}
if (-not $run) { "NO RUN: no 'macOS build' run found for $Sha on $Branch (the workflow only starts when macos/** or the workflow file changed)."; exit 2 }
if ($run.status -ne 'completed') { "TIMEOUT: run still $($run.status): $($run.html_url)"; exit 3 }

"RESULT: $($run.conclusion)   $($run.html_url)"
if ($run.conclusion -eq 'success') {
    try {
        $rel = Api 'releases/tags/macos-latest'
        foreach ($a in $rel.assets) { "DMG: $($a.browser_download_url)  ($([math]::Round($a.size / 1MB, 1)) MB)" }
    } catch { "The run succeeded but the macos-latest release was not found yet." }
    exit 0
}
# failed: the workflow writes the compiler errors into one annotation
foreach ($job in (Api "actions/runs/$($run.id)/jobs").jobs) {
    "JOB: $($job.name) -> $($job.conclusion)"
    foreach ($s in $job.steps) { if ($s.conclusion -eq 'failure') { "  failed step: $($s.name)" } }
    foreach ($an in (Api "check-runs/$($job.id)/annotations")) { "--- $($an.title)"; $an.message }
}
exit 1
