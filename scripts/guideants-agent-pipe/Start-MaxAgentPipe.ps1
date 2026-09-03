#Requires -Version 5.1
<#
.SYNOPSIS
  Named-pipe listener on Max: run PowerShell in the authenticated Max session context.

.NOTES
  Pipe: \\.\pipe\GuideAntsAgent  (clients: \\MAX\pipe\GuideAntsAgent)
  Auth: token file next to this script (never print).
  Start from the existing Max SSH session so R:/C:/docker/user context match that session.
#>
param(
    [string]$PipeName = 'GuideAntsAgent',
    [switch]$Detach
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$tokenPath = Join-Path $here 'token'
$logPath = Join-Path $here 'listener.log'

function Write-Log([string]$Message) {
    $line = '{0:o} {1}' -f (Get-Date).ToUniversalTime(), $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}

function Get-OrCreateToken {
    if (-not (Test-Path -LiteralPath $tokenPath)) {
        $bytes = New-Object byte[] 32
        [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $token = [Convert]::ToBase64String($bytes)
        [System.IO.File]::WriteAllText($tokenPath, $token)
        try {
            & icacls $tokenPath /inheritance:r 2>$null | Out-Null
            & icacls $tokenPath /grant:r "${env:USERNAME}:(R)" 2>$null | Out-Null
        } catch {
            Write-Log "token acl skipped: $($_.Exception.Message)"
        }
        Write-Log 'token file created'
    }
    return [System.IO.File]::ReadAllText($tokenPath).Trim()
}

function New-PipeSecurity {
    $sec = New-Object System.IO.Pipes.PipeSecurity
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $rule = New-Object System.IO.Pipes.PipeAccessRule(
        $id.User,
        [System.IO.Pipes.PipeAccessRights]::FullControl,
        [System.Security.AccessControl.AccessControlType]::Allow
    )
    $sec.AddAccessRule($rule)
    $authUsers = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-11')
    $rule2 = New-Object System.IO.Pipes.PipeAccessRule(
        $authUsers,
        [System.IO.Pipes.PipeAccessRights]::ReadWrite,
        [System.Security.AccessControl.AccessControlType]::Allow
    )
    $sec.AddAccessRule($rule2)
    return $sec
}

function Invoke-MaxPowerShell([string]$Script) {
    if ([string]::IsNullOrWhiteSpace($Script)) {
        throw 'script required'
    }
    $tmp = Join-Path $env:TEMP ("maxpipe-{0}.ps1" -f [guid]::NewGuid().ToString('N'))
    try {
        [System.IO.File]::WriteAllText($tmp, $Script, [System.Text.UTF8Encoding]::new($false))
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $tmp 2>&1
        $code = $LASTEXITCODE
        if ($null -eq $code) { $code = 0 }
        $text = ($out | ForEach-Object { "$_" }) -join "`n"
        return @{
            ok       = ($code -eq 0)
            exitCode = [int]$code
            stdout   = $text
            stderr   = ''
        }
    }
    finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
}

if ($Detach) {
    $ps = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $scriptPath = $MyInvocation.MyCommand.Path
    $arg = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" -PipeName `"$PipeName`""
    $outLog = Join-Path $here 'detach-stdout.log'
    $errLog = Join-Path $here 'detach-stderr.log'
    # Stop prior listener instances for this script (best-effort).
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -like '*Start-MaxAgentPipe.ps1*' -and $_.CommandLine -notlike '*-Detach*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    $p = Start-Process -FilePath $ps -ArgumentList $arg -WorkingDirectory $here -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    Set-Content -LiteralPath (Join-Path $here 'detach.pid') -Value $p.Id -Encoding ASCII
    Write-Output "detached listener pid=$($p.Id) pipe=$PipeName"
    exit 0
}

$expectedToken = Get-OrCreateToken
$pipeSecurity = New-PipeSecurity
Write-Log "listener starting pipe=$PipeName user=$env:USERNAME"
$inBuffer = 65536
$outBuffer = 65536

while ($true) {
    $server = New-Object -TypeName System.IO.Pipes.NamedPipeServerStream -ArgumentList @(
        $PipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        1,
        [System.IO.Pipes.PipeTransmissionMode]::Message,
        [System.IO.Pipes.PipeOptions]::None,
        $inBuffer,
        $outBuffer,
        $pipeSecurity
    )
    try {
        if (-not (Test-Path -LiteralPath (Join-Path $here 'ready.flag'))) {
            Set-Content -LiteralPath (Join-Path $here 'ready.flag') -Value ((Get-Date).ToUniversalTime().ToString('o')) -Encoding ASCII
        }
        Write-Log 'waiting for connection'
        $server.WaitForConnection()
        $reader = New-Object System.IO.StreamReader($server, [System.Text.Encoding]::UTF8, $false, 65536, $true)
        $writer = New-Object System.IO.StreamWriter($server, [System.Text.Encoding]::UTF8, 65536, $true)
        $writer.AutoFlush = $true
        $line = $reader.ReadLine()
        $response = @{ id = $null; ok = $false; exitCode = 1; stdout = ''; stderr = 'bad request' }
        try {
            $req = $line | ConvertFrom-Json
            $response.id = $req.id
            if ([string]$req.token -ne $expectedToken) {
                throw 'unauthorized'
            }
            $op = [string]$req.op
            if ([string]::IsNullOrWhiteSpace($op) -and $req.cmd) { $op = [string]$req.cmd }
            Write-Log "exec op=$op"
            switch ($op) {
                'ping' {
                    $response.ok = $true
                    $response.exitCode = 0
                    $response.stdout = 'pong'
                    $response.stderr = ''
                }
                'exec' {
                    $script = $null
                    if ($req.script) { $script = [string]$req.script }
                    elseif ($req.args -and $req.args.script) { $script = [string]$req.args.script }
                    $result = Invoke-MaxPowerShell -Script $script
                    $response.ok = [bool]$result.ok
                    $response.exitCode = [int]$result.exitCode
                    $response.stdout = [string]$result.stdout
                    $response.stderr = [string]$result.stderr
                }
                default {
                    throw "unknown op: $op (use ping or exec)"
                }
            }
        }
        catch {
            $response.ok = $false
            $response.exitCode = 1
            $response.stderr = $_.Exception.Message
            Write-Log "error: $($_.Exception.Message)"
        }
        $writer.WriteLine(($response | ConvertTo-Json -Compress -Depth 6))
    }
    finally {
        if ($server) { $server.Dispose() }
    }
}
