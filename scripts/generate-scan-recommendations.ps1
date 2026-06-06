param(
    [Parameter()]
    [string]$InputPath = "scan-results.txt",

    [Parameter()]
    [string]$OutputRoot = "scan-recs",

    [Parameter()]
    [string]$SchemaPath = "scripts/scan-rec-output.schema.json",

    [Parameter()]
    [string]$BaseUrl = "",

    [Parameter()]
    [string]$Model = "",

    [Parameter()]
    [string]$ApiKeyEnvVar = "",

    [Parameter()]
    [int]$StartAt = 0,

    [Parameter()]
    [int]$Limit = 0,

    [Parameter()]
    [switch]$Overwrite,

    [Parameter()]
    [int]$PauseMs = 0,

    [Parameter()]
    [int]$RequestTimeoutSec = 120,

    [Parameter()]
    [int]$MaxRetries = 1,

    [Parameter()]
    [int]$RetryDelaySeconds = 3,

    [Parameter()]
    [bool]$ReprocessFailureFallbacks = $true,

    [Parameter()]
    [bool]$ForceDefaultsFromUserConfig = $true
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-ExistingPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        if (-not (Test-Path -LiteralPath $Path)) {
            throw "Path does not exist: $Path"
        }

        return (Resolve-Path -LiteralPath $Path).ProviderPath
    }

    $candidate = Join-Path $RepoRoot $Path
    if (-not (Test-Path -LiteralPath $candidate)) {
        throw "Path does not exist: $candidate"
    }

    return (Resolve-Path -LiteralPath $candidate).ProviderPath
}

function Resolve-OutputPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return (Join-Path $RepoRoot $Path)
}

function Get-TopLevelTomlStringValue {
    param(
        [string[]]$Lines,
        [string]$Key
    )

    if (-not $Lines) {
        return ""
    }

    foreach ($line in $Lines) {
        if ($line -match '^\s*\[') {
            break
        }

        if ($line -match ('^\s*' + [regex]::Escape($Key) + '\s*=\s*"([^"]+)"\s*$')) {
            return $Matches[1]
        }
    }

    return ""
}

function Get-TomlSectionStringValues {
    param(
        [string[]]$Lines,
        [string]$SectionName
    )

    $result = @{}
    if (-not $Lines) {
        return $result
    }

    $inSection = $false
    foreach ($line in $Lines) {
        if ($line -match '^\s*\[([^\]]+)\]\s*$') {
            $inSection = ($Matches[1] -eq $SectionName)
            continue
        }

        if (-not $inSection) {
            continue
        }

        if ($line -match '^\s*([A-Za-z0-9_\-]+)\s*=\s*"([^"]*)"\s*$') {
            $result[$Matches[1]] = $Matches[2]
        }
    }

    return $result
}

function Get-EnvValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $value = [Environment]::GetEnvironmentVariable($Name, "Process")
    if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }

    $value = [Environment]::GetEnvironmentVariable($Name, "User")
    if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }

    $value = [Environment]::GetEnvironmentVariable($Name, "Machine")
    if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }

    return ""
}

function Get-AlertNumber {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Alert,

        [Parameter(Mandatory = $true)]
        [int]$FallbackIndex
    )

    $parsed = 0
    if ($Alert.PSObject.Properties.Name -contains "number" -and [int]::TryParse([string]$Alert.number, [ref]$parsed)) {
        return $parsed
    }

    return $FallbackIndex
}

function Test-IsFailureFallbackRecord {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }

    try {
        $content = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -ErrorAction Stop
        return $content.decision -eq "reject" -and $content.disposition_reason -match "codex_exec_failure|triage_request_failed|triage_parse_failed"
    }
    catch {
        return $false
    }
}

function Get-ResponseOutputJsonText {
    param(
        [Parameter(Mandatory = $true)]
        [object]$ResponseObject
    )

    if ($ResponseObject.PSObject.Properties.Name -contains "output_text" -and -not [string]::IsNullOrWhiteSpace([string]$ResponseObject.output_text)) {
        return [string]$ResponseObject.output_text
    }

    if ($ResponseObject.PSObject.Properties.Name -contains "output") {
        foreach ($outputItem in @($ResponseObject.output)) {
            if ($outputItem.PSObject.Properties.Name -contains "content") {
                foreach ($contentItem in @($outputItem.content)) {
                    if ($contentItem.PSObject.Properties.Name -contains "text" -and -not [string]::IsNullOrWhiteSpace([string]$contentItem.text)) {
                        return [string]$contentItem.text
                    }
                }
            }
        }
    }

    throw "Could not find JSON text in API response payload."
}

function Invoke-ResponsesApiViaPython {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Endpoint,

        [Parameter(Mandatory = $true)]
        [string]$ApiKey,

        [Parameter(Mandatory = $true)]
        [string]$RequestBodyJson,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutSec
    )

    $pythonScript = @'
