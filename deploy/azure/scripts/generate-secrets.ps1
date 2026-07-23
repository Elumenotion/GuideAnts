# Generate deployment secrets for GuideAnts Azure slim profile.
# Outputs JSON to stdout; never writes secrets to disk unless -KeyVaultName is set.
param(
    [string]$KeyVaultName = "",
    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function New-RandomBase64Key {
    param([int]$ByteLength = 32)
    $bytes = New-Object byte[] $ByteLength
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    return [Convert]::ToBase64String($bytes)
}

function New-RandomToken {
    param([int]$Length = 48)
    $chars = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $bytes = New-Object byte[] $Length
    $rng.GetBytes($bytes)
    return -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

$secrets = [ordered]@{
    jwtSigningKey          = New-RandomToken -Length 64
    settingsSecretsKey     = New-RandomBase64Key -ByteLength 32
    scriptAgentToken       = New-RandomToken -Length 48
    scriptAgentAdminToken  = New-RandomToken -Length 48
    documentServerJwtSecret = New-RandomToken -Length 48
}

if ($KeyVaultName) {
    if (-not $Quiet) { Write-Host "[INFO] Writing secrets to Key Vault '$KeyVaultName'..." -ForegroundColor Blue }
    az keyvault secret set --vault-name $KeyVaultName --name "jwt-signing-key" --value $secrets.jwtSigningKey --output none
    az keyvault secret set --vault-name $KeyVaultName --name "settings-secrets-key-azure-deploy" --value $secrets.settingsSecretsKey --output none
    az keyvault secret set --vault-name $KeyVaultName --name "script-agent-token" --value $secrets.scriptAgentToken --output none
    az keyvault secret set --vault-name $KeyVaultName --name "script-agent-admin-token" --value $secrets.scriptAgentAdminToken --output none
    az keyvault secret set --vault-name $KeyVaultName --name "documentserver-jwt-secret" --value $secrets.documentServerJwtSecret --output none
    if (-not $Quiet) { Write-Host "[SUCCESS] Secrets written to Key Vault." -ForegroundColor Green }
}

$secrets | ConvertTo-Json -Compress
