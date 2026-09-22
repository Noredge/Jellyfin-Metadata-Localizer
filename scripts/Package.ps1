#Requires -Version 5.1
[CmdletBinding()]
param([string]$Dotnet = 'dotnet', [string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Build.ps1') -Dotnet $Dotnet
Push-Location $repo
try {
    & $Python scripts/Privacy-Check.py
    if ($LASTEXITCODE -ne 0) { throw 'Source privacy checks failed.' }
    $assets = Get-Content -LiteralPath src/Localizer.Plugin/obj/project.assets.json -Raw | ConvertFrom-Json
    $packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
    $wanted = @('MediaBrowser.Common.dll','MediaBrowser.Controller.dll','MediaBrowser.Model.dll','Jellyfin.Data.dll','Jellyfin.Database.Implementations.dll','Microsoft.Data.Sqlite.dll','SQLitePCLRaw.core.dll')
    $references = @()
    foreach ($entry in $assets.targets.'net10.0'.PSObject.Properties) {
        if (-not $entry.Value.compile) { continue }
        foreach ($asset in $entry.Value.compile.PSObject.Properties.Name) {
            $name = [IO.Path]::GetFileName($asset)
            if ($name -notin $wanted) { continue }
            $packagePath = $assets.libraries.($entry.Name).path
            $path = $null
            foreach ($packageRoot in $packageRoots) {
                $candidate = Join-Path (Join-Path $packageRoot $packagePath) $asset
                if (Test-Path -LiteralPath $candidate) { $path = $candidate; break }
            }
            if (-not $path) { throw "Missing package reference $name" }
            $references += [ordered]@{ name=$name; package=$entry.Name; version=[Reflection.AssemblyName]::GetAssemblyName($path).Version.ToString(); sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); suppliedBy='Jellyfin host'; hashKind='NuGet compile reference' }
        }
    }
    if ($references.Count -ne $wanted.Count) { throw 'Incomplete host compile reference inventory.' }
    $assemblies = @()
    foreach ($name in @('Localizer.Plugin.dll','Localizer.Core.dll','Localizer.Jellyfin.dll','Localizer.Translation.dll')) {
        $path = Join-Path $repo "src/Localizer.Plugin/bin/Release/net10.0/$name"
        $version = [Reflection.AssemblyName]::GetAssemblyName($path).Version.ToString()
        if ($version -ne '0.11.0.0') { throw "Unexpected assembly version for $name" }
        $assemblies += [ordered]@{ name=$name; version=$version; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $metadata = [ordered]@{ assemblies=@($assemblies); hostDependencies=@($references | Sort-Object name) } | ConvertTo-Json -Depth 6
    $metadataPath = Join-Path $repo 'work/package-build-metadata.json'
    [IO.File]::WriteAllText($metadataPath,$metadata,(New-Object Text.UTF8Encoding($false)))
    & $Python scripts/Verify-Package.py --create $metadataPath
    if ($LASTEXITCODE -ne 0) { throw 'Package creation or verification failed.' }
} finally { Pop-Location }
