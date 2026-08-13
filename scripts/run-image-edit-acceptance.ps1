[CmdletBinding()]
param(
    [switch]$StartService,
    [string]$VideoHost = "http://127.0.0.1:8189",
    [string]$ScriptAgentToken = "local-script-agent-test-token",
    [string]$VideoAdminToken = "local-video-admin-test-token",
    [string]$ComposeFile = "docker/compose/comfyui-video-rocm.standalone.yml",
    [string]$ContentFilesRoot = "artifacts",
    [string]$ArtifactsRoot = "artifacts/qwen-image-edit",
    [string]$InputImage = "C:\models\qwen-outputs\jobs\5fcb173e-f942-4d4f-829c-4c56b9bf0763\input.png",
    [string]$Prompt = "this partial image is a man in an office with bookshelves. complete the scene by adding the missing elements",
    [string]$OutputName = "office-edit-ac2.png",
    [string]$WarmOutputName = "office-edit-ac3-warm.png",
    [switch]$SkipWarmRun,
    [int]$ReadyTimeoutSeconds = 1800,
    [int]$JobTimeoutSeconds = 3600,
    [int]$PollSeconds = 10
)

$ErrorActionPreference = "Stop"

$ProjectId = "11111111-1111-1111-1111-111111111111"
$NotebookId = "22222222-2222-2222-2222-222222222222"
$GuideId = "33333333-3333-3333-3333-333333333333"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Resolve-RepoPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Test-RequiredFile([string]$Path, [string]$Message) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Message`nMissing: $Path" }
    if ((Get-Item -LiteralPath $Path).Length -eq 0) { throw "Required file is empty: $Path" }
}

function Invoke-CurlText {
    param([string]$Label, [string[]]$Arguments)
    Add-Content -LiteralPath $script:TranscriptPath -Value "`n=== $Label ===`n> curl $($Arguments -join ' ')"
    $output = & curl.exe --fail --silent --show-error @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        Add-Content -LiteralPath $script:TranscriptPath -Value ($output | Out-String)
        throw "curl failed during '$Label' (exit $LASTEXITCODE). See $script:TranscriptPath"
    }
    $text = ($output | Out-String).Trim()
    Add-Content -LiteralPath $script:TranscriptPath -Value $text
    return $text
}

function Invoke-CurlJson {
    param([string]$Label, [string[]]$Arguments)
    $text = Invoke-CurlText $Label $Arguments
    try { return $text | ConvertFrom-Json } catch { throw "'$Label' did not return JSON: $text" }
}

function Write-Utf8NoBomFile {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Content)
    $encoding = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function ConvertTo-Hashtable($InputObject) {
    if ($InputObject -is [hashtable]) { return $InputObject }
    $hash = @{}
    foreach ($property in $InputObject.PSObject.Properties) { $hash[$property.Name] = $property.Value }
    return $hash
}

function ConvertTo-JsonPayload($Payload) {
    $hash = ConvertTo-Hashtable $Payload
    $obj = New-Object PSObject
    foreach ($key in $hash.Keys) { $obj | Add-Member -MemberType NoteProperty -Name $key -Value $hash[$key] }
    return ($obj | ConvertTo-Json -Depth 8 -Compress)
}

function Get-ResponseProperty($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object.PSObject.Properties.Name -contains $Name) { return $Object.$Name }
    $camel = $Name.Substring(0, 1).ToLowerInvariant() + $Name.Substring(1)
    if ($Object.PSObject.Properties.Name -contains $camel) { return $Object.$camel }
    return $null
}

function Invoke-SandboxExecute([string]$Label, $Payload, [string]$PayloadPath) {
    Write-Utf8NoBomFile -Path $PayloadPath -Content (ConvertTo-JsonPayload $Payload)
    $response = Invoke-CurlJson $Label @(
        "-H", "X-Script-Agent-Token: $ScriptAgentToken",
        "-H", "Content-Type: application/json",
        "--data-binary", "@$PayloadPath",
        "$VideoHost/sandbox/execute"
    )
    $exitCode = Get-ResponseProperty $response 'ExitCode'
    if ($null -eq $exitCode) { $exitCode = Get-ResponseProperty $response 'exitCode' }
    if ($null -eq $exitCode -or [int]$exitCode -ne 0) {
        $stderr = Get-ResponseProperty $response 'StandardError'
        if ($null -eq $stderr) { $stderr = Get-ResponseProperty $response 'standardError' }
        throw "'$Label' script failed. stderr: $stderr"
    }
    $stdout = Get-ResponseProperty $response 'StandardOutput'
    if ($null -eq $stdout) { $stdout = Get-ResponseProperty $response 'standardOutput' }
    try { return ([string]$stdout).Trim() | ConvertFrom-Json }
    catch { throw "'$Label' stdout was not client JSON: $stdout" }
}

function Wait-ImageJob {
    param(
        [string]$JobId,
        [string]$Label,
        [hashtable]$Common,
        [string]$ArtifactDir,
        [datetime]$Deadline
    )
    do {
        $statusPayload = $Common.Clone()
        $statusPayload.script = "from guideants_video_client import get_image_job`nimport json`nprint(json.dumps(get_image_job('$JobId'), separators=(',', ':')))"
        $status = Invoke-SandboxExecute "$Label status" $statusPayload (Join-Path $ArtifactDir "$Label-status.json")
        $state = (Get-ResponseProperty $status "state").ToLowerInvariant()
        $progress = Get-ResponseProperty $status 'progress'
        if ($null -ne $progress) {
            $message = Get-ResponseProperty $progress 'message'
            if ([string]::IsNullOrWhiteSpace([string]$message)) { $message = $state }
            Write-Host ("[{0}] {1}" -f $Label, $message)
        }
        if ($state -eq "completed") { return $status }
        if ($state -in @("failed", "cancelled")) { throw "$Label ended in state '$state'." }
        if ((Get-Date) -ge $Deadline) { throw "Timed out waiting for $Label job $JobId." }
        Start-Sleep -Seconds $PollSeconds
    } while ($true)
}

$ContentRoot = Resolve-RepoPath $ContentFilesRoot
$ArtifactDir = Resolve-RepoPath $ArtifactsRoot
$InputDir = Join-Path $ContentRoot "acceptance-project\authorized-notebook\Input"
$OutputDir = Join-Path $ContentRoot "acceptance-project\authorized-notebook\Output"
New-Item -ItemType Directory -Force -Path $InputDir, $OutputDir, $ArtifactDir | Out-Null

$InputImage = Resolve-RepoPath $InputImage
Test-RequiredFile $InputImage "AC2 input image is required."
$InputName = "office-partial.png"
Copy-Item -LiteralPath $InputImage -Destination (Join-Path $InputDir $InputName) -Force
Remove-Item -LiteralPath (Join-Path $OutputDir $OutputName) -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $OutputDir $WarmOutputName) -Force -ErrorAction SilentlyContinue

$script:TranscriptPath = Join-Path $ArtifactDir ("acceptance-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
New-Item -ItemType File -Force -Path $script:TranscriptPath | Out-Null

if ($StartService) {
    $Compose = Resolve-RepoPath $ComposeFile
    $env:GA_CONTENT_FILES_HOST_PATH = $ContentRoot
    $env:GA_SCRIPT_AGENT_TOKEN = $ScriptAgentToken
    $env:GA_SCRIPT_AGENT_ADMIN_TOKEN = $ScriptAgentToken
    $env:GA_COMFYUI_VIDEO_ADMIN_TOKEN = $VideoAdminToken
    $libRocdxg = Resolve-RepoPath 'docker/volumes/rocm-wsl/lib/librocdxg.so'
    if (-not (Test-Path -LiteralPath $libRocdxg)) {
        $libRocdxg = Resolve-RepoPath 'installer/docker/volumes/rocm-wsl/lib/librocdxg.so'
    }
    if (-not (Test-Path -LiteralPath $libRocdxg)) {
        throw "ROCm service start requires staged librocdxg at docker/volumes/rocm-wsl/lib/librocdxg.so"
    }
    $env:GA_ROCM_WSL_LIBROCDXG_HOST_PATH = $libRocdxg
    Push-Location (Split-Path $Compose)
    & docker compose -f (Split-Path -Leaf $Compose) up -d --no-deps comfyui-video
    if ($LASTEXITCODE -ne 0) { throw "Failed to start comfyui-video service." }
    Pop-Location
}

$serviceDeadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
do {
    try {
        Invoke-CurlText "sandbox health" @("$VideoHost/sandbox/health") | Out-Null
        break
    } catch {
        if ((Get-Date) -ge $serviceDeadline) { throw }
        Start-Sleep -Seconds $PollSeconds
    }
} while ($true)

Invoke-CurlJson "video health" @("$VideoHost/video/health") | Out-Null
$capabilities = Invoke-CurlJson "capabilities" @("$VideoHost/video/v1/capabilities")
if ((Get-ResponseProperty $capabilities 'image_ready') -ne $true) {
    throw "image_ready is false: $($capabilities | ConvertTo-Json -Depth 6 -Compress)"
}

$common = @{
    scriptType = "Python"
    workingDirectory = "/app/ContentFiles/acceptance-project/authorized-notebook/Output"
    projectId = $ProjectId
    notebookId = $NotebookId
    guideId = $GuideId
    timeoutSeconds = 1800
}

function Invoke-ImageEditJob {
    param(
        [string]$Label,
        [string]$SourceRelative,
        [string]$ResultName
    )
    $escapedPrompt = $Prompt.Replace("'", "\'")
    $submitPayload = $common.Clone()
    $submitPayload.workingDirectory = "/app/ContentFiles/acceptance-project/authorized-notebook/Output"
    $submitPayload.script = @"
from guideants_video_client import submit_image_edit
import json
print(json.dumps(submit_image_edit(
    '../Input/$SourceRelative',
    '$escapedPrompt',
    '$ResultName',
    parameters={'steps': 4, 'cfg': 1.0, 'seed': 8859265813802057645},
), separators=(',', ':')))
"@
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $submit = Invoke-SandboxExecute "$Label submit" $submitPayload (Join-Path $ArtifactDir "$Label-submit.json")
    $jobId = Get-ResponseProperty $submit 'jobId'
    $deadline = (Get-Date).AddSeconds($JobTimeoutSeconds)
    Wait-ImageJob -JobId $jobId -Label $Label -Common $common -ArtifactDir $ArtifactDir -Deadline $deadline | Out-Null
    $materializePayload = $common.Clone()
    $materializePayload.script = "from guideants_video_client import materialize_image_result`nimport json`nprint(json.dumps(materialize_image_result('$jobId', '$ResultName'), separators=(',', ':')))"
    Invoke-SandboxExecute "$Label materialize" $materializePayload (Join-Path $ArtifactDir "$Label-materialize.json") | Out-Null
    $sw.Stop()
    return @{
        jobId = $jobId
        elapsedSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        outputPath = Join-Path $OutputDir $ResultName
    }
}

$first = Invoke-ImageEditJob -Label "ac2" -SourceRelative $InputName -ResultName $OutputName
Test-RequiredFile $first.outputPath "AC2 output PNG is missing."
$pngHeader = [IO.File]::ReadAllBytes($first.outputPath)
if ($pngHeader.Length -lt 8 -or $pngHeader[0] -ne 0x89 -or $pngHeader[1] -ne 0x50) {
    throw "AC2 output is not a PNG."
}
Copy-Item -LiteralPath $first.outputPath -Destination (Join-Path $ArtifactDir $OutputName) -Force
Write-Host "AC2 candidate PNG produced in $($first.elapsedSeconds)s (job $($first.jobId)); visual acceptance is required."

if ($SkipWarmRun) {
    Write-Host "Technical image-edit check completed; AC2 requires visual review. Transcript: $script:TranscriptPath"
    return
}

$second = Invoke-ImageEditJob -Label "ac3" -SourceRelative $InputName -ResultName $WarmOutputName
Test-RequiredFile $second.outputPath "AC3 warm output PNG is missing."
Copy-Item -LiteralPath $second.outputPath -Destination (Join-Path $ArtifactDir $WarmOutputName) -Force
Write-Host "AC3 warm second job completed in $($second.elapsedSeconds)s (job $($second.jobId))."
if ($second.elapsedSeconds -gt 600) {
    throw "AC3 failed: warm job took $($second.elapsedSeconds)s (>600s suggests a full reload)."
}

Write-Host "Technical image-edit checks passed; AC2 requires visual review. Transcript: $script:TranscriptPath"
