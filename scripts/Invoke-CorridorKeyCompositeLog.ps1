<#
Composite telemetry contract — read before editing any caller or ad-hoc queue script.

  - Progress lines come from run-corridorkey-composite.py stdout.
  - NEVER use 2>&1 before piping — merges MIOpen/HIP stderr into telemetry.
  - docker exec alone does NOT write container logs; use exec-composite-telemetry.sh
    (tees stdout to /proc/1/fd/1) so docker logs show CorridorKey/VSR/composite lines.
  - Host copy: artifacts/*-pipeline.log (UTF-8, line-buffered).
  - Machine poll: {output-stem}-progress.json in content-files Output/.
  - Canonical spec: run-corridorkey-composite.py header, composite.py, test_composite.py.
#>

function Write-CompositeLogLine {
    param(
        [Parameter(Mandatory)][string]$LogPath,
        [Parameter(Mandatory)][string]$Line
    )
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::AppendAllText($LogPath, $Line + [Environment]::NewLine, $utf8)
}

function Start-DockerStream {
    param([Parameter(Mandatory)][string[]]$ArgumentList)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "docker"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
  if ($PSVersionTable.PSVersion.Major -ge 7) {
        foreach ($arg in $ArgumentList) { [void]$psi.ArgumentList.Add($arg) }
    } else {
        $psi.Arguments = (
            $ArgumentList | ForEach-Object {
                if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
            }
        ) -join ' '
    }
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    [void]$process.Start()
    return $process
}

function Get-DockerExecContainerIndex {
    param([Parameter(Mandatory)][string[]]$DockerArgs)

    $i = 0
    while ($i -lt $DockerArgs.Length) {
        $arg = $DockerArgs[$i]
        if ($arg -eq "exec") {
            $i++
            continue
        }
        if ($arg -in @("-e", "--env", "-u", "--user", "-w", "--workdir")) {
            $i += 2
            continue
        }
        if ($arg.StartsWith("-")) {
            $i++
            continue
        }
        return $i
    }
    return -1
}

function Invoke-CorridorKeyCompositeLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, ParameterSetName = "Docker")]
        [string[]]$DockerArgs,

        [Parameter(Mandatory = $true, ParameterSetName = "Local")]
        [string]$FilePath,

        [Parameter(ParameterSetName = "Local")]
        [string[]]$ArgumentList,

        [string]$LogPath,

        [scriptblock]$OnLine
    )

    if ($LogPath -and -not (Test-Path -LiteralPath $LogPath)) {
        $utf8 = New-Object System.Text.UTF8Encoding $false
        [System.IO.File]::WriteAllText($LogPath, "", $utf8)
    }

    if ($PSCmdlet.ParameterSetName -eq "Docker") {
        $containerIdx = Get-DockerExecContainerIndex -DockerArgs $DockerArgs
        if ($containerIdx -lt 0) {
            throw "Invoke-CorridorKeyCompositeLog: could not find container name in DockerArgs"
        }
        $container = $DockerArgs[$containerIdx]
        $prefix = @($DockerArgs[0..($containerIdx - 1)])
        $suffix = @($DockerArgs[($containerIdx + 1)..($DockerArgs.Length - 1)])
        $wrapped = $prefix + @(
            $container,
            "/bin/bash", "/opt/guideants/comfyui-video/scripts/exec-composite-telemetry.sh"
        ) + $suffix

        $process = Start-DockerStream -ArgumentList $wrapped
        while (-not $process.StandardOutput.EndOfStream) {
            $text = $process.StandardOutput.ReadLine()
            if ([string]::IsNullOrWhiteSpace($text)) { continue }
            Write-Host $text
            if ($OnLine) { & $OnLine $text }
            if ($LogPath) { Write-CompositeLogLine -LogPath $LogPath -Line $text }
        }
        while (-not $process.StandardError.EndOfStream) {
            $errLine = $process.StandardError.ReadLine()
            if ([string]::IsNullOrWhiteSpace($errLine)) { continue }
            $tagged = "stderr: $errLine"
            Write-Host $tagged
            if ($OnLine) { & $OnLine $tagged }
            if ($LogPath) { Write-CompositeLogLine -LogPath $LogPath -Line $tagged }
        }
        $process.WaitForExit()
        return $process.ExitCode
    }

    $exitCode = 0
    & $FilePath @ArgumentList | ForEach-Object {
        $text = ([string]$_).TrimEnd("`r", "`n")
        if ([string]::IsNullOrWhiteSpace($text)) { return }
        Write-Host $text
        if ($OnLine) { & $OnLine $text }
        if ($LogPath) { Write-CompositeLogLine -LogPath $LogPath -Line $text }
    }
    return $LASTEXITCODE
}
