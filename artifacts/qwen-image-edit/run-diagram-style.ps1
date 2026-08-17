# Style a diagram via Qwen Image Edit SEA path
param(
    [string]$VideoHost = "http://127.0.0.1:8189",
    [string]$ScriptAgentToken = "local-script-agent-test-token",
    [string]$ArtifactsRoot = "artifacts/qwen-image-edit",
    [string]$ContentFilesRoot = "artifacts",
    [string]$SourceRelative = "diagram-style-src.png",
    [string]$OutputName = "diagram-styled.png",
    [string]$Prompt = "Restyle this technical sequence diagram into a polished modern product illustration. Keep the same participants, arrows, labels, and notes exactly readable. Add soft color: cool slate background, blue accents for Depth 1 Inv A, teal for Depth 2 Inv B, violet for Depth 3 Inv C, warm amber highlights on notes and the Human actor. Clean flat vector look with subtle shadows, high contrast typography, professional SaaS docs aesthetic. Do not invent new boxes or change the message flow.",
    [int]$JobTimeoutSeconds = 3600,
    [int]$PollSeconds = 5
)
$ErrorActionPreference = "Stop"
$RepoRoot = "C:\repos\GuideAnts"
function Resolve-RepoPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}
function Write-Utf8NoBomFile([string]$Path, [string]$Content) {
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
    $text = & curl.exe --fail --silent --show-error -H "X-Script-Agent-Token: $ScriptAgentToken" -H "Content-Type: application/json" --data-binary "@$PayloadPath" "$VideoHost/sandbox/execute"
    if ($LASTEXITCODE -ne 0) { throw "curl failed during $Label" }
    $response = $text | ConvertFrom-Json
    $exitCode = Get-ResponseProperty $response 'ExitCode'
    if ($null -eq $exitCode -or [int]$exitCode -ne 0) {
        throw "'$Label' failed: $(Get-ResponseProperty $response 'StandardError')"
    }
    return ([string](Get-ResponseProperty $response 'StandardOutput')).Trim() | ConvertFrom-Json
}

$ArtifactDir = Resolve-RepoPath $ArtifactsRoot
$OutputDir = Join-Path (Resolve-RepoPath $ContentFilesRoot) "acceptance-project\authorized-notebook\Output"
Remove-Item -LiteralPath (Join-Path $OutputDir $OutputName) -Force -ErrorAction SilentlyContinue

$common = @{
    scriptType = "Python"
    workingDirectory = "/app/ContentFiles/acceptance-project/authorized-notebook/Output"
    projectId = "11111111-1111-1111-1111-111111111111"
    notebookId = "22222222-2222-2222-2222-222222222222"
    guideId = "33333333-3333-3333-3333-333333333333"
    timeoutSeconds = 1800
}
$escapedPrompt = $Prompt.Replace("'", "\'")
$submitPayload = $common.Clone()
$submitPayload.script = @"
from guideants_video_client import submit_image_edit
import json
print(json.dumps(submit_image_edit(
    '../Input/$SourceRelative',
    '$escapedPrompt',
    '$OutputName',
    parameters={'steps': 4, 'cfg': 1.0, 'seed': 42},
), separators=(',', ':')))
"@
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$submit = Invoke-SandboxExecute "style submit" $submitPayload (Join-Path $ArtifactDir "diagram-style-submit.json")
$jobId = Get-ResponseProperty $submit 'jobId'
Write-Host "style job $jobId"
$deadline = (Get-Date).AddSeconds($JobTimeoutSeconds)
do {
    $statusPayload = $common.Clone()
    $statusPayload.script = "from guideants_video_client import get_image_job`nimport json`nprint(json.dumps(get_image_job('$jobId'), separators=(',', ':')))"
    $status = Invoke-SandboxExecute "style status" $statusPayload (Join-Path $ArtifactDir "diagram-style-status.json")
    $state = (Get-ResponseProperty $status "state").ToLowerInvariant()
    $progress = Get-ResponseProperty $status 'progress'
    $message = if ($null -ne $progress) { Get-ResponseProperty $progress 'message' } else { $state }
    Write-Host "[style] $message"
    if ($state -eq "completed") { break }
    if ($state -in @("failed", "cancelled")) { throw "style ended in state '$state': $(Get-ResponseProperty $status 'error')" }
    if ((Get-Date) -ge $deadline) { throw "Timed out waiting for style job $jobId" }
    Start-Sleep -Seconds $PollSeconds
} while ($true)
$materializePayload = $common.Clone()
$materializePayload.script = "from guideants_video_client import materialize_image_result`nimport json`nprint(json.dumps(materialize_image_result('$jobId', '$OutputName'), separators=(',', ':')))"
Invoke-SandboxExecute "style materialize" $materializePayload (Join-Path $ArtifactDir "diagram-style-materialize.json") | Out-Null
$sw.Stop()
$elapsed = [math]::Round($sw.Elapsed.TotalSeconds, 1)
Copy-Item -LiteralPath (Join-Path $OutputDir $OutputName) -Destination (Join-Path $ArtifactDir $OutputName) -Force
Write-Host "STYLE_OK elapsed=$elapsed path=$(Join-Path $ArtifactDir $OutputName)"
