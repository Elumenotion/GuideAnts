[CmdletBinding()]
param(
    [switch]$StartService,
    [string]$VideoHost = "http://127.0.0.1:8189",
    [string]$ScriptAgentToken = "local-script-agent-test-token",
    [string]$VideoAdminToken = "local-video-admin-test-token",
    [string]$ComposeFile = "docker/compose/comfyui-video-cuda13.standalone.yml",
    [string]$ContentFilesRoot = "tests/runtime/content-files",
    [string]$ArtifactsRoot = "artifacts/infinitetalk",
    [int]$ReadyTimeoutSeconds = 1800,
    [int]$JobTimeoutSeconds = 3600,
    [int]$PollSeconds = 10
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ProjectId = "11111111-1111-1111-1111-111111111111"
$NotebookId = "22222222-2222-2222-2222-222222222222"
$GuideId = "33333333-3333-3333-3333-333333333333"
$OutputName = "sample-cuda13-rtx5090.mp4"
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
    if ([string]::IsNullOrWhiteSpace($text)) { throw "'$Label' returned an empty response." }
    return $text
}

function Invoke-CurlJson {
    param([string]$Label, [string[]]$Arguments)
    $text = Invoke-CurlText $Label $Arguments
    try { return $text | ConvertFrom-Json } catch { throw "'$Label' did not return JSON: $text" }
}

function Invoke-SandboxExecute([string]$Label, [hashtable]$Payload, [string]$PayloadPath) {
    $Payload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $PayloadPath -Encoding utf8NoBOM
    $response = Invoke-CurlJson $Label @(
        "-H", "X-Script-Agent-Token: $ScriptAgentToken",
        "-H", "Content-Type: application/json",
        "--data-binary", "@$PayloadPath",
        "$VideoHost/sandbox/execute"
    )
    if ($null -eq $response.exitCode -or [int]$response.exitCode -ne 0) {
        throw "'$Label' script failed. stderr: $($response.standardError)"
    }
    try { return ([string]$response.standardOutput).Trim() | ConvertFrom-Json }
    catch { throw "'$Label' stdout was not client JSON: $($response.standardOutput)" }
}

function Get-RequiredProperty($Object, [string]$Name, [string]$Context) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "$Context response is missing required '$Name'."
    }
    return [string]$property.Value
}

if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) { throw "curl.exe is required." }
if ($PollSeconds -lt 1) { throw "PollSeconds must be at least 1." }

$Assets = Resolve-RepoPath "tests/assets/infinitetalk"
$Avatar = Join-Path $Assets "avatar.png"
$Voice = Join-Path $Assets "voice.wav"
$Provenance = Join-Path $Assets "ASSET_PROVENANCE.md"
Test-RequiredFile $Provenance "Asset provenance guidance is required."
Test-RequiredFile $Avatar "Licensed avatar.png is not committed. Complete ASSET_PROVENANCE.md before running acceptance."
Test-RequiredFile $Voice "Licensed voice.wav is not committed. Complete ASSET_PROVENANCE.md before running acceptance."

$avatarBytes = [IO.File]::ReadAllBytes($Avatar)
$voiceBytes = [IO.File]::ReadAllBytes($Voice)
if ($avatarBytes.Length -lt 8 -or -not ($avatarBytes[0] -eq 0x89 -and $avatarBytes[1] -eq 0x50 -and $avatarBytes[2] -eq 0x4e -and $avatarBytes[3] -eq 0x47)) {
    throw "avatar.png does not have a PNG signature."
}
if ($voiceBytes.Length -lt 12 -or [Text.Encoding]::ASCII.GetString($voiceBytes, 0, 4) -ne "RIFF" -or [Text.Encoding]::ASCII.GetString($voiceBytes, 8, 4) -ne "WAVE") {
    throw "voice.wav does not have a RIFF/WAVE signature."
}
if (-not (Get-Command ffprobe -ErrorAction SilentlyContinue)) { throw "ffprobe is required to verify the generated MP4." }

