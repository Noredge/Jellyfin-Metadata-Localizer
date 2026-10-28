[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Dotnet,
    [Parameter(Mandatory=$true)][string]$EvidenceRoot,
    [string]$Node='node'
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$target=[IO.Path]::GetFullPath($EvidenceRoot)
if(Test-Path -LiteralPath $target){throw 'Use a new evidence directory'}
if(-not [IO.Path]::IsPathFullyQualified($EvidenceRoot)){throw 'Absolute evidence directory required'}
[IO.Directory]::CreateDirectory($target)|Out-Null
$env:DOTNET_CLI_HOME=Join-Path $repo 'work/dotnet-home'
$head=(& git -c "safe.directory=$repo" -C $repo rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0){throw 'Source commit unavailable'}
$dirty=[bool](& git -c "safe.directory=$repo" -C $repo status --porcelain=v1 -uall)
$projects=@('src/Localizer.Plugin/Localizer.Plugin.csproj','tests/Localizer.Unified.Checks/Localizer.Unified.Checks.csproj','tests/Localizer.Unified.Transport.Checks/Localizer.Unified.Transport.Checks.csproj')
$index=0
foreach($relative in $projects){
    $index++
    $project=Join-Path $repo $relative
    & $Dotnet restore $project --locked-mode -m:1 /nodeReuse:false -p:NuGetAudit=false *> (Join-Path $target "restore-$index.log")
    if($LASTEXITCODE -ne 0){throw "Locked restore failed; inspect restore-$index.log"}
    & $Dotnet build $project -c Release --no-restore --nologo -m:1 /nodeReuse:false -p:UseSharedCompilation=false "-p:SourceRevisionId=$head" *> (Join-Path $target "build-$index.log")
    if($LASTEXITCODE -ne 0){throw "Build failed; inspect build-$index.log"}
}
$checks=Join-Path $repo 'tests/Localizer.Unified.Checks/bin/Release/net10.0/Localizer.Unified.Checks.dll'
$transport=Join-Path $repo 'tests/Localizer.Unified.Transport.Checks/bin/Release/net10.0/Localizer.Unified.Transport.Checks.dll'
& $Dotnet $checks (Join-Path $target 'storage') *> (Join-Path $target 'storage.log')
if($LASTEXITCODE -ne 0){throw 'Storage checks failed'}
& $Dotnet $checks --executor (Join-Path $target 'executor') *> (Join-Path $target 'executor.log')
if($LASTEXITCODE -ne 0){throw 'Executor checks failed'}
& $Dotnet $checks --read (Join-Path $target 'read') *> (Join-Path $target 'read.log')
if($LASTEXITCODE -ne 0){throw 'Read checks failed'}
& $Dotnet $transport (Join-Path $target 'transport') *> (Join-Path $target 'transport.log')
if($LASTEXITCODE -ne 0){throw 'Transport checks failed'}
& $Node (Join-Path $repo 'tests/check_unified_work_poll.mjs') (Join-Path $target 'work-poll-checks.json') *> (Join-Path $target 'work-poll.log')
if($LASTEXITCODE -ne 0){throw 'Work polling checks failed'}
& $Node (Join-Path $repo 'tests/check_unified_onboarding.mjs') (Join-Path $target 'onboarding-checks.json') *> (Join-Path $target 'onboarding.log')
if($LASTEXITCODE -ne 0){throw 'Connection onboarding checks failed'}
& $Node (Join-Path $repo 'tests/check_unified_private14.mjs') (Join-Path $target 'private14-ui-checks.json') *> (Join-Path $target 'private14-ui.log')
if($LASTEXITCODE -ne 0){throw 'Private14 UI checks failed'}
& $Node (Join-Path $repo 'tests/check_unified_private16.mjs') (Join-Path $target 'private16-ui-checks.json') *> (Join-Path $target 'private16-ui.log')
if($LASTEXITCODE -ne 0){throw 'Private16 UI checks failed'}
& $Node (Join-Path $repo 'tests/check_unified_private18.mjs') (Join-Path $target 'private18-ui-checks.json') *> (Join-Path $target 'private18-ui.log')
if($LASTEXITCODE -ne 0){throw 'Private18 UI checks failed'}
& $Node (Join-Path $repo 'tests/check_unified_private19.mjs') (Join-Path $target 'private19-ui-checks.json') *> (Join-Path $target 'private19-ui.log')
if($LASTEXITCODE -ne 0){throw 'Private19 UI checks failed'}
& $Node (Join-Path $repo 'tests/check_unified_private20.mjs') (Join-Path $target 'private20-ui-checks.json') *> (Join-Path $target 'private20-ui.log')
if($LASTEXITCODE -ne 0){throw 'Private20 UI checks failed'}
& $Node (Join-Path $repo 'tests/check_unified_private21.mjs') (Join-Path $target 'private21-ui-checks.json') *> (Join-Path $target 'private21-ui.log')
if($LASTEXITCODE -ne 0){throw 'Private21 UI checks failed'}
& $Node (Join-Path $repo 'tests/check_unified_private22.mjs') (Join-Path $target 'private22-ui-checks.json') *> (Join-Path $target 'private22-ui.log')
if($LASTEXITCODE -ne 0){throw 'Private22 UI checks failed'}
$reports=@('storage/checks.json','storage/settings/settings-checks.json','executor/executor-checks.json','read/read-checks.json','transport/transport-checks.json','work-poll-checks.json','onboarding-checks.json','private14-ui-checks.json','private16-ui-checks.json','private18-ui-checks.json','private19-ui-checks.json','private20-ui-checks.json','private21-ui-checks.json','private22-ui-checks.json')
$evidence=@(foreach($relative in $reports){
    $path=Join-Path $target $relative
    $report=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
    if($report.status -ne 'passed' -or $report.checks.Count -eq 0 -or @($report.checks|Where-Object{-not $_.passed}).Count -ne 0){throw "Invalid or failed evidence: $relative"}
    [ordered]@{path=$relative;checks=$report.checks.Count;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$total=0
foreach($item in $evidence){$total+=$item['checks']}
$result=[ordered]@{status='passed';sourceCommit=$head;sourceDirty=$dirty;verifiedUtc=[DateTimeOffset]::UtcNow.ToString('O');checks=$total;evidence=$evidence;syntheticHttp=$true;nativeHostTested=$false;productionChanged=$false;realModelRequests=0}
$result|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $target 'VERIFICATION.json') -Encoding utf8
$result|ConvertTo-Json -Depth 7
