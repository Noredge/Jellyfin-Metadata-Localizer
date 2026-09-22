#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('openai', 'groq', 'openai-compatible')][string]$Provider,
    [Parameter(Mandatory = $true)][string]$PluginDataPath,
    [string]$ServiceId,
    [switch]$Replace
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'This helper requires Windows CurrentUser DPAPI.' }
if ($Provider -eq 'openai-compatible') {
    if ($ServiceId -cnotmatch '\A[A-Za-z0-9_-]{1,64}\z') {
        throw 'Custom service requires a ServiceId of 1-64 ASCII letters, numbers, underscores or hyphens.'
    }
    $credentialFileName = 'service-' + $ServiceId.ToLowerInvariant() + '-api-key.dpapi'
} else {
    if ($ServiceId) { throw 'ServiceId is only valid for openai-compatible providers.' }
    $credentialFileName = $Provider.ToLowerInvariant() + '-api-key.dpapi'
}
if ($PluginDataPath -notmatch '^(?:[A-Za-z]:[\\/]|\\\\(?![?.]\\)[^\\/:]+\\[^\\/:]+(?:\\|$))') {
    throw 'Use the fully qualified absolute Localizer plugin data directory.'
}
$pluginRoot = [System.IO.Path]::GetFullPath($PluginDataPath)
if ($pluginRoot.TrimEnd('\', '/') -eq [System.IO.Path]::GetPathRoot($pluginRoot).TrimEnd('\', '/')) {
    throw 'A drive root is not a plugin data directory.'
}
$credentialDirectory = Join-Path $pluginRoot 'credentials'
$credentialPath = Join-Path $credentialDirectory $credentialFileName

function Assert-PlainPath([string]$Target) {
    $currentPath = $Target
    while ($currentPath) {
        if (Test-Path -LiteralPath $currentPath) {
            $entry = Get-Item -LiteralPath $currentPath -Force
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Reparse points are not supported in the credential path.'
            }
        }
        $parentPath = [System.IO.Path]::GetDirectoryName($currentPath.TrimEnd('\', '/'))
        if ($parentPath -eq $currentPath) { break }
        $currentPath = $parentPath
    }
}

Assert-PlainPath $credentialPath
if ((Test-Path -LiteralPath $credentialPath) -and -not $Replace) {
    throw 'A saved key already exists. Use -Replace only to replace that key.'
}
Write-Host 'Save a Localizer service key under the Windows account that runs Jellyfin.'
Write-Host 'Input is hidden. This helper makes no network requests and does not test authentication.'
if ($Provider -eq 'openai-compatible') { Write-Host ('Custom service ID: ' + $ServiceId) }
$serviceSecret = $null
$pendingPath = $null
$encryptedSecret = $null
try {
    $serviceSecret = Read-Host ($Provider + ' API key') -AsSecureString
    if ($null -eq $serviceSecret -or $serviceSecret.Length -eq 0 -or $serviceSecret.Length -gt 4096) {
        throw 'No key saved: input was empty or unexpectedly long.'
    }
    $encryptedSecret = ConvertFrom-SecureString -SecureString $serviceSecret
    [System.IO.Directory]::CreateDirectory($credentialDirectory) | Out-Null
    Assert-PlainPath $credentialPath
    $pendingPath = Join-Path $credentialDirectory ([Guid]::NewGuid().ToString('N') + '.tmp')
    $credentialStream = [System.IO.File]::Open($pendingPath, [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $encryptedBytes = [System.Text.Encoding]::UTF8.GetBytes($encryptedSecret)
        $credentialStream.Write($encryptedBytes, 0, $encryptedBytes.Length)
        $credentialStream.Flush($true)
    } finally { $credentialStream.Dispose() }
    if (Test-Path -LiteralPath $credentialPath) {
        if (-not $Replace) { throw 'A saved key appeared during input; it was not overwritten.' }
        [System.IO.File]::Replace($pendingPath, $credentialPath, [NullString]::Value)
    } else {
        [System.IO.File]::Move($pendingPath, $credentialPath)
    }
    Write-Host 'Saved the encrypted key. Authentication has NOT been tested.'
    Write-Host $credentialPath
} finally {
    if ($pendingPath -and (Test-Path -LiteralPath $pendingPath)) {
        Remove-Item -LiteralPath $pendingPath -Force
    }
    if ($null -ne $serviceSecret) { $serviceSecret.Dispose() }
    $encryptedSecret = $null
}
