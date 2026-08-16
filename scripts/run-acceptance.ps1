[CmdletBinding()]
param(
    [ValidateSet('cuda13', 'rocm')]
    [string]$Backend = 'rocm',
    [switch]$StartService,
    [string]$VideoHost = "http://127.0.0.1:8189",
    [string]$ScriptAgentToken = "local-script-agent-test-token",
    [string]$VideoAdminToken = "local-video-admin-test-token",
    [string]$ComposeFile = "",
    [string]$ContentFilesRoot = "artifacts",
    [string]$ArtifactsRoot = "artifacts/infinitetalk",
    [string]$SubmitFixture = "",
    [string]$OutputName = "",
    [int]$ReadyTimeoutSeconds = 1800,
    [int]$JobTimeoutSeconds = 3600,
    [int]$QueuedTimeoutSeconds = 300,
    [int]$PollSeconds = 10
)

$ErrorActionPreference = "Stop"

$ProjectId = "11111111-1111-1111-1111-111111111111"
$NotebookId = "22222222-2222-2222-2222-222222222222"
$GuideId = "33333333-3333-3333-3333-333333333333"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

if ([string]::IsNullOrWhiteSpace($ComposeFile)) {
    $ComposeFile = if ($Backend -eq 'rocm') {
        'docker/compose/comfyui-video-rocm.standalone.yml'
    } else {
        'docker/compose/comfyui-video-cuda13.standalone.yml'
    }
}
if ([string]::IsNullOrWhiteSpace($SubmitFixture)) {
    $SubmitFixture = Join-Path $ArtifactsRoot 'submit-request.json'
}
if ([string]::IsNullOrWhiteSpace($OutputName)) {
    $OutputName = if ($Backend -eq 'rocm') { 'sample-rocm-gfx1151.mkv' } else { 'sample-cuda13-rtx5090.mkv' }
}

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

function Write-Utf8NoBomFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )
    $encoding = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function ConvertTo-Hashtable($InputObject) {
    if ($InputObject -is [hashtable]) { return $InputObject }
    $hash = @{}
    foreach ($property in $InputObject.PSObject.Properties) {
        $hash[$property.Name] = $property.Value
    }
    return $hash
}