$ContentRoot = Resolve-RepoPath $ContentFilesRoot
$NotebookRoot = Join-Path $ContentRoot "acceptance-project/authorized-notebook"
$InputDir = Join-Path $NotebookRoot "Input"
$OutputDir = Join-Path $NotebookRoot "Output"
$MetadataDir = Join-Path $NotebookRoot ".guideants"
$ArtifactDir = Resolve-RepoPath $ArtifactsRoot
New-Item -ItemType Directory -Force -Path $InputDir, $OutputDir, $MetadataDir, $ArtifactDir | Out-Null
@{ ProjectId = $ProjectId; NotebookId = $NotebookId } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $MetadataDir "notebook.json") -Encoding utf8NoBOM
Copy-Item -LiteralPath $Avatar -Destination (Join-Path $InputDir "avatar.png") -Force
Copy-Item -LiteralPath $Voice -Destination (Join-Path $InputDir "voice.wav") -Force
Remove-Item -LiteralPath (Join-Path $OutputDir $OutputName) -Force -ErrorAction SilentlyContinue

$script:TranscriptPath = Join-Path $ArtifactDir ("acceptance-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
New-Item -ItemType File -Force -Path $script:TranscriptPath | Out-Null

if ($StartService) {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "docker is required with -StartService." }
    $Compose = Resolve-RepoPath $ComposeFile
    Test-RequiredFile $Compose "Standalone compose file is required with -StartService."
    $env:GA_CONTENT_FILES_HOST_PATH = $ContentRoot
    $env:GA_SCRIPT_AGENT_TOKEN = $ScriptAgentToken
    $env:GA_COMFYUI_VIDEO_ADMIN_TOKEN = $VideoAdminToken
    & docker compose -f $Compose up -d --no-deps comfyui-video
    if ($LASTEXITCODE -ne 0) { throw "Failed to start the standalone comfyui-video service." }
}

Invoke-CurlText "sandbox health" @("$VideoHost/sandbox/health") | Out-Null
Invoke-CurlJson "video health" @("$VideoHost/video/health") | Out-Null
Invoke-CurlJson "capabilities" @("$VideoHost/video/v1/capabilities") | Out-Null
$readyDeadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
$modelsStatus = Invoke-CurlJson "models" @("-H", "X-Video-Admin-Token: $VideoAdminToken", "$VideoHost/video/v1/models")
if ($modelsStatus.ready -eq $true) {
    Add-Content -LiteralPath $script:TranscriptPath -Value "models already ready; skipping install"
} else {
$install = Invoke-CurlJson "model install" @(
    "-H", "X-Video-Admin-Token: $VideoAdminToken",
    "-H", "Content-Type: application/json",
    "--data", '{"bundle":"infinitetalk-i2v-v1"}',
    "$VideoHost/video/v1/admin/models/install"
)
$installId = Get-RequiredProperty $install "installId" "model install"
$deadline = $readyDeadline
do {
    $installStatus = Invoke-CurlJson "model install status" @(
        "-H", "X-Video-Admin-Token: $VideoAdminToken",
        "$VideoHost/video/v1/admin/models/install/$installId"
    )
    $installState = (Get-RequiredProperty $installStatus "state" "model install status").ToLowerInvariant()
    if ($installState -in @("failed", "cancelled")) { throw "Model installation ended in state '$installState'." }
    if ($installState -eq "completed") { break }
    if ((Get-Date) -ge $deadline) { throw "Timed out waiting for model installation." }
    Start-Sleep -Seconds $PollSeconds
} while ($true)
}

do {
    try {
        Invoke-CurlJson "video ready" @("$VideoHost/video/ready") | Out-Null
        break
    } catch {
        if ((Get-Date) -ge $readyDeadline) { throw }
        Start-Sleep -Seconds $PollSeconds
    }
} while ($true)

$submitFixture = Resolve-RepoPath "tests/requests/infinitetalk/execute-sample.json"
$submitPayload = Get-Content -LiteralPath $submitFixture -Raw | ConvertFrom-Json -AsHashtable
$submit = Invoke-SandboxExecute "submit" $submitPayload (Join-Path $ArtifactDir "submit-request.json")
$jobId = Get-RequiredProperty $submit "jobId" "submit"

