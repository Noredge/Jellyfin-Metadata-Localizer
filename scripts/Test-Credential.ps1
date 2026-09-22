#Requires -Version 5.1
# Offline test with synthetic strings only. Never reads installed credentials.
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('jml-credential-check-' + [Guid]::NewGuid().ToString('N'))
$helper = Join-Path $PSScriptRoot 'Save-Credential.ps1'
$global:JmlCredentialTestSecret = 'synthetic-credential-one'
$global:JmlCredentialPromptCount = 0
function Read-Host {
    param([string]$Prompt, [switch]$AsSecureString)
    $global:JmlCredentialPromptCount++
    $secure = New-Object System.Security.SecureString
    foreach ($character in $global:JmlCredentialTestSecret.ToCharArray()) { $secure.AppendChar($character) }
    return $secure
}
function Read-TestSecret([string]$Path) {
    $secure = ConvertTo-SecureString (Get-Content -LiteralPath $Path -Raw)
    try { return [System.Net.NetworkCredential]::new('', $secure).Password }
    finally { $secure.Dispose() }
}
function Assert([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAILED: ' + $Name) }
    Write-Output ('PASS: ' + $Name)
}
function Expect-Failure([scriptblock]$Action, [string]$Pattern, [string]$Name) {
    $failed = $false
    try { & $Action } catch { if ($_.Exception.Message -notmatch $Pattern) { throw }; $failed = $true }
    Assert $failed $Name
}
try {
    & $helper -Provider openai -PluginDataPath $testRoot 6>$null
    $openAiFile = Join-Path $testRoot 'credentials/openai-api-key.dpapi'
    Assert ((Read-TestSecret $openAiFile) -ceq $global:JmlCredentialTestSecret) 'CurrentUser DPAPI round trip'
    Assert ((Get-Content -LiteralPath $openAiFile -Raw) -notmatch 'synthetic') 'No plaintext saved'
    $initialHash = (Get-FileHash -LiteralPath $openAiFile -Algorithm SHA256).Hash
    $initialPrompts = $global:JmlCredentialPromptCount
    Expect-Failure { & $helper -Provider openai -PluginDataPath $testRoot 6>$null } 'already exists' 'Existing key refused'
    Assert ($global:JmlCredentialPromptCount -eq $initialPrompts -and (Get-FileHash -LiteralPath $openAiFile -Algorithm SHA256).Hash -eq $initialHash) 'Refusal preserves key without prompting'
    $global:JmlCredentialTestSecret = 'synthetic-credential-two'
    & $helper -Provider openai -PluginDataPath $testRoot -Replace 6>$null
    Assert ((Read-TestSecret $openAiFile) -ceq $global:JmlCredentialTestSecret) 'Explicit replacement'
    & $helper -Provider groq -PluginDataPath $testRoot 6>$null
    Assert ((Read-TestSecret (Join-Path $testRoot 'credentials/groq-api-key.dpapi')) -ceq $global:JmlCredentialTestSecret) 'Separate provider path'
    Expect-Failure { & $helper -Provider openai-compatible -PluginDataPath $testRoot 6>$null } 'requires a ServiceId' 'Custom service requires ID'
    Expect-Failure { & $helper -Provider openai-compatible -ServiceId '../other' -PluginDataPath $testRoot 6>$null } 'requires a ServiceId' 'Custom service rejects unsafe ID'
    Expect-Failure { & $helper -Provider openai -ServiceId 'custom-one' -PluginDataPath $testRoot 6>$null } 'only valid' 'Built-in provider rejects custom ID'
    $global:JmlCredentialTestSecret = 'custom-provider-one-synthetic'
    & $helper -Provider openai-compatible -ServiceId 'Custom-One' -PluginDataPath $testRoot 6>$null
    $customFile = Join-Path $testRoot 'credentials/service-custom-one-api-key.dpapi'
    Assert ((Read-TestSecret $customFile) -ceq $global:JmlCredentialTestSecret) 'Generic custom key round trip and canonical path'
    Expect-Failure { & $helper -Provider openai-compatible -ServiceId 'custom-one' -PluginDataPath $testRoot 6>$null } 'already exists' 'Case variants cannot overwrite custom key'
    $global:JmlCredentialTestSecret = 'custom-provider-two-synthetic'
    & $helper -Provider openai-compatible -ServiceId 'custom-two' -PluginDataPath $testRoot 6>$null
    Assert ((Read-TestSecret $customFile) -ceq 'custom-provider-one-synthetic' -and (Read-TestSecret (Join-Path $testRoot 'credentials/service-custom-two-api-key.dpapi')) -ceq $global:JmlCredentialTestSecret) 'Custom services use isolated key files'
    & $helper -Provider openai-compatible -ServiceId 'Custom-One' -PluginDataPath $testRoot -Replace 6>$null
    Assert ((Read-TestSecret $customFile) -ceq $global:JmlCredentialTestSecret -and (Read-TestSecret $openAiFile) -ceq 'synthetic-credential-two') 'Custom replacement preserves built-in key'
    $global:JmlCredentialTestSecret = ''
    Expect-Failure { & $helper -Provider openai -PluginDataPath (Join-Path $testRoot 'empty') 6>$null } 'empty' 'Empty input refused'
    Assert (-not (Test-Path -LiteralPath (Join-Path $testRoot 'empty'))) 'Empty input creates no store'
    $global:JmlCredentialTestSecret = 'x' * 4097
    Expect-Failure { & $helper -Provider openai -PluginDataPath (Join-Path $testRoot 'large') 6>$null } 'long' 'Oversize input refused'
    Expect-Failure { & $helper -Provider openai -PluginDataPath 'relative/path' 6>$null } 'absolute' 'Relative path refused'
    Expect-Failure { & $helper -Provider openai -PluginDataPath 'C:relative' 6>$null } 'absolute' 'Drive-relative path refused'
    Expect-Failure { & $helper -Provider openai -PluginDataPath '\relative' 6>$null } 'absolute' 'Root-relative path refused'
    Expect-Failure { & $helper -Provider openai -PluginDataPath ([System.IO.Path]::GetPathRoot($testRoot)) 6>$null } 'drive root' 'Drive root refused'
    Assert (@(Get-ChildItem -LiteralPath (Join-Path $testRoot 'credentials') -Filter '*.tmp').Count -eq 0) 'No temporary ciphertext left behind'
} finally {
    $resolvedRoot = [System.IO.Path]::GetFullPath($testRoot)
    $temporaryParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
    if ([System.IO.Path]::GetDirectoryName($resolvedRoot) -ne $temporaryParent -or
        [System.IO.Path]::GetFileName($resolvedRoot) -notlike 'jml-credential-check-*') { throw 'Unsafe test cleanup path.' }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
    $global:JmlCredentialTestSecret = $null
}
