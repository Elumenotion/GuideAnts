[CmdletBinding()]
param(
    [string]$DriverVideoPath = "artifacts/infinitetalk-v2v/inputs/august16-office-full-11-green-416x256.mkv",
    [string]$AudioPath = "artifacts/infinitetalk-v2v/inputs/august_16_cover.wav",
    [string]$BackgroundPath = "tests/runtime/content-files/acceptance-project/authorized-notebook/Input/office-whiteboard-bf16-inpaint-28.png",
    [string]$OutputStem = "v2v-august16-office-full-11",
    [int]$Width = 416,
    [int]$Height = 256,
    [int]$Steps = 14,
    [double]$Cfg = 1.0,
    [int]$Fps = 25,
    [string]$PositivePrompt = "A professional presenter speaks naturally to camera, relaxed head movement, subtle head turns, expressive eyes, small posture shifts, warm restrained smile",
    [string]$NegativePrompt = "blur, distortion, extra limbs, deformed face, subtitles, low quality, dramatic gestures, overacting, wild motion",
    [string]$VideoHost = "http://127.0.0.1:8189",
    [string]$ScriptAgentToken = "local-script-agent-test-token",
    [int]$JobTimeoutSeconds = 3600,
    [int]$PollSeconds = 10,
    [int]$OutputWidth = 1280,
    [int]$OutputHeight = 720,
    [string]$CorridorKeyRoot = "artifacts/tools/CorridorKey",
    [string]$CorridorKeyDevice = "cuda:0",
    [double]$BackgroundBlurSigma = 1.5,
    [double]$ForegroundSharpenAmount = 0.15,
    [double]$ForegroundSharpenSigma = 0.8,
    [double]$AudioLeadInSeconds = 0.5,
    [string]$SourceGreenPath = "",
    [switch]$SkipGenerate
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Resolve-RepoPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Invoke-Ffmpeg {
    param([Parameter(Mandatory = $true)][string[]]$Arguments, [string]$Label)
    & ffmpeg.exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed during '$Label' (exit $LASTEXITCODE)" }
}

function Get-VideoSize([string]$Path) {
    $raw = & ffprobe.exe -v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0:s=x $Path
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($raw)) {
        throw "ffprobe failed for $Path"
    }
    $parts = $raw.Trim().Split("x")
    return @{ Width = [int]$parts[0]; Height = [int]$parts[1] }
}

function Invoke-CurlJson($Label, $Arguments) {
    $attempts = 0
    while ($true) {
        $attempts++
        try {
            $output = & curl.exe --fail --silent --show-error @Arguments 2>&1
            if ($LASTEXITCODE -ne 0) { throw "curl failed during '$Label': $output" }
            return ($output | Out-String).Trim() | ConvertFrom-Json
        } catch {
            if ($attempts -ge 12) { throw }
            Start-Sleep -Seconds 5
        }
    }
}

function Write-Utf8NoBomFile([string]$Path, [string]$Content) {
    $encoding = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Get-AvailableOutputStem {
    param(
        [Parameter(Mandatory = $true)][string]$BaseStem,
        [Parameter(Mandatory = $true)][string]$ArtifactDir
    )
    $candidate = $BaseStem
    $suffix = 2
    while (Test-Path -LiteralPath (Join-Path $ArtifactDir "$candidate-overlay-720p.mp4") -PathType Leaf) {
        $candidate = "$BaseStem-$suffix"
        $suffix++
    }
    return $candidate
}

function New-PaddedAudio {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][double]$LeadInSeconds
    )
    if ($LeadInSeconds -le 0) {
        Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath -Force
        return
    }
    $delayMs = [int][Math]::Round($LeadInSeconds * 1000)
    Invoke-Ffmpeg -Label "pad audio lead-in" -Arguments @(
        "-y", "-hide_banner", "-loglevel", "error",
        "-i", $SourcePath,
        "-af", "adelay=${delayMs}:all=1",
        "-c:a", "pcm_s16le",
        $DestinationPath
    )
}

function Invoke-SandboxExecute($Label, $Payload, $PayloadPath) {
    Write-Utf8NoBomFile $PayloadPath ($Payload | ConvertTo-Json -Depth 8)
    $response = Invoke-CurlJson $Label @(
        "-H", "X-Script-Agent-Token: $ScriptAgentToken",
        "-H", "Content-Type: application/json",
        "--data-binary", "@$PayloadPath",
        "$VideoHost/sandbox/execute"
    )
    if ([int]$response.exitCode -ne 0) { throw "'$Label' failed: $($response.standardError)" }
    $stdout = ([string]$response.standardOutput).Trim()
    if ([string]::IsNullOrWhiteSpace($stdout)) { throw "'$Label' returned empty stdout" }
    return $stdout | ConvertFrom-Json
}

$DriverVideo = Resolve-RepoPath $DriverVideoPath
$Audio = Resolve-RepoPath $AudioPath
$Background = Resolve-RepoPath $BackgroundPath
$CorridorKey = Resolve-RepoPath $CorridorKeyRoot
foreach ($required in @($DriverVideo, $Audio, $Background)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing: $required" }
}
if (-not (Test-Path -LiteralPath $CorridorKey -PathType Container)) {
    throw "Missing CorridorKey checkout: $CorridorKey. Run scripts/install-corridorkey.ps1."
}

