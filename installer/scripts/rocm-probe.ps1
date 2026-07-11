# Shared ROCm / WSL probe helpers for GuideAnts installer scripts.
# Dot-source from guideants.ps1, rocm-runtime-compose.ps1, or stop_guideants.ps1.

function Get-WslUserDistroNames {
    if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) { return @() }

    $result = & wsl.exe -l -q 2>$null
    if ($LASTEXITCODE -ne 0) { return @() }

    $distros = New-Object System.Collections.Generic.List[string]
    foreach ($line in @($result)) {
        $name = (($line -as [string]) -replace "`0", '').Trim()
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if ($name -match '^(?i)docker-desktop(-data)?$') { continue }
        $distros.Add($name) | Out-Null
    }

    return @($distros)
}

function Invoke-WslDistroProbe {
    param(
        [Parameter(Mandatory = $true)][string]$Probe,
        [string]$Distro = ''
    )

    $targets = if ([string]::IsNullOrWhiteSpace($Distro)) {
        @(Get-WslUserDistroNames)
    }
    else {
        @($Distro)
    }

    foreach ($target in $targets) {
        $output = & wsl.exe -d $target sh -lc $Probe 2>$null
        if ($LASTEXITCODE -eq 0) {
            $value = ([string](@($output) | Select-Object -First 1)).Trim()
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                return $value
            }
        }
    }

    return $null
}

function ConvertTo-WslLinuxPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = [System.IO.Path]::GetFullPath($Path)
    if ($resolved -match '^([A-Za-z]):\\(.*)$') {
        $drive = $Matches[1].ToLower()
        $rest = ($Matches[2] -replace '\\', '/')
        return "/mnt/$drive/$rest"
    }

    return ($resolved -replace '\\', '/')
}

function Get-PreferredWslDistro {
    $distros = @(Get-WslUserDistroNames)
    if ($distros.Count -eq 0) { return $null }

    $ubuntu = @($distros | Where-Object { $_ -match '(?i)Ubuntu-24\.04' } | Select-Object -First 1)
    if ($ubuntu.Count -gt 0) { return [string]$ubuntu[0] }

    $anyUbuntu = @($distros | Where-Object { $_ -match '(?i)Ubuntu' } | Select-Object -First 1)
    if ($anyUbuntu.Count -gt 0) { return [string]$anyUbuntu[0] }

    return [string]$distros[0]
}

function Get-RocmInstallCommand {
    param(
        [Parameter(Mandatory = $true)][string]$RootDir,
        [string]$Distro = ''
    )

    if ([string]::IsNullOrWhiteSpace($Distro)) {
        $Distro = Get-PreferredWslDistro
    }
    if ([string]::IsNullOrWhiteSpace($Distro)) {
        $Distro = 'Ubuntu-24.04'
    }

    $installScript = Join-Path $RootDir 'scripts/install-rocm-wsl.sh'
    $wslScript = ConvertTo-WslLinuxPath -Path $installScript
    return "wsl -d $Distro -u root bash $wslScript"
}

function Write-RocmInstallHint {
    param(
        [Parameter(Mandatory = $true)][string]$RootDir,
        [scriptblock]$WarnFn = { param($m) Write-Host "[guideants][warn] $m" }
    )

    $cmd = Get-RocmInstallCommand -RootDir $RootDir
    & $WarnFn "Install ROCm in WSL with:"
    & $WarnFn "  $cmd"
    & $WarnFn "Or run: .\guideants.ps1 --install-rocm-wsl"
}

function Get-NormalizedWslCliText {
    param([string]$Text)
    # wsl.exe often emits UTF-16LE with embedded NULs when captured from PowerShell;
    # strip them so status/version parsing regexes match.
    return ($Text -replace "`0", '').Trim()
}

function Test-Wsl2Ready {
    if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) {
        return [pscustomobject]@{
            Ok = $false
            Message = 'WSL is not installed. Install WSL2: https://learn.microsoft.com/windows/wsl/install'
        }
    }

    $status = Get-NormalizedWslCliText -Text ((& wsl.exe --status 2>&1 | Out-String))
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject]@{
            Ok = $false
            Message = @(
                'Could not read WSL status. Ensure WSL2 is installed and enabled:',
                '  wsl --install',
                '  wsl --set-default-version 2',
                'See https://learn.microsoft.com/windows/wsl/install'
            ) -join "`n"
        }
    }

    if ($status -notmatch '(?i)Default Version:\s*2\b') {
        $distros = Get-NormalizedWslCliText -Text ((& wsl.exe -l -v 2>&1 | Out-String))
        if ($distros -match '(?m)^\s*\*?\s*\S+\s+\S+\s+2\s*$') {
            return [pscustomobject]@{ Ok = $true; Message = '' }
        }

        return [pscustomobject]@{
            Ok = $false
            Message = @(
                'WSL2 is required on Windows (Docker Desktop default backend).',
                'Your default WSL version is not 2. Fix with:',
                '  wsl --set-default-version 2',
                'Then reinstall or upgrade your Linux distro (e.g. wsl --install -d Ubuntu-24.04).',
                'Enable WSL integration in Docker Desktop: Settings -> Resources -> WSL integration.'
            ) -join "`n"
        }
    }

    return [pscustomobject]@{ Ok = $true; Message = '' }
}

