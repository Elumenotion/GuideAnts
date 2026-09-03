#Requires -Version 5.1
<#
.SYNOPSIS
  Control Max over the GuideAntsAgent named pipe (no SSH / no focus stealing).

.EXAMPLE
  .\Invoke-MaxAgentPipe.ps1 -Ping
  .\Invoke-MaxAgentPipe.ps1 -Exec 'docker ps --format "{{.Names}} {{.Status}}"'
  .\Invoke-MaxAgentPipe.ps1 -Exec 'Set-Location C:\repos\GuideAnts; .\start_windows.cmd --backend rocm --compose local'
#>
param(
    [Parameter(ParameterSetName = 'Ping')]
    [switch]$Ping,

    [Parameter(ParameterSetName = 'Exec', Mandatory = $true)]
    [string]$Exec,

    [string]$ComputerName = 'MAX',
    [string]$PipeName = 'GuideAntsAgent',
    [string]$TokenPath = '',
    [int]$TimeoutMs = 600000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $TokenPath) {
    $candidates = @(
        (Join-Path $here 'token'),
        (Join-Path $env:USERPROFILE 'guideants-agent-pipe\token'),
        'D:\repos\GuideAnts\scripts\guideants-agent-pipe\token'
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { $TokenPath = $c; break }
    }
    if (-not $TokenPath) { $TokenPath = $candidates[0] }
}

if (-not (Test-Path -LiteralPath $TokenPath)) {
    throw @"
Token file not found: $TokenPath

Copy without pasting into chat:
  Copy-Item 'D:\repos\GuideAnts\scripts\guideants-agent-pipe\token' '$TokenPath'

If the listener is down, run /sshmax first.
"@
}

$token = [System.IO.File]::ReadAllText($TokenPath).Trim()
if (-not $token) { throw "Token file empty: $TokenPath" }

$pipePath = "\\$ComputerName\pipe\$PipeName"
$op = if ($Ping) { 'ping' } else { 'exec' }
$reqObj = [ordered]@{
    id     = [guid]::NewGuid().ToString('N')
    token  = $token
    op     = $op
    script = $(if ($Ping) { $null } else { $Exec })
}
$payload = ($reqObj | ConvertTo-Json -Compress -Depth 6)

$client = New-Object System.IO.Pipes.NamedPipeClientStream(
    $ComputerName,
    $PipeName,
    [System.IO.Pipes.PipeDirection]::InOut,
    [System.IO.Pipes.PipeOptions]::None,
    [System.Security.Principal.TokenImpersonationLevel]::Impersonation
)
try {
    $client.Connect($TimeoutMs)
    $writer = New-Object System.IO.StreamWriter($client, [System.Text.Encoding]::UTF8, 65536, $true)
    $reader = New-Object System.IO.StreamReader($client, [System.Text.Encoding]::UTF8, $false, 65536, $true)
    $writer.AutoFlush = $true
    $writer.WriteLine($payload)
    $responseLine = $reader.ReadLine()
    if (-not $responseLine) { throw "No response from $pipePath" }
    $response = $responseLine | ConvertFrom-Json
    $response | ConvertTo-Json -Depth 6
    if (-not $response.ok) {
        exit 1
    }
}
finally {
    if ($client) { $client.Dispose() }
}