$NotebookRoot = Join-Path $RepoRoot "tests/runtime/content-files/acceptance-project/authorized-notebook"
$InputDir = Join-Path $NotebookRoot "Input"
$OutputDir = Join-Path $NotebookRoot "Output"
$ArtifactDir = Join-Path $RepoRoot "artifacts/infinitetalk-v2v/runs"
New-Item -ItemType Directory -Force -Path $ArtifactDir, $InputDir, $OutputDir | Out-Null

$RequestedOutputStem = $OutputStem
$OutputStem = Get-AvailableOutputStem -BaseStem $OutputStem -ArtifactDir $ArtifactDir

$DriverName = "$OutputStem-driver-${Width}x${Height}.mkv"
$GenName = "$OutputStem-green-${Width}x${Height}.mkv"
$FinalName = "$OutputStem-overlay-720p.mp4"
$MasterName = "$OutputStem-master-720p.mkv"
$DriverHost = Join-Path $InputDir $DriverName
$GenHost = Join-Path $OutputDir $GenName
$FinalHost = Join-Path $ArtifactDir $FinalName
$MasterHost = Join-Path $ArtifactDir $MasterName
$LogPath = Join-Path $ArtifactDir "$OutputStem-pipeline.log"
if ($SkipGenerate -and -not [string]::IsNullOrWhiteSpace($SourceGreenPath)) {
    $GenHost = Resolve-RepoPath $SourceGreenPath
}

"Starting V2V talking-head pipeline at $(Get-Date -Format o)" | Tee-Object -FilePath $LogPath
"workflow=infinitetalk-v2v-v1" | Tee-Object -FilePath $LogPath -Append
if ($OutputStem -ne $RequestedOutputStem) {
    "requested_output_stem=$RequestedOutputStem" | Tee-Object -FilePath $LogPath -Append
}
"output_stem=$OutputStem" | Tee-Object -FilePath $LogPath -Append
"driver_video=$DriverVideo" | Tee-Object -FilePath $LogPath -Append
"audio_lead_in_seconds=$AudioLeadInSeconds" | Tee-Object -FilePath $LogPath -Append

$driverSize = Get-VideoSize $DriverVideo
"1. stage driver $($driverSize.Width)x$($driverSize.Height) -> ${Width}x${Height}" | Tee-Object -FilePath $LogPath -Append
if ($driverSize.Width -eq $Width -and $driverSize.Height -eq $Height) {
    Copy-Item -LiteralPath $DriverVideo -Destination $DriverHost -Force
} else {
    Invoke-Ffmpeg -Label "prep driver video" -Arguments @(
        "-y", "-hide_banner", "-loglevel", "error",
        "-i", $DriverVideo,
        "-vf", "scale=${Width}:${Height}:force_original_aspect_ratio=decrease:flags=lanczos,pad=${Width}:${Height}:(ow-iw)/2:(oh-ih)/2:color=0x00ff00,setsar=1",
        "-c:v", "ffv1",
        "-pix_fmt", "rgba64le",
        $DriverHost
    )
}
Copy-Item -LiteralPath $DriverHost -Destination (Join-Path $ArtifactDir $DriverName) -Force
$AudioName = "$OutputStem-input-padded.wav"
$AudioInNotebook = Join-Path $InputDir $AudioName
$AudioArtifact = Join-Path $ArtifactDir $AudioName
New-PaddedAudio -SourcePath $Audio -DestinationPath $AudioInNotebook -LeadInSeconds $AudioLeadInSeconds
Copy-Item -LiteralPath $AudioInNotebook -Destination $AudioArtifact -Force
$preparedSize = Get-VideoSize $DriverHost
if ($preparedSize.Width -ne $Width -or $preparedSize.Height -ne $Height) {
    throw "Prepared driver is $($preparedSize.Width)x$($preparedSize.Height), expected ${Width}x${Height}"
}