import json
import os
import sys
import urllib.request
import urllib.error

url = sys.argv[1]
timeout_sec = int(sys.argv[2])
api_key = os.environ.get("_QA_API_KEY", "")
body = sys.stdin.read().encode("utf-8")

result = {"ok": False, "status": None, "body": "", "error": ""}
try:
    req = urllib.request.Request(
        url=url,
        data=body,
        headers={"api-key": api_key, "Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=timeout_sec) as resp:
        result["status"] = int(resp.getcode())
        result["body"] = resp.read().decode("utf-8", errors="replace")
        result["ok"] = 200 <= result["status"] < 300
except urllib.error.HTTPError as e:
    result["status"] = int(e.code)
    try:
        result["body"] = e.read().decode("utf-8", errors="replace")
    except Exception:
        result["body"] = ""
    result["error"] = f"HTTPError: {e}"
except Exception as e:
    result["error"] = repr(e)

print(json.dumps(result))
'@

    $priorTempKey = [Environment]::GetEnvironmentVariable("_QA_API_KEY", "Process")
    [Environment]::SetEnvironmentVariable("_QA_API_KEY", $ApiKey, "Process")
    try {
        $raw = $RequestBodyJson | & python -c $pythonScript $Endpoint $TimeoutSec
        if ($LASTEXITCODE -ne 0) {
            return [pscustomobject]@{
                ok = $false
                status = $null
                body = ""
                error = "python transport exited with code $LASTEXITCODE"
            }
        }

        $text = if ($raw -is [System.Array]) { ($raw -join "`n") } else { [string]$raw }
        try {
            return ($text | ConvertFrom-Json -ErrorAction Stop)
        }
        catch {
            return [pscustomobject]@{
                ok = $false
                status = $null
                body = ""
                error = "python transport returned non-JSON payload: $text"
            }
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable("_QA_API_KEY", $priorTempKey, "Process")
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedInputPath = Resolve-ExistingPath -Path $InputPath -RepoRoot $repoRoot
$resolvedSchemaPath = Resolve-ExistingPath -Path $SchemaPath -RepoRoot $repoRoot
$resolvedOutputRoot = Resolve-OutputPath -Path $OutputRoot -RepoRoot $repoRoot

if ($ForceDefaultsFromUserConfig) {
    $userConfigPath = Join-Path $env:USERPROFILE ".codex\config.toml"
    if (Test-Path -LiteralPath $userConfigPath) {
        $userConfigLines = Get-Content -LiteralPath $userConfigPath
        $providerId = Get-TopLevelTomlStringValue -Lines $userConfigLines -Key "model_provider"
        $userModel = Get-TopLevelTomlStringValue -Lines $userConfigLines -Key "model"

        if ([string]::IsNullOrWhiteSpace($Model) -and -not [string]::IsNullOrWhiteSpace($userModel)) {
            $Model = $userModel
        }

        if (-not [string]::IsNullOrWhiteSpace($providerId)) {
            $providerSection = Get-TomlSectionStringValues -Lines $userConfigLines -SectionName "model_providers.$providerId"

            if ([string]::IsNullOrWhiteSpace($BaseUrl) -and $providerSection.ContainsKey("base_url")) {
                $BaseUrl = $providerSection["base_url"]
            }

            if ([string]::IsNullOrWhiteSpace($ApiKeyEnvVar) -and $providerSection.ContainsKey("env_key")) {
                $ApiKeyEnvVar = $providerSection["env_key"]
            }
        }
    }
}

if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    throw "BaseUrl is empty. Set -BaseUrl or configure model_providers.<id>.base_url in ~/.codex/config.toml."
}

if ([string]::IsNullOrWhiteSpace($Model)) {
    throw "Model is empty. Set -Model or configure top-level model in ~/.codex/config.toml."
}

if ([string]::IsNullOrWhiteSpace($ApiKeyEnvVar)) {
    throw "ApiKeyEnvVar is empty. Set -ApiKeyEnvVar or configure model_providers.<id>.env_key in ~/.codex/config.toml."
}

$apiKey = Get-EnvValue -Name $ApiKeyEnvVar
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    throw "Environment variable '$ApiKeyEnvVar' is not set in process/user/machine scope."
}

$baseUrlTrimmed = $BaseUrl.TrimEnd("/")
$responsesEndpoint = "$baseUrlTrimmed/responses"

$validDir = Join-Path $resolvedOutputRoot "valid"
$rejectDir = Join-Path $resolvedOutputRoot "reject"
$tmpDir = Join-Path $resolvedOutputRoot "_tmp"

New-Item -ItemType Directory -Path $validDir -Force | Out-Null
New-Item -ItemType Directory -Path $rejectDir -Force | Out-Null
New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null

$schemaObject = Get-Content -LiteralPath $resolvedSchemaPath -Raw | ConvertFrom-Json
$inputPayload = Get-Content -LiteralPath $resolvedInputPath -Raw | ConvertFrom-Json

$alerts = @()
if ($inputPayload -is [System.Array]) {
    $alerts = @($inputPayload)
}
elseif ($inputPayload.PSObject.Properties.Name -contains "alerts") {
    $alerts = @($inputPayload.alerts)
}
else {
    throw "Expected scan input to be a JSON array or object with an 'alerts' property."
}

if ($StartAt -gt 0) {
    $alerts = @($alerts | Select-Object -Skip $StartAt)
}

if ($Limit -gt 0) {
    $alerts = @($alerts | Select-Object -First $Limit)
}

$systemPrompt = @'
You are reviewing a single GitHub code-scanning alert for a specific repository.

Decide if this alert is actionable:
- "valid": true positive that should be fixed.
- "reject": false positive, duplicate, mitigated, or not applicable.

Return ONLY a JSON object that matches the provided schema.
Recommendations must be concrete and concise.
'@

$writtenValid = 0
$writtenReject = 0
$skipped = 0
$failures = 0
$total = $alerts.Count
$index = 0

foreach ($alert in $alerts) {
    $index++
    $alertNumber = Get-AlertNumber -Alert $alert -FallbackIndex ($StartAt + $index)
    $baseName = "{0:D6}" -f $alertNumber
    $validPath = Join-Path $validDir "$baseName.json"
    $rejectPath = Join-Path $rejectDir "$baseName.json"
    $rawResponsePath = Join-Path $tmpDir "$baseName.response.json"

    if (-not $Overwrite) {
        if (Test-Path -LiteralPath $validPath) {
            $skipped++
            Write-Host "[$index/$total] Skipping alert #$alertNumber (valid output exists)."
            continue
        }

        if (Test-Path -LiteralPath $rejectPath) {
            if ($ReprocessFailureFallbacks -and (Test-IsFailureFallbackRecord -Path $rejectPath)) {
                Remove-Item -LiteralPath $rejectPath -ErrorAction SilentlyContinue
                Write-Host "[$index/$total] Reprocessing alert #$alertNumber (existing reject is failure fallback)."
            }
            else {
                $skipped++
                Write-Host "[$index/$total] Skipping alert #$alertNumber (reject output exists)."
                continue
            }
        }
    }

    if ($Overwrite) {
        Remove-Item -LiteralPath $validPath -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $rejectPath -ErrorAction SilentlyContinue
    }

    Remove-Item -LiteralPath $rawResponsePath -ErrorAction SilentlyContinue
    Write-Host "[$index/$total] Evaluating alert #$alertNumber..."

    $alertJson = $alert | ConvertTo-Json -Depth 100 -Compress
    $requestPayload = [ordered]@{
        model = $Model
        input = @(
            @{
                role = "system"
                content = @(
                    @{
                        type = "input_text"
                        text = $systemPrompt
                    }
                )
            },
            @{
                role = "user"
                content = @(
                    @{
                        type = "input_text"
                        text = "Alert JSON:`n$alertJson"
                    }
                )
            }
        )
        text = @{
            format = @{
                type = "json_schema"
                name = "scan_recommendation"
                strict = $true
                schema = $schemaObject
            }
        }
    }

    $requestBodyJson = $requestPayload | ConvertTo-Json -Depth 100 -Compress
    $attempt = 0
    $response = $null
    $responseText = ""
    $requestErrorMessage = ""
    $requestErrorStatus = ""
    $requestErrorBody = ""

    do {
        $attempt++
        try {
            $transport = Invoke-ResponsesApiViaPython -Endpoint $responsesEndpoint -ApiKey $apiKey -RequestBodyJson $requestBodyJson -TimeoutSec $RequestTimeoutSec
            if ($transport.ok) {
                $response = $transport
                $responseText = [string]$transport.body
                break
            }

            $requestErrorMessage = [string]$transport.error
            $requestErrorStatus = [string]$transport.status
            $requestErrorBody = [string]$transport.body
        }
        catch {
            $requestErrorMessage = $_.Exception.Message
        }

        if ($attempt -lt ($MaxRetries + 1)) {
            Write-Host "[$index/$total] Alert #$alertNumber request failed on attempt $attempt/$($MaxRetries + 1), retrying..."
            Start-Sleep -Seconds ([Math]::Max(1, $RetryDelaySeconds))
        }
    } while ($attempt -lt ($MaxRetries + 1))

    if (-not [string]::IsNullOrWhiteSpace($responseText)) {
        Set-Content -LiteralPath $rawResponsePath -Value $responseText -Encoding utf8
    }

    if ($null -eq $response) {
        $failures++
        $status = $requestErrorStatus
        $body = $requestErrorBody
        $message = $requestErrorMessage

        $rejectRecord = [ordered]@{
            alert_number = $alertNumber
            html_url = $alert.html_url
            rule_id = $alert.rule.id
            decision = "reject"
            disposition_reason = "triage_request_failed"
            recommendation = [ordered]@{
                summary = "Automated triage request failed."
                actions = @(
                    "Verify Azure endpoint connectivity and credentials for this shell.",
                    "Re-run with -Overwrite after fixing provider connectivity."
                )
                code_pointers = @()
                references = @()
            }
            confidence = "low"
            tags = @("triage-error", "manual-review")
            generated_at = (Get-Date).ToString("o")
            source = [ordered]@{
                tool = "azure.responses.rest"
                endpoint = $responsesEndpoint
                model = $Model
                api_key_env_var = $ApiKeyEnvVar
            }
            triage_error = [ordered]@{
                message = $message
                status = $status
                body = $body
            }
        }

        $rejectRecord | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath $rejectPath -Encoding utf8
        $writtenReject++
        Write-Host "[$index/$total] Alert #$alertNumber -> reject (triage_request_failed)."

        if ($PauseMs -gt 0) {
            Start-Sleep -Milliseconds $PauseMs
        }

        continue
    }

    $parsedResponse = $null
    $recommendation = $null
    $parseFailure = $false
    $parseFailureMessage = ""

    try {
        $parsedResponse = $responseText | ConvertFrom-Json -ErrorAction Stop
        $jsonText = Get-ResponseOutputJsonText -ResponseObject $parsedResponse
        $recommendation = $jsonText | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        $parseFailure = $true
        $parseFailureMessage = $_.Exception.Message
    }

    if ($parseFailure -or $null -eq $recommendation -or ($recommendation.decision -ne "valid" -and $recommendation.decision -ne "reject")) {
        $failures++
        $rejectRecord = [ordered]@{
            alert_number = $alertNumber
            html_url = $alert.html_url
            rule_id = $alert.rule.id
            decision = "reject"
            disposition_reason = "triage_parse_failed"
            recommendation = [ordered]@{
                summary = "Automated triage response could not be parsed."
                actions = @(
                    "Inspect raw API response under scan-recs/_tmp for this alert.",
                    "Re-run with -Overwrite after correcting response formatting constraints."
                )
                code_pointers = @()
                references = @()
            }
            confidence = "low"
            tags = @("triage-error", "manual-review")
            generated_at = (Get-Date).ToString("o")
            source = [ordered]@{
                tool = "azure.responses.rest"
                endpoint = $responsesEndpoint
                model = $Model
                api_key_env_var = $ApiKeyEnvVar
            }
            triage_error = [ordered]@{
                message = $parseFailureMessage
            }
        }

        $rejectRecord | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath $rejectPath -Encoding utf8
        $writtenReject++
        Write-Host "[$index/$total] Alert #$alertNumber -> reject (triage_parse_failed)."

        if ($PauseMs -gt 0) {
            Start-Sleep -Milliseconds $PauseMs
        }

        continue
    }

    $destinationPath = if ($recommendation.decision -eq "valid") { $validPath } else { $rejectPath }
    $record = [ordered]@{
        alert_number = $alertNumber
        html_url = $alert.html_url
        rule_id = $alert.rule.id
        rule_severity = $alert.rule.severity
        security_severity_level = $alert.rule.security_severity_level
        decision = $recommendation.decision
        disposition_reason = $recommendation.disposition_reason
        recommendation = $recommendation.recommendation
        confidence = $recommendation.confidence
        tags = $recommendation.tags
        generated_at = (Get-Date).ToString("o")
        source = [ordered]@{
            tool = "azure.responses.rest"
            endpoint = $responsesEndpoint
            model = $Model
            api_key_env_var = $ApiKeyEnvVar
        }
    }

    $record | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath $destinationPath -Encoding utf8

    if ($recommendation.decision -eq "valid") {
        $writtenValid++
    }
    else {
        $writtenReject++
    }

    Write-Host "[$index/$total] Alert #$alertNumber -> $($recommendation.decision)."

    if ($PauseMs -gt 0) {
        Start-Sleep -Milliseconds $PauseMs
    }
}

Write-Host ""
Write-Host "Completed recommendation generation."
Write-Host "Input: $resolvedInputPath"
Write-Host "Output root: $resolvedOutputRoot"
Write-Host "Endpoint: $responsesEndpoint"
Write-Host "Model: $Model"
Write-Host "API key env var: $ApiKeyEnvVar"
Write-Host "Valid files: $writtenValid"
Write-Host "Reject files: $writtenReject"
Write-Host "Failures: $failures"
Write-Host "Skipped existing: $skipped"