function Get-RocmVersion {
    param(
        [string]$OsName = 'unknown',
        [bool]$IsWsl = $false
    )

    if (Test-Path -LiteralPath '/opt/rocm/.info/version') {
        try {
            $value = (Get-Content -LiteralPath '/opt/rocm/.info/version' | Select-Object -First 1).Trim()
            if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }
        }
        catch {
        }
    }

    if (Get-Command rocminfo -ErrorAction SilentlyContinue) {
        $output = & rocminfo 2>$null
        if ($LASTEXITCODE -eq 0) {
            $text = $output -join "`n"
            if ($text -match 'ROCm Runtime Version:\s*([0-9]+\.[0-9]+(?:\.[0-9]+)?)') {
                return $Matches[1]
            }
        }
    }

    if ((Get-Command apt -ErrorAction SilentlyContinue) -and (Get-Command dpkg -ErrorAction SilentlyContinue)) {
        $output = & dpkg -l rocm-core 2>$null
        if ($LASTEXITCODE -eq 0) {
            foreach ($line in @($output)) {
                if ($line -match '^ii\s+\S+\s+(\S+)') {
                    return $Matches[1]
                }
            }
        }
    }

    if ($OsName -eq 'windows' -and -not $IsWsl) {
        $probe = 'if [ -f /opt/rocm/.info/version ]; then head -n1 /opt/rocm/.info/version; elif command -v dpkg >/dev/null 2>&1; then dpkg -l rocm-core 2>/dev/null | awk ''/^ii/{print $3; exit}''; fi'
        $value = Invoke-WslDistroProbe -Probe $probe
        if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }
    }

    return $null
}

function Test-AmdGpuDetected {
    param(
        [string]$OsName = 'unknown',
        [bool]$IsWsl = $false
    )

    if (Test-Path -LiteralPath '/dev/kfd') { return $true }

    if (Get-Command rocminfo -ErrorAction SilentlyContinue) {
        & rocminfo 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { return $true }
    }

    if ($OsName -eq 'windows' -and -not $IsWsl) {
        try {
            $names = @(Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop | Select-Object -ExpandProperty Name)
            if (($names -join "`n") -match '(?i)AMD|Radeon') { return $true }
        }
        catch {
        }

        foreach ($distro in (Get-WslUserDistroNames)) {
            & wsl.exe -d $distro sh -lc 'test -e /dev/dxg || test -e /dev/kfd' 2>$null | Out-Null
            if ($LASTEXITCODE -eq 0) { return $true }

            & wsl.exe -d $distro sh -lc 'command -v rocminfo >/dev/null 2>&1 && HSA_ENABLE_DXG_DETECTION=1 rocminfo >/dev/null 2>&1' 2>$null | Out-Null
            if ($LASTEXITCODE -eq 0) { return $true }
        }
    }

    return $false
}

function Resolve-WslRocmLibrocdxgSource {
    foreach ($distro in (Get-WslUserDistroNames)) {
        foreach ($candidate in @(
                '/opt/rocm/lib/librocdxg.so.1.2.0',
                '/opt/rocm/lib/librocdxg.so',
                '/opt/rocm-7.2.4/lib/librocdxg.so',
                '/opt/rocm-7.2.1/lib/librocdxg.so',
                '/opt/rocm-7.2.0/lib/librocdxg.so'
            )) {
            $probe = "if test -e '$candidate'; then readlink -f '$candidate' 2>/dev/null || realpath '$candidate' 2>/dev/null || echo '$candidate'; fi"
            $found = Invoke-WslDistroProbe -Probe $probe -Distro $distro
            if (-not [string]::IsNullOrWhiteSpace($found)) {
                return @{ Distro = $distro; LinuxPath = $found }
            }
        }
    }

    return $null
}

function Resolve-WslRocmDidsSource {
    foreach ($distro in (Get-WslUserDistroNames)) {
        foreach ($candidate in @('/opt/rocm/share/rocdxg/dids.conf', '/usr/share/rocdxg/dids.conf')) {
            $probe = "test -f '$candidate' && echo '$candidate'"
            $found = Invoke-WslDistroProbe -Probe $probe -Distro $distro
            if (-not [string]::IsNullOrWhiteSpace($found)) {
                return @{ Distro = $distro; LinuxPath = $found }
            }
        }
    }

    return $null
}

function Test-NeedsRocmLibStage {
    param(
        [Parameter(Mandatory = $true)][string]$StagingPath,
        [Parameter(Mandatory = $true)][string]$SourceDistro,
        [Parameter(Mandatory = $true)][string]$SourceLinuxPath
    )

    if (-not (Test-Path -LiteralPath $StagingPath)) { return $true }

    $srcHashProbe = "md5sum -b '$SourceLinuxPath' 2>/dev/null | awk '{print `$1}'"
    $srcHash = Invoke-WslDistroProbe -Probe $srcHashProbe -Distro $SourceDistro
    if ([string]::IsNullOrWhiteSpace($srcHash)) { return $true }

    try {
        $dstHash = (Get-FileHash -LiteralPath $StagingPath -Algorithm MD5).Hash.ToLowerInvariant()
        return ($srcHash.ToLowerInvariant() -ne $dstHash)
    }
    catch {
        return $true
    }
}
