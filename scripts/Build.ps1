#Requires -Version 5.1
[CmdletBinding()]
param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $repo 'work/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
Push-Location $repo
try {
    & $Dotnet restore src/Localizer.Plugin/Localizer.Plugin.csproj --locked-mode --configfile NuGet.config
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    & $Dotnet build src/Localizer.Plugin/Localizer.Plugin.csproj --no-restore -c Release -p:ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
} finally { Pop-Location }