function ConvertTo-JsonPayload($Payload) {
    $hash = ConvertTo-Hashtable $Payload
    $obj = New-Object PSObject
    foreach ($key in $hash.Keys) {
        $obj | Add-Member -MemberType NoteProperty -Name $key -Value $hash[$key]
    }
    return ($obj | ConvertTo-Json -Depth 8 -Compress)
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

function Get-RequiredProperty($Object, [string]$Name, [string]$Context) {
    if ($null -eq $Object) {
        throw "$Context response is missing required '$Name'."
    }
    if ($Object -is [hashtable]) {
        if (-not $Object.ContainsKey($Name) -or [string]::IsNullOrWhiteSpace([string]$Object[$Name])) {
            throw "$Context response is missing required '$Name'."
        }
        return [string]$Object[$Name]
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "$Context response is missing required '$Name'."
    }
    return [string]$property.Value
}

function Get-ResponseProperty($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [hashtable]) {
        if ($Object.ContainsKey($Name)) { return $Object[$Name] }
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Clear-ComfyQueue {
    $clearPath = Join-Path $ArtifactDir "queue-clear-request.json"
    Write-Utf8NoBomFile -Path $clearPath -Content '{"clear": true}'
    try {
        docker exec compose-comfyui-video-1 curl -s -X POST http://127.0.0.1:8188/interrupt | Out-Null
    } catch {
        # ComfyUI may not be running inside docker during local script tests.
    }
    try {
        docker exec compose-comfyui-video-1 curl -s -X POST http://127.0.0.1:8188/queue `
            -H "Content-Type: application/json" `
            --data-binary "@$clearPath" | Out-Null
    } catch {
        # Ignore queue clear failures when the service is not docker-managed.
    }
}

if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) { throw "curl.exe is required." }
if ($PollSeconds -lt 1) { throw "PollSeconds must be at least 1." }

$Assets = Resolve-RepoPath "tests/assets/infinitetalk"
$NotebookRoot = Join-Path (Resolve-RepoPath $ContentFilesRoot) "acceptance-project/authorized-notebook"
$Avatar = Join-Path $NotebookRoot "Input/avatar.png"
$Voice = Join-Path $NotebookRoot "Input/voice.wav"
if (-not (Test-Path -LiteralPath $Avatar)) {
    $Avatar = Join-Path $Assets "avatar.png"
    $Voice = Join-Path $Assets "voice.wav"
    $Provenance = Join-Path $Assets "ASSET_PROVENANCE.md"
    Test-RequiredFile $Provenance "Asset provenance guidance is required."
}
Test-RequiredFile $Avatar "avatar.png is required in artifacts or tests/assets."
Test-RequiredFile $Voice "voice.wav is required in artifacts or tests/assets."

$avatarBytes = [IO.File]::ReadAllBytes($Avatar)
$voiceBytes = [IO.File]::ReadAllBytes($Voice)
if ($avatarBytes.Length -lt 8 -or -not ($avatarBytes[0] -eq 0x89 -and $avatarBytes[1] -eq 0x50 -and $avatarBytes[2] -eq 0x4e -and $avatarBytes[3] -eq 0x47)) {
    throw "avatar.png does not have a PNG signature."
}
if ($voiceBytes.Length -lt 12 -or [Text.Encoding]::ASCII.GetString($voiceBytes, 0, 4) -ne "RIFF" -or [Text.Encoding]::ASCII.GetString($voiceBytes, 8, 4) -ne "WAVE") {
    throw "voice.wav does not have a RIFF/WAVE signature."
}
if (-not (Get-Command ffprobe -ErrorAction SilentlyContinue)) { throw "ffprobe is required to verify the generated MKV." }

$ContentRoot = Resolve-RepoPath $ContentFilesRoot
$InputDir = Join-Path $NotebookRoot "Input"
$OutputDir = Join-Path $NotebookRoot "Output"
$MetadataDir = Join-Path $NotebookRoot ".guideants"
$ArtifactDir = Resolve-RepoPath $ArtifactsRoot
New-Item -ItemType Directory -Force -Path $InputDir, $OutputDir, $MetadataDir, $ArtifactDir | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $MetadataDir "notebook.json"))) {
    Write-Utf8NoBomFile -Path (Join-Path $MetadataDir "notebook.json") -Content (@{ ProjectId = $ProjectId; NotebookId = $NotebookId } | ConvertTo-Json)
}
if ($Avatar -notlike "$InputDir*") {
    Copy-Item -LiteralPath $Avatar -Destination (Join-Path $InputDir "avatar.png") -Force
    Copy-Item -LiteralPath $Voice -Destination (Join-Path $InputDir "voice.wav") -Force
}
$submitPayloadPath = Resolve-RepoPath $SubmitFixture
Test-RequiredFile $submitPayloadPath "Submit fixture is required."
$submitPayload = ConvertTo-Hashtable (Get-Content -LiteralPath $submitPayloadPath -Raw | ConvertFrom-Json)
if ($submitPayload.script -match "output_filename='[^']+'") {
    $submitPayload.script = [regex]::Replace(
        $submitPayload.script,
        "output_filename='[^']+'",
        "output_filename='$OutputName'"
    )
}
Remove-Item -LiteralPath (Join-Path $OutputDir $OutputName) -Force -ErrorAction SilentlyContinue

$script:TranscriptPath = Join-Path $ArtifactDir ("acceptance-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
New-Item -ItemType File -Force -Path $script:TranscriptPath | Out-Null

if ($StartService) {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "docker is required with -StartService." }
    $Compose = Resolve-RepoPath $ComposeFile
    Test-RequiredFile $Compose "Standalone compose file is required with -StartService."
    $env:GA_CONTENT_FILES_HOST_PATH = $ContentRoot
    $env:GA_SCRIPT_AGENT_TOKEN = $ScriptAgentToken
    $env:GA_SCRIPT_AGENT_ADMIN_TOKEN = $ScriptAgentToken
    $env:GA_COMFYUI_VIDEO_ADMIN_TOKEN = $VideoAdminToken
    if ($Backend -eq 'rocm') {
        $libRocdxg = Resolve-RepoPath 'docker/volumes/rocm-wsl/lib/librocdxg.so'
        if (-not (Test-Path -LiteralPath $libRocdxg)) {
            $libRocdxg = Resolve-RepoPath 'installer/docker/volumes/rocm-wsl/lib/librocdxg.so'
        }
        if (-not (Test-Path -LiteralPath $libRocdxg)) {
            throw "ROCm service start requires staged librocdxg at docker/volumes/rocm-wsl/lib/librocdxg.so"
        }
        $env:GA_ROCM_WSL_LIBROCDXG_HOST_PATH = $libRocdxg
    }
    & docker compose -f $Compose up -d --no-deps comfyui-video
    if ($LASTEXITCODE -ne 0) { throw "Failed to start the standalone comfyui-video service." }
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
Invoke-CurlJson "capabilities" @("$VideoHost/video/v1/capabilities") | Out-Null
$readyDeadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
$modelsStatus = Invoke-CurlJson "models" @("-H", "X-Video-Admin-Token: $VideoAdminToken", "$VideoHost/video/v1/models")
if ((Get-ResponseProperty $modelsStatus 'ready') -eq $true) {
    Add-Content -LiteralPath $script:TranscriptPath -Value "models already ready; skipping install"
} else {
$installPayloadPath = Join-Path $ArtifactDir "model-install-request.json"
Write-Utf8NoBomFile -Path $installPayloadPath -Content '{"bundle":"infinitetalk-i2v-v1"}'
$install = Invoke-CurlJson "model install" @(
    "-H", "X-Video-Admin-Token: $VideoAdminToken",
    "-H", "Content-Type: application/json",
    "--data-binary", "@$installPayloadPath",
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

$submitFixture = $submitPayloadPath
Clear-ComfyQueue
$submit = Invoke-SandboxExecute "submit" $submitPayload (Join-Path $ArtifactDir "submit-execute-request.json")
$jobId = Get-RequiredProperty $submit "jobId" "submit"

$common = @{
    scriptType = "Python"; workingDirectory = "/app/ContentFiles/acceptance-project/authorized-notebook/Output"
    projectId = $ProjectId; notebookId = $NotebookId; guideId = $GuideId; timeoutSeconds = 600
}
$deadline = (Get-Date).AddSeconds($JobTimeoutSeconds)
$queuedDeadline = (Get-Date).AddSeconds($QueuedTimeoutSeconds)
$sawSampling = $false
do {
    $statusPayload = $common.Clone()
    $statusPayload.script = "from guideants_video_client import get_talking_head_job`nimport json`nprint(json.dumps(get_talking_head_job('$jobId'), separators=(',', ':')))"
    $status = Invoke-SandboxExecute "job status" $statusPayload (Join-Path $ArtifactDir "status-execute-request.json")
    $state = (Get-RequiredProperty $status "state" "job status").ToLowerInvariant()
    $progress = Get-ResponseProperty $status 'progress'
    if ($null -ne $progress) {
        $phase = (Get-ResponseProperty $progress 'phase')
        if ($phase -in @('executing', 'sampling', 'completed')) {
            $sawSampling = $true
        }
        $message = Get-ResponseProperty $progress 'message'
        if ([string]::IsNullOrWhiteSpace([string]$message)) { $message = $state }
        $details = @([string]$message)
        $nodeClass = Get-ResponseProperty $progress 'node_class'
        if (-not [string]::IsNullOrWhiteSpace([string]$nodeClass)) { $details += "node=$nodeClass" }
        $step = Get-ResponseProperty $progress 'step'
        $maxSteps = Get-ResponseProperty $progress 'max_steps'
        if ($null -ne $step -and $null -ne $maxSteps) {
            $details += "step=$step/$maxSteps"
        }
        Write-Host ("[job {0}] {1}" -f $jobId, ($details -join " | "))
    }
    if ($state -eq "completed") { break }
    if ($state -in @("failed", "cancelled")) { throw "Video job ended in state '$state'." }
    if (-not $sawSampling -and (Get-Date) -ge $queuedDeadline) {
        throw "Video job $jobId stayed queued for ${QueuedTimeoutSeconds}s without sampling progress."
    }
    if ((Get-Date) -ge $deadline) { throw "Timed out waiting for video job $jobId." }
    Start-Sleep -Seconds $PollSeconds
} while ($true)

$materializePayload = $common.Clone()
$materializePayload.script = "from guideants_video_client import materialize_talking_head_result`nimport json`nprint(json.dumps(materialize_talking_head_result('$jobId', '$OutputName'), separators=(',', ':')))"
Invoke-SandboxExecute "materialize" $materializePayload (Join-Path $ArtifactDir "materialize-execute-request.json") | Out-Null

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
Test-RequiredFile $HostOutput "Materialized MKV is missing from the host ContentFiles share."
$header = [IO.File]::ReadAllBytes($HostOutput)
if ($header.Length -lt 4 -or -not ($header[0] -eq 0x1a -and $header[1] -eq 0x45 -and $header[2] -eq 0xdf -and $header[3] -eq 0xa3)) {
    throw "Host output is not a Matroska file."
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
Write-Host "Preserved MKV: $PreservedOutput"
