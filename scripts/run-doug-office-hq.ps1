$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$VideoHost = "http://127.0.0.1:8189"
$ScriptAgentToken = "local-script-agent-test-token"
$OutputName = "doug-office-hq-v2-416x240-30s.mp4"
$JobTimeoutSeconds = 7200
$ArtifactDir = Join-Path $RepoRoot "artifacts/infinitetalk"
$LogPath = Join-Path $ArtifactDir "doug-office-hq-run.log"
New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null

function Invoke-CurlJson($Label, $Arguments) {
    $output = & curl.exe --fail --silent --show-error @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "curl failed during '$Label': $output" }
    return ($output | Out-String).Trim() | ConvertFrom-Json
}

function Invoke-SandboxExecute($Label, $Payload, $PayloadPath) {
    $Payload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $PayloadPath -Encoding utf8NoBOM
    $response = Invoke-CurlJson $Label @(
        "-H", "X-Script-Agent-Token: $ScriptAgentToken",
        "-H", "Content-Type: application/json",
        "--data-binary", "@$PayloadPath",
        "$VideoHost/sandbox/execute"
    )
    if ([int]$response.exitCode -ne 0) { throw "'$Label' failed: $($response.standardError)" }
    return ([string]$response.standardOutput).Trim() | ConvertFrom-Json
}

$common = @{
    scriptType = "Python"
    workingDirectory = "/app/ContentFiles/acceptance-project/authorized-notebook/Output"
    projectId = "11111111-1111-1111-1111-111111111111"
    notebookId = "22222222-2222-2222-2222-222222222222"
    guideId = "33333333-3333-3333-3333-333333333333"
    timeoutSeconds = 600
}

"Starting doug-office HQ job at $(Get-Date -Format o)" | Tee-Object -FilePath $LogPath
$submitPayload = Get-Content (Join-Path $RepoRoot "tests/requests/infinitetalk/execute-doug-office-hq.json") -Raw | ConvertFrom-Json -AsHashtable
$submit = Invoke-SandboxExecute "submit" $submitPayload (Join-Path $ArtifactDir "doug-office-submit.json")
$jobId = [string]$submit.jobId
"jobId=$jobId" | Tee-Object -FilePath $LogPath -Append

$deadline = (Get-Date).AddSeconds($JobTimeoutSeconds)
do {
    $statusPayload = $common.Clone()
    $statusPayload.script = "from guideants_video_client import get_talking_head_job`nimport json`nprint(json.dumps(get_talking_head_job('$jobId'), separators=(',', ':')))"
    $status = Invoke-SandboxExecute "status" $statusPayload (Join-Path $ArtifactDir "doug-office-status.json")
    $state = [string]$status.state
    if ($status.progress) {
        $p = $status.progress
        $line = "[job $jobId] $($p.message) | node=$($p.node_class) | step=$($p.step)/$($p.max_steps)"
        Write-Host $line
        $line | Add-Content -Path $LogPath
    }
    if ($state -eq "completed") { break }
    if ($state -in @("failed", "cancelled")) { throw "Job ended in state $state" }
    if ((Get-Date) -ge $deadline) { throw "Timed out" }
    Start-Sleep -Seconds 10
} while ($true)

$matPayload = $common.Clone()
$matPayload.script = "from guideants_video_client import materialize_talking_head_result`nimport json`nprint(json.dumps(materialize_talking_head_result('$jobId', '$OutputName'), separators=(',', ':')))"
Invoke-SandboxExecute "materialize" $matPayload (Join-Path $ArtifactDir "doug-office-materialize.json") | Out-Null

$hostOut = Join-Path $RepoRoot "tests/runtime/content-files/acceptance-project/authorized-notebook/Output/$OutputName"
$preserved = Join-Path $ArtifactDir $OutputName
Copy-Item -LiteralPath $hostOut -Destination $preserved -Force
"Completed. Output: $preserved" | Tee-Object -FilePath $LogPath -Append