$common = @{
    scriptType = "Python"
    workingDirectory = "/app/ContentFiles/acceptance-project/authorized-notebook/Output"
    projectId = "11111111-1111-1111-1111-111111111111"
    notebookId = "22222222-2222-2222-2222-222222222222"
    guideId = "33333333-3333-3333-3333-333333333333"
    timeoutSeconds = 600
}
$jobId = ""
if (-not $SkipGenerate) {
$promptLiteral = $PositivePrompt | ConvertTo-Json -Compress
$negativeLiteral = $NegativePrompt | ConvertTo-Json -Compress
$submitPayload = $common.Clone()
$submitPayload.script = @"
from guideants_video_client import submit_talking_head_v2v
import json
result = submit_talking_head_v2v(
    video_path='../Input/$DriverName',
    audio_path='../Input/$AudioName',
    workflow='infinitetalk-v2v-v1',
    output_filename='$GenName',
    parameters={'width': $Width, 'height': $Height, 'steps': $Steps, 'cfg': $Cfg, 'fps': $Fps},
    positive_prompt=$promptLiteral,
    negative_prompt=$negativeLiteral,
)
print(json.dumps(result, separators=(',', ':')))
"@
"2. generate V2V ${Width}x${Height} from $(Split-Path -Leaf $AudioInNotebook)" | Tee-Object -FilePath $LogPath -Append
$submit = Invoke-SandboxExecute "submit" $submitPayload (Join-Path $ArtifactDir "$OutputStem-submit.json")
$jobId = [string]$submit.jobId
"jobId=$jobId" | Tee-Object -FilePath $LogPath -Append
$deadline = (Get-Date).AddSeconds($JobTimeoutSeconds)
do {
    $statusPayload = $common.Clone()
    $statusPayload.script = "from guideants_video_client import get_talking_head_job`nimport json`nprint(json.dumps(get_talking_head_job('$jobId'), separators=(',', ':')))"
    $status = Invoke-SandboxExecute "status" $statusPayload (Join-Path $ArtifactDir "$OutputStem-status.json")
    $state = [string]$status.state
    if ($status.progress) {
        $p = $status.progress
        $line = "[job $jobId] $($p.message) | node=$($p.node_class) | step=$($p.step)/$($p.max_steps)"
        Write-Host $line
        $line | Add-Content -Path $LogPath
    }
    if ($state -eq "completed") { break }
    if ($state -in @("failed", "cancelled")) {
        throw "Job ended in state $state : $($status.error)"
    }
    if ((Get-Date) -ge $deadline) { throw "Timed out waiting for job $jobId" }
    Start-Sleep -Seconds $PollSeconds
} while ($true)

$matPayload = $common.Clone()
$matPayload.script = "from guideants_video_client import materialize_talking_head_result`nimport json`nprint(json.dumps(materialize_talking_head_result('$jobId', '$GenName'), separators=(',', ':')))"
Invoke-SandboxExecute "materialize" $matPayload (Join-Path $ArtifactDir "$OutputStem-materialize.json") | Out-Null
if (-not (Test-Path -LiteralPath $GenHost -PathType Leaf)) { throw "Missing generated file $GenHost" }
Copy-Item -LiteralPath $GenHost -Destination (Join-Path $ArtifactDir $GenName) -Force
} else {
    if (-not (Test-Path -LiteralPath $GenHost -PathType Leaf)) {
        $cachedGenerate = Join-Path $ArtifactDir $GenName
        if (-not (Test-Path -LiteralPath $cachedGenerate -PathType Leaf)) {
            throw "SkipGenerate requires $GenHost or $cachedGenerate"
        }
        Copy-Item -LiteralPath $cachedGenerate -Destination $GenHost -Force
    }
    Copy-Item -LiteralPath $GenHost -Destination (Join-Path $ArtifactDir $GenName) -Force
    "2. skipped V2V generation (SkipGenerate)" | Tee-Object -FilePath $LogPath -Append
}

"3+4. CorridorKey foreground unmixing and composite to ${OutputWidth}x${OutputHeight}" | Tee-Object -FilePath $LogPath -Append
$corridorScript = Join-Path $PSScriptRoot "run-corridorkey-composite.py"
& python.exe $corridorScript `
    --source $GenHost `
    --plate $Background `
    --output $FinalHost `
    --master-output $MasterHost `
    --corridorkey-root $CorridorKey `
    --device $CorridorKeyDevice `
    --width $OutputWidth `
    --height $OutputHeight `
    --background-blur-sigma $BackgroundBlurSigma `
    --foreground-sharpen-amount $ForegroundSharpenAmount `
    --foreground-sharpen-sigma $ForegroundSharpenSigma 2>&1 |
    Tee-Object -FilePath $LogPath -Append
if ($LASTEXITCODE -ne 0) {
    throw "run-corridorkey-composite.py failed (exit $LASTEXITCODE)"
}
if (-not (Test-Path -LiteralPath $MasterHost -PathType Leaf)) {
    throw "Missing lossless master $MasterHost"
}

$audioDur = [double](& ffprobe.exe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 $AudioInNotebook)
$finalDur = [double](& ffprobe.exe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 $FinalHost)
$masterDur = [double](& ffprobe.exe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 $MasterHost)
$finalSize = Get-VideoSize $FinalHost
$masterSize = Get-VideoSize $MasterHost
"audio_duration_seconds=$audioDur" | Tee-Object -FilePath $LogPath -Append
"final_duration_seconds=$finalDur" | Tee-Object -FilePath $LogPath -Append
"final_size=$($finalSize.Width)x$($finalSize.Height)" | Tee-Object -FilePath $LogPath -Append
"master_duration_seconds=$masterDur" | Tee-Object -FilePath $LogPath -Append
"master_size=$($masterSize.Width)x$($masterSize.Height)" | Tee-Object -FilePath $LogPath -Append
"Completed. delivery=$FinalHost master=$MasterHost" | Tee-Object -FilePath $LogPath -Append
