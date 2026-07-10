param(

    [Parameter(Mandatory = $true)][string]$DockerDir,

    [Parameter(Mandatory = $true)][string]$Backend

)



$OverrideFile = 'docker-compose.rocm-runtime.generated.yml'

$OutPath = Join-Path $DockerDir $OverrideFile

$RocmWslStagingRel = './volumes/rocm-wsl'



function Write-RocmLog([string]$Message) {

    Write-Host "[guideants] $Message"

}



function Write-RocmWarn([string]$Message) {

    Write-Host "[guideants][warn] $Message"

}



function Write-Utf8NoBomFile {

    param(

        [Parameter(Mandatory = $true)][string]$Path,

        [Parameter(Mandatory = $true)][string[]]$Content

    )

    $encoding = New-Object System.Text.UTF8Encoding $false

    [System.IO.File]::WriteAllLines($Path, $Content, $encoding)

}



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

    param([string]$Distro, [string]$Probe)

    $output = & wsl.exe -d $Distro sh -lc $Probe 2>$null

    if ($LASTEXITCODE -ne 0) { return $null }

    $value = ([string](@($output) | Select-Object -First 1)).Trim()

    if ([string]::IsNullOrWhiteSpace($value)) { return $null }

    return $value

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



function Resolve-WslRocmLibrocdxgSource {

    foreach ($distro in (Get-WslUserDistroNames)) {

        foreach ($candidate in @(

                '/opt/rocm/lib/librocdxg.so.1.2.0',

                '/opt/rocm/lib/librocdxg.so',

                '/opt/rocm-7.2.0/lib/librocdxg.so'

            )) {

            $probe = "if test -e '$candidate'; then readlink -f '$candidate' 2>/dev/null || realpath '$candidate' 2>/dev/null || echo '$candidate'; fi"

            $found = Invoke-WslDistroProbe -Distro $distro -Probe $probe

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

            $found = Invoke-WslDistroProbe -Distro $distro -Probe $probe

            if (-not [string]::IsNullOrWhiteSpace($found)) {

                return @{ Distro = $distro; LinuxPath = $found }

            }

        }

    }

    return $null

}



function Stage-WslRocmLibs {

    param([Parameter(Mandatory = $true)][string]$DockerDir)



    $source = Resolve-WslRocmLibrocdxgSource

    if ($null -eq $source) { return $null }



    $stagingLib = Join-Path $DockerDir 'volumes/rocm-wsl/lib'

    $stagingShare = Join-Path $DockerDir 'volumes/rocm-wsl/share'

    New-Item -ItemType Directory -Force -Path $stagingLib | Out-Null



    $wslStagingLib = ConvertTo-WslLinuxPath -Path $stagingLib

    $copyProbe = "cp -L '$($source.LinuxPath)' '$wslStagingLib/librocdxg.so'"

    & wsl.exe -d $source.Distro sh -lc $copyProbe 2>$null

    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $stagingLib 'librocdxg.so'))) {

        Write-RocmWarn "ROCm WSL: failed to stage librocdxg to $stagingLib"

        return $null

    }



    $result = @{

        LibRocdxg = "$RocmWslStagingRel/lib/librocdxg.so"

        Dids = ''

    }



    $didsSource = Resolve-WslRocmDidsSource

    if ($null -ne $didsSource) {

        New-Item -ItemType Directory -Force -Path $stagingShare | Out-Null

        $wslStagingShare = ConvertTo-WslLinuxPath -Path $stagingShare

        $didsCopyProbe = "cp '$($didsSource.LinuxPath)' '$wslStagingShare/dids.conf'"

        & wsl.exe -d $didsSource.Distro sh -lc $didsCopyProbe 2>$null | Out-Null

        if (Test-Path -LiteralPath (Join-Path $stagingShare 'dids.conf')) {

            $result.Dids = "$RocmWslStagingRel/share/dids.conf"

        }

    }



    return $result

}



