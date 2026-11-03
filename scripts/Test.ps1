[CmdletBinding()]
param([string]$Dotnet='dotnet',[string]$Node='node',[string]$Python='python')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$evidence=Join-Path $repo ('work/checks/'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6))
& (Join-Path $PSScriptRoot 'Verify-Unified.ps1') -Dotnet $Dotnet -Node $Node -EvidenceRoot $evidence
if($LASTEXITCODE -ne 0){throw 'Unified checks failed'}
Push-Location $repo
try {
    & $Python scripts/Test-LocalizerBackup.py (Join-Path $evidence 'backup') (Join-Path $evidence 'backup-checks.json')
    if($LASTEXITCODE -ne 0){throw 'Backup checks failed'}
    & $Python scripts/Test-LocalizerAdoption.py (Join-Path $evidence 'adoption') (Join-Path $evidence 'adoption-checks.json')
    if($LASTEXITCODE -ne 0){throw 'Adoption checks failed'}
    & $Node tests/check_installation.mjs (Join-Path $evidence 'public-ui-checks.json')
    if($LASTEXITCODE -ne 0){throw 'Public setup checks failed'}
    & $Node tests/check_web_sources.mjs (Join-Path $evidence 'web-sources-checks.json')
    if($LASTEXITCODE -ne 0){throw 'Web source and embedded page checks failed'}
    & $Node tests/check_rendering.mjs (Join-Path $evidence 'ui-performance-checks.json')
    if($LASTEXITCODE -ne 0){throw 'UI rendering and translation checks failed'}
    & $Python scripts/Privacy-Check.py
    if($LASTEXITCODE -ne 0){throw 'Privacy checks failed'}
    $summary=Get-Content -LiteralPath (Join-Path $evidence 'VERIFICATION.json') -Raw|ConvertFrom-Json
    foreach($file in @('backup-checks.json','adoption-checks.json','public-ui-checks.json','web-sources-checks.json','ui-performance-checks.json')){
        $r=Get-Content -LiteralPath (Join-Path $evidence $file) -Raw|ConvertFrom-Json
        if($r.status -ne 'passed' -or @($r.checks|Where-Object{-not $_.passed}).Count){throw 'Failed extra check'}
        $summary.checks+=$r.checks.Count
    }
    $summary|Add-Member -NotePropertyName evidenceRoot -NotePropertyValue $evidence
    $summary|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $repo 'work/test-results.json') -Encoding utf8
    Write-Host "PASS $($summary.checks) checks; work/test-results.json"
} finally {Pop-Location}
