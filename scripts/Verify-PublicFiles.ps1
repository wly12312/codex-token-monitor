[CmdletBinding()]
param([switch]$Staged)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ($Staged) {
    $files = @(& git -C $projectRoot diff --cached --name-only --diff-filter=ACMR)
} else {
    $files = @(& git -C $projectRoot ls-files)
}
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the Git file list.' }
if ($files.Count -eq 0) { throw 'No public files to verify.' }
$privatePath = '(?i)(^|/)(artifacts|bin|obj|\.codex|sessions|archived_sessions|data|scratch|tmp)(/|$)|(^|/)(auth\.json|settings\.json|application-private\.[^/]*|\.env(\..*)?)$|\.(jsonl|sqlite3?|db|pem|key|pfx|p12|log)$'
$secretPatterns = @(
    'gh[pousr]_[A-Za-z0-9]{20,}',
    'github_pat_[A-Za-z0-9_]{20,}',
    'sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{20,}',
    '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
    '(?i)[A-Z]:[\\/](?:Users|Documents and Settings)[\\/]',
    '(?i)[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}'
)
$problems = [System.Collections.Generic.List[string]]::new()
foreach ($relativePath in $files) {
    if ($relativePath -match $privatePath) {
        $problems.Add('Private file is tracked: ' + $relativePath)
        continue
    }
    if ($relativePath -match '\.(png|ico)$') { continue }
    $content = if ($Staged) {
        (& git -C $projectRoot show (':' + $relativePath)) -join "`n"
    } else {
        Get-Content -LiteralPath (Join-Path $projectRoot $relativePath) -Raw
    }
    foreach ($pattern in $secretPatterns) {
        if ($content -match $pattern) {
            # Report only the filename, never a matched secret.
            $problems.Add('Review possible private content in: ' + $relativePath)
            break
        }
    }
}
if ($problems.Count -gt 0) { throw ($problems -join "`n") }
Write-Host ('Verified ' + $files.Count + ' public files; no blocked paths or matching private strings.')