function Test-RocmWslMode {

    $dockerOs = (& docker info --format '{{.OperatingSystem}}' 2>$null | Select-Object -First 1)

    if ([string]$dockerOs -match 'Docker Desktop') { return $true }

    if ($env:WSL_DISTRO_NAME -and (Test-Path -LiteralPath '/dev/dxg')) { return $true }

    return $false

}



function Write-RocmNativeOverride([string]$Path) {

    $lines = @(

        '# Generated by GuideAnts launcher - native Linux ROCm devices. Do not edit.',

        'services:',

        '  guideants-ai:',

        '    devices:',

        '      - /dev/kfd',

        '      - /dev/dri',

        '    group_add:',

        '      - video',

        '      - render'

    )

    Write-Utf8NoBomFile -Path $Path -Content $lines

}



function Write-RocmWslOverride {

    param(

        [string]$Path,

        [string]$LibDxCore,

        [string]$LibRocdxg,

        [string]$Dids = ''

    )

    $lines = @(

        '# Generated by GuideAnts launcher - WSL ROCDXG binds. Do not edit.',

        'services:',

        '  guideants-ai:',

        '    devices:',

        '      - /dev/dxg',

        '    cap_add:',

        '      - SYS_PTRACE',

        '    security_opt:',

        '      - seccomp:unconfined',

        '    environment:',

        '      - HSA_ENABLE_DXG_DETECTION=1',

        '    volumes:',

        '      - type: bind',

        "        source: $LibDxCore",

        '        target: /usr/lib/libdxcore.so',

        '        read_only: true',

        '      - type: bind',

        "        source: $LibRocdxg",

        '        target: /lib/librocdxg.so',

        '        read_only: true',

        '      - type: bind',

        "        source: $LibRocdxg",

        '        target: /usr/lib/librocdxg.so',

        '        read_only: true'

    )

    if (-not [string]::IsNullOrWhiteSpace($Dids)) {

        $lines += @(

            '      - type: bind',

            "        source: $Dids",

            '        target: /usr/share/rocdxg/dids.conf',

            '        read_only: true'

        )

    }

    Write-Utf8NoBomFile -Path $Path -Content $lines

}



if (Test-Path -LiteralPath $OutPath) {

    Remove-Item -LiteralPath $OutPath -Force

}



if ($Backend -ne 'rocm') {

    return

}



if (Test-RocmWslMode) {

    $libDxCore = '/usr/lib/wsl/lib/libdxcore.so'

    $staged = Stage-WslRocmLibs -DockerDir $DockerDir



    if ($null -eq $staged) {

        if ($env:WSL_DISTRO_NAME) {

            foreach ($candidate in @('/opt/rocm/lib/librocdxg.so', '/opt/rocm-7.2.0/lib/librocdxg.so')) {

                if (Test-Path -LiteralPath $candidate) {

                    $staged = @{

                        LibRocdxg = $candidate

                        Dids = ''

                    }

                    foreach ($didsCandidate in @('/opt/rocm/share/rocdxg/dids.conf', '/usr/share/rocdxg/dids.conf')) {

                        if (Test-Path -LiteralPath $didsCandidate) {

                            $staged.Dids = $didsCandidate

                            break

                        }

                    }

                    break

                }

            }

        }

    }



    if ($null -eq $staged -or [string]::IsNullOrWhiteSpace($staged.LibRocdxg)) {

        Write-RocmWarn 'ROCm WSL: librocdxg not found. Install ROCm in WSL: installer/scripts/install-rocm-wsl.sh'

        return

    }



    Write-RocmWslOverride -Path $OutPath -LibDxCore $libDxCore -LibRocdxg $staged.LibRocdxg -Dids $staged.Dids

    Write-RocmLog 'ROCm: WSL ROCDXG (/dev/dxg + staged librocdxg binds).'

}

else {

    Write-RocmNativeOverride -Path $OutPath

    Write-RocmLog 'ROCm: native Linux (/dev/kfd + /dev/dri).'

}