$common = @{
    scriptType = "Python"; workingDirectory = "/app/ContentFiles/acceptance-project/authorized-notebook/Output"
    projectId = $ProjectId; notebookId = $NotebookId; guideId = $GuideId; timeoutSeconds = 600
}
$deadline = (Get-Date).AddSeconds($JobTimeoutSeconds)
do {
    $statusPayload = $common.Clone()
    $statusPayload.script = "from guideants_video_client import get_talking_head_job`nimport json`nprint(json.dumps(get_talking_head_job('$jobId'), separators=(',', ':')))"
    $status = Invoke-SandboxExecute "job status" $statusPayload (Join-Path $ArtifactDir "status-request.json")
    $state = (Get-RequiredProperty $status "state" "job status").ToLowerInvariant()
    if ($status.progress) {
        $progress = $status.progress
        $message = if ($progress.message) { [string]$progress.message } else { $state }
        $details = @($message)
        if ($progress.node_class) { $details += "node=$($progress.node_class)" }
        if ($null -ne $progress.step -and $null -ne $progress.max_steps) {
            $details += "step=$($progress.step)/$($progress.max_steps)"
        }
        Write-Host ("[job {0}] {1}" -f $jobId, ($details -join " | "))
    }
    if ($state -eq "completed") { break }
    if ($state -in @("failed", "cancelled")) { throw "Video job ended in state '$state'." }
    if ((Get-Date) -ge $deadline) { throw "Timed out waiting for video job $jobId." }
    Start-Sleep -Seconds $PollSeconds
} while ($true)

$materializePayload = $common.Clone()
$materializePayload.script = "from guideants_video_client import materialize_talking_head_result`nimport json`nprint(json.dumps(materialize_talking_head_result('$jobId', '$OutputName'), separators=(',', ':')))"
Invoke-SandboxExecute "materialize" $materializePayload (Join-Path $ArtifactDir "materialize-request.json") | Out-Null

$files = Invoke-CurlJson "sandbox files" @(
    "-H", "X-Script-Agent-Token: $ScriptAgentToken", "--get",
    "--data-urlencode", "directory=/app/ContentFiles/acceptance-project/authorized-notebook/Output",
    "--data-urlencode", "projectId=$ProjectId", "--data-urlencode", "notebookId=$NotebookId",
    "$VideoHost/sandbox/files"
)
if (($files | ConvertTo-Json -Depth 10) -notmatch [regex]::Escape($OutputName)) {
    throw "/sandbox/files did not list $OutputName."
}

$HostOutput = Join-Path $OutputDir $OutputName
Test-RequiredFile $HostOutput "Materialized MP4 is missing from the host ContentFiles share."
$header = [IO.File]::ReadAllBytes($HostOutput)
if ($header.Length -lt 12 -or [Text.Encoding]::ASCII.GetString($header, 4, 4) -ne "ftyp") {
    throw "Host output is not an ISO Base Media/MP4 file."
}
& ffprobe -v error -select_streams v:0 -show_entries stream=codec_name -of default=noprint_wrappers=1:nokey=1 $HostOutput |
    Add-Content -LiteralPath $script:TranscriptPath
if ($LASTEXITCODE -ne 0) { throw "ffprobe could not read a video stream from the host output." }
$audioDuration = [double](& ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 (Join-Path $InputDir "voice.wav"))
if ($LASTEXITCODE -ne 0) { throw "ffprobe could not read duration from voice.wav." }
$videoDuration = [double](& ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 $HostOutput)
if ($LASTEXITCODE -ne 0) { throw "ffprobe could not read duration from the host output." }
$durationToleranceSeconds = 0.5
if ([Math]::Abs($videoDuration - $audioDuration) -gt $durationToleranceSeconds) {
    throw "Output duration ${videoDuration}s does not match audio duration ${audioDuration}s (tolerance ${durationToleranceSeconds}s)."
}
Add-Content -LiteralPath $script:TranscriptPath -Value ("audio_duration_seconds={0}" -f $audioDuration)
Add-Content -LiteralPath $script:TranscriptPath -Value ("video_duration_seconds={0}" -f $videoDuration)
$PreservedOutput = Join-Path $ArtifactDir $OutputName
Copy-Item -LiteralPath $HostOutput -Destination $PreservedOutput -Force
Write-Host "Acceptance passed. Transcript: $script:TranscriptPath"
Write-Host "Preserved MP4: $PreservedOutput"
