#Requires -Version 5.1
[CmdletBinding()]
param([string]$Dotnet = 'dotnet', [string]$Node = 'node', [string]$Python = 'python', [switch]$IncludePerformance)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $repo 'work/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$runId = [Guid]::NewGuid().ToString('N')
$reportRoot = Join-Path $repo ('work/checks/' + $runId)
New-Item -ItemType Directory -Path $reportRoot | Out-Null
$results = @()
function Invoke-Check([string]$Name, [string]$Executable, [string[]]$Arguments) {
    $log = Join-Path $reportRoot ($Name + '.log')
    & $Executable @Arguments *> $log
    $code = $LASTEXITCODE
    if ($code -ne 0) { Get-Content -LiteralPath $log -Tail 30; throw "$Name failed (exit $code)." }
    Write-Host "PASS $Name"
}
Push-Location $repo
try {
    $checks = @('Core','Translation','Operations','Genre','Batch','CampaignStore','Overview','GenreTranslation')
    if ($IncludePerformance) { $checks += 'Performance' }
    foreach ($name in $checks) {
        $project = "tests/Localizer.$name.Checks/Localizer.$name.Checks.csproj"
        Invoke-Check "$name-restore" $Dotnet @('restore',$project,'--locked-mode','--configfile','NuGet.config')
        $arguments = @('run','--project',$project,'--no-restore','-c','Release','--')
        if ($name -notin @('Overview','GenreTranslation')) {
            $scratch = Join-Path $reportRoot ($name + '-scratch')
            if ($name -eq 'CampaignStore') { $scratch = Join-Path $repo ('work/m9-campaign-store/' + $runId) }
            if ($name -eq 'Performance') { $scratch = Join-Path $repo ('work/m9-performance/' + $runId) }
            $arguments += @($scratch,(Join-Path $reportRoot ($name + '.json')))
            if ($name -in @('CampaignStore','Performance')) { $arguments += $repo }
        }
        Invoke-Check $name $Dotnet $arguments
        $results += [ordered]@{ name=$name; kind='dotnet'; passed=$true }
    }
    foreach ($test in (Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'Test-*.cjs' -File | Sort-Object Name)) {
        Invoke-Check $test.BaseName $Node @($test.FullName)
        $results += [ordered]@{ name=$test.BaseName; kind='node'; passed=$true }
    }
    Invoke-Check 'Backup' $Python @('scripts/Test-LocalizerBackup.py','scripts/Localizer-DataBackup.py',(Join-Path $reportRoot 'backup-scratch'),(Join-Path $reportRoot 'Backup.json'))
    $results += [ordered]@{ name='Backup'; kind='python'; passed=$true }
    $powerShell = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    Invoke-Check 'Credential' $powerShell @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'Test-Credential.ps1'))
    $results += [ordered]@{ name='Credential'; kind='powershell'; passed=$true }
    Invoke-Check 'Privacy' $Python @('scripts/Privacy-Check.py')
    $results += [ordered]@{ name='Privacy'; kind='python'; passed=$true }
    $summary = [ordered]@{ passed=$true; checks=@($results); performanceIncluded=[bool]$IncludePerformance; cloudRequests=0; productionAccess=$false }
    $json = $summary | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $repo 'work/test-results.json'),$json,(New-Object Text.UTF8Encoding($false)))
    Write-Host "PASS $($results.Count) check groups. Summary: work/test-results.json"
} finally { Pop-Location }
