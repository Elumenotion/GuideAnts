<# 
GuideAnts portable launcher - PowerShell companion to guideants.sh.

  Windows       : .\guideants.ps1
  Linux / macOS : pwsh ./guideants.ps1

What it does, in order:
  1. Detects your OS / shell environment.
  2. Checks Docker is installed and running.
  3. Reports memory and disk (warns if low, never blocks).
  4. Walks you through database layout, AI backend, and optional services.
  5. Pulls each selected image sequentially before starting the stack.
  6. Starts the stack, waits for health, opens your browser.

Flags:
  --doctor                 Run checks only; change nothing.
  --backend <none|cpu|cuda13|rocm|slim|vulkan>   Skip the AI backend prompt.
  --compose <ghcr|local>   Use GHCR images (default) or local build images.
  --mount <path>           Mount a host folder into a project (requires prior login).
  --unmount                Interactively remove a host folder mount (requires prior login).
  --reconfigure            Re-prompt for backend even if one was saved.
  --install-rocm-wsl       Install ROCm + ROCDXG in a user WSL distro (Windows).
  --yes                    Assume "yes" for prompts (auto-accept updates).
  --help                   Show this help.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $script:RootDir 'scripts/rocm-probe.ps1')
. (Join-Path $script:RootDir 'scripts/installer-wizard.ps1')
$script:InstallerLogFn = ${function:Write-Log}
$script:InstallerWarnFn = ${function:Write-WarnLog}
$script:InstallerDockerInvokeFn = ${function:Invoke-External}
$script:InstallerDockerCaptureFn = ${function:Invoke-ExternalCapture}
$script:DockerDir = Join-Path $script:RootDir 'docker'
$script:EnvFile = Join-Path $script:DockerDir '.env'
$script:StateFile = Join-Path $script:RootDir '.installer_state.env'
$script:HealthUrl = 'http://localhost:5107/'
$script:ApiBase = 'http://localhost:5107'
$script:HostMountOverrideFile = 'docker-compose.host-mounts.generated.yml'
$script:RocmRuntimeOverrideFile = 'docker-compose.rocm-runtime.generated.yml'
$script:VoicePackOverrideFile = 'docker-compose.voice-pack.local.yml'
$script:DockerDirectory = 'docker'

$script:Mode = 'install'
$script:BackendOverride = ''
$script:ComposeMode = 'ghcr'
$script:AssumeYes = $false
$script:Reconfigure = $false
$script:InstallRocmWsl = $false
$script:MountPath = ''
$script:Unmount = $false
$script:SelectedBackend = ''
$script:SelectedDbLayout = 'bundled'
$script:SelectedAiBackend = 'slim'
$script:SelectedComponents = @()
$script:SelectedComposeFragments = @()
$script:ComposeArgs = @()
$script:ComposeFile = ''
$script:LowRam = $false
$script:Recommended = ''
$script:RecommendationReason = ''
$script:UpdateDecision = 'skip'
$script:PullAlwaysServices = @()
$script:PullMissingServices = @()
$script:AuthToken = ''
$script:FallbackMinNvidiaDriver = '580.0'
$script:FallbackMinCudaVersion = '13.0'
$script:MinNvidiaDriver = $script:FallbackMinNvidiaDriver
$script:MinCudaVersion = $script:FallbackMinCudaVersion
$script:MinRocmVersion = '6.0.0'
$script:OsName = 'unknown'
$script:IsWsl = $false
$script:Arch = 'unknown'

function Write-Log {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "[guideants] $Message"
}

function Write-WarnLog {
    param([Parameter(Mandatory = $true)][string]$Message)
    [Console]::Error.WriteLine("[guideants][warn] $Message")
}

function Stop-WithError {
    param([Parameter(Mandatory = $true)][string]$Message)
    throw $Message
}

function Write-HRule {
    Write-Host '----------------------------------------------------------------'
}

function Show-Usage {
    @'
GuideAnts portable launcher - PowerShell companion to guideants.sh.

  Windows       : .\guideants.ps1
  Linux / macOS : pwsh ./guideants.ps1

What it does, in order:
  1. Detects your OS / shell environment.
  2. Checks Docker is installed and running.
  3. Reports memory and disk (warns if low, never blocks).
  4. Walks you through database layout, AI backend, and optional services.
  5. Pulls each selected image sequentially before starting the stack.
  6. Starts the stack, waits for health, opens your browser.

Flags:
  --doctor                 Run checks only; change nothing.
  --backend <none|cpu|cuda13|rocm|slim|vulkan>   Skip the AI backend prompt.
  --compose <ghcr|local>   Use GHCR images (default) or local build images.
  --mount <path>           Mount a host folder into a project (requires prior login).
  --unmount                Interactively remove a host folder mount (requires prior login).
  --reconfigure            Re-prompt for backend even if one was saved.
  --install-rocm-wsl       Install ROCm + ROCDXG in a user WSL distro (Windows).
  --yes                    Assume "yes" for prompts (auto-accept updates).
  --help                   Show this help.
'@
}

function Test-Command {
    param([Parameter(Mandatory = $true)][string]$Name)
    return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

function Get-LastExitCodeSafe {
    $var = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    if ($null -eq $var -or $null -eq $var.Value) {
        return 0
    }
    return [int]$var.Value
}

function Invoke-ExternalCapture {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [switch]$IgnoreErrors
    )

    try {
        $output = & $FilePath @ArgumentList 2>$null
        $exitCode = Get-LastExitCodeSafe
    }
    catch {
        if ($IgnoreErrors) {
            return [pscustomobject]@{ ExitCode = 1; Output = @() }
        }
        Stop-WithError "$FilePath failed: $($_.Exception.Message)"
    }

    if (-not $IgnoreErrors -and $exitCode -ne 0) {
        Stop-WithError "$FilePath $($ArgumentList -join ' ') failed with exit code $exitCode."
    }

    return [pscustomobject]@{ ExitCode = $exitCode; Output = @($output) }
}

function Invoke-External {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @()
    )

    & $FilePath @ArgumentList
    $exitCode = Get-LastExitCodeSafe
    if ($exitCode -ne 0) {
        Stop-WithError "$FilePath $($ArgumentList -join ' ') failed with exit code $exitCode."
    }
}

function Parse-Arguments {
    $rawArgs = @($args)
    for ($i = 0; $i -lt $rawArgs.Count; $i++) {
        switch ($rawArgs[$i]) {
            '--doctor' { $script:Mode = 'doctor' }
            '--yes' { $script:AssumeYes = $true }
            '-y' { $script:AssumeYes = $true }
            '--reconfigure' { $script:Reconfigure = $true }
            '--install-rocm-wsl' { $script:InstallRocmWsl = $true }
            '--backend' {
                if ($i + 1 -ge $rawArgs.Count) { Stop-WithError 'Missing value for --backend' }
                $script:BackendOverride = [string]$rawArgs[$i + 1]
                $i++
            }
            '--compose' {
                if ($i + 1 -ge $rawArgs.Count) { Stop-WithError 'Missing value for --compose' }
                $script:ComposeMode = [string]$rawArgs[$i + 1]
                $i++
            }
            '--mount' {
                if ($i + 1 -ge $rawArgs.Count) { Stop-WithError 'Missing value for --mount' }
                $script:MountPath = [string]$rawArgs[$i + 1]
                $i++
            }
            '--unmount' { $script:Unmount = $true }
            '--help' {
                Show-Usage
                exit 0
            }
            '-h' {
                Show-Usage
                exit 0
            }
            default {
                Stop-WithError "Unknown option: $($rawArgs[$i]) (try --help)"
            }
        }
    }

    if ($script:BackendOverride -ne '' -and $script:BackendOverride -notmatch '^(none|cpu|cuda13|rocm|slim|vulkan)$') {
        Stop-WithError '--backend must be none, cpu, cuda13, rocm, slim, or vulkan'
    }

    if ($script:ComposeMode -notin @('ghcr', 'local')) {
        Stop-WithError '--compose must be ghcr or local'
    }
}

function Initialize-Platform {
    $platform = if ($PSVersionTable.ContainsKey('Platform')) { [string]$PSVersionTable.Platform } else { '' }
    if ($platform -eq 'Unix') {
        $uname = (Invoke-ExternalCapture -FilePath 'uname' -ArgumentList @('-s') -IgnoreErrors).Output | Select-Object -First 1
        switch -Regex ([string]$uname) {
            '^Darwin' { $script:OsName = 'macos' }
            '^Linux' { $script:OsName = 'linux' }
            default { $script:OsName = 'unknown' }
        }
    }
    elseif ($platform -eq 'Win32NT' -or $env:OS -eq 'Windows_NT') {
        $script:OsName = 'windows'
    }
    else {
        $script:OsName = 'windows'
    }

    if ($script:OsName -eq 'linux' -and (Test-Path -LiteralPath '/proc/version')) {
        try {
            $procVersion = Get-Content -LiteralPath '/proc/version' -Raw
            if ($procVersion -match '(?i)microsoft|wsl') {
                $script:OsName = 'windows'
                $script:IsWsl = $true
            }
        }
        catch {
            $script:IsWsl = $false
        }
    }

    if ($script:OsName -eq 'windows' -and $env:WSL_DISTRO_NAME) {
        $script:IsWsl = $true
    }

    try {
        $script:Arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    }
    catch {
        if ($env:PROCESSOR_ARCHITECTURE) {
            $script:Arch = $env:PROCESSOR_ARCHITECTURE.ToLowerInvariant()
        }
    }
}

function Check-Docker {
    Write-Log 'Checking Docker...'
    if (-not (Test-Command 'docker')) {
        switch ($script:OsName) {
            'macos' { Stop-WithError 'Docker not found. Install Docker Desktop: https://www.docker.com/products/docker-desktop/ then rerun.' }
            'windows' { Stop-WithError 'Docker not found. Install Docker Desktop (with WSL2 integration), then rerun.' }
            default { Stop-WithError 'Docker not found. Install Docker Engine 24+ and the Compose plugin, then rerun.' }
        }
    }

    $compose = Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('compose', 'version') -IgnoreErrors
    if ($compose.ExitCode -ne 0) {
        Stop-WithError "Docker Compose plugin not found. Install/upgrade Docker (the legacy 'docker-compose' v1 is not supported)."
    }

    $info = Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('info') -IgnoreErrors
    if ($info.ExitCode -ne 0) {
        if ($script:OsName -eq 'linux') {
            Stop-WithError "Docker daemon not reachable. Start it (e.g. 'sudo systemctl start docker') and rerun."
        }
        Stop-WithError 'Docker daemon not reachable. Start Docker Desktop and rerun.'
    }

    if ($script:OsName -eq 'windows' -and -not $script:IsWsl -and (Test-Command 'wsl.exe')) {
        $wslStatus = Test-Wsl2Ready
        if (-not $wslStatus.Ok) {
            Stop-WithError $wslStatus.Message
        }
    }

    Write-Log 'Docker is installed and running.'
}

function Get-HostRamGiB {
    try {
        if ($script:OsName -eq 'windows' -and -not $script:IsWsl) {
            $computer = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
            return [int][math]::Round(([double]$computer.TotalPhysicalMemory) / 1GB)
        }

        if ($script:OsName -eq 'macos') {
            $bytes = (Invoke-ExternalCapture -FilePath 'sysctl' -ArgumentList @('-n', 'hw.memsize') -IgnoreErrors).Output | Select-Object -First 1
            if ($bytes) { return [int][math]::Round(([double]$bytes) / 1GB) }
        }

        if (Test-Path -LiteralPath '/proc/meminfo') {
            $line = Get-Content -LiteralPath '/proc/meminfo' | Where-Object { $_ -match '^MemTotal:' } | Select-Object -First 1
            if ($line -match '(\d+)') {
                return [int][math]::Round(([double]$Matches[1]) / 1048576)
            }
        }
    }
    catch {
        return 0
    }

    return 0
}

function Get-DockerRamGiB {
    $result = Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('info', '--format', '{{.MemTotal}}') -IgnoreErrors
    if ($result.ExitCode -ne 0) { return 0 }
    $bytes = ($result.Output | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace([string]$bytes)) { return 0 }
    try {
        return [int][math]::Round(([double]$bytes) / 1GB)
    }
    catch {
        return 0
    }
}

function Format-Bytes {
    param([Parameter(Mandatory = $true)][double]$Bytes)
    if ($Bytes -ge 1TB) { return ('{0:N1} TiB' -f ($Bytes / 1TB)) }
    if ($Bytes -ge 1GB) { return ('{0:N1} GiB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MiB' -f ($Bytes / 1MB)) }
    return ('{0:N0} B' -f $Bytes)
}

function Get-FreeDiskDescription {
    try {
        $resolved = Resolve-Path -LiteralPath $script:DockerDir -ErrorAction Stop
        $root = [System.IO.Path]::GetPathRoot($resolved.Path)
        $drive = [System.IO.DriveInfo]::new($root)
        return Format-Bytes -Bytes $drive.AvailableFreeSpace
    }
    catch {
        return ''
    }
}

function Report-Resources {
    $hostRam = Get-HostRamGiB
    $dockerRam = Get-DockerRamGiB
    Write-Log "Host RAM: $hostRam GiB    Docker engine RAM: $dockerRam GiB"

    $disk = Get-FreeDiskDescription
    if (-not [string]::IsNullOrWhiteSpace($disk)) {
        Write-Log "Free disk on bundle volume: $disk"
    }

    $script:LowRam = $false
    if ($dockerRam -gt 0 -and $dockerRam -lt 16) {
        $script:LowRam = $true
        Write-WarnLog "Docker has < 16 GiB. Full stacks (cpu/cuda13/rocm) want >=16 GiB; 'slim' runs in ~8 GiB."
    }
}

function Ask-YesNo {
    param(
        [Parameter(Mandatory = $true)][string]$Prompt,
        [string]$Default = 'Y'
    )

    if ($script:AssumeYes) { return $true }
    $reply = Read-Host $Prompt
    if ([string]::IsNullOrWhiteSpace($reply)) { $reply = $Default }
    return $reply -match '^[Yy]'
}

function Invoke-InstallRocmWsl {
    if ($script:OsName -ne 'windows' -or $script:IsWsl) {
        Stop-WithError '--install-rocm-wsl is only supported on native Windows with WSL2.'
    }

    $distro = Get-PreferredWslDistro
    if ([string]::IsNullOrWhiteSpace($distro)) {
        Stop-WithError 'No user WSL distro found. Install one: wsl --install -d Ubuntu-24.04'
    }

    $installScript = Join-Path $script:RootDir 'scripts/install-rocm-wsl.sh'
    if (-not (Test-Path -LiteralPath $installScript)) {
        Stop-WithError "Install script not found: $installScript"
    }

    $wslScript = ConvertTo-WslLinuxPath -Path $installScript
    $wslArgs = @('-d', $distro, '-u', 'root', 'bash', $wslScript)
    if ($script:AssumeYes) { $wslArgs += '--yes' }

    Write-Log "Installing ROCm in WSL distro '$distro'..."
    & wsl.exe @wslArgs
    if ((Get-LastExitCodeSafe) -ne 0) {
        Stop-WithError 'ROCm WSL install failed.'
    }
    Write-Log 'ROCm WSL install complete.'
}

function Get-NvidiaDriverFull {
    if (-not (Test-Command 'nvidia-smi')) { return $null }
    $result = Invoke-ExternalCapture -FilePath 'nvidia-smi' -ArgumentList @('--query-gpu=driver_version', '--format=csv,noheader') -IgnoreErrors
    if ($result.ExitCode -ne 0) { return $null }
    $value = ([string]($result.Output | Select-Object -First 1)).Trim()
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    return ($value -replace '\s+', '')
}

function Get-NvidiaDriverMajor {
    $driver = Get-NvidiaDriverFull
    if ([string]::IsNullOrWhiteSpace($driver)) { return $null }
    return ($driver -split '\.')[0]
}

function Get-NvidiaCudaVersion {
    if (-not (Test-Command 'nvidia-smi')) { return $null }
    $result = Invoke-ExternalCapture -FilePath 'nvidia-smi' -ArgumentList @() -IgnoreErrors
    if ($result.ExitCode -ne 0) { return $null }
    $text = ($result.Output -join "`n")
    if ($text -match 'CUDA Version:\s*([0-9]+\.[0-9]+)') {
        return $Matches[1]
    }
    return $null
}

function Test-VersionGte {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Minimum
    )

    $left = @($Value -split '[^0-9]+' | Where-Object { $_ -ne '' })
    $right = @($Minimum -split '[^0-9]+' | Where-Object { $_ -ne '' })
    $max = [math]::Max($left.Count, $right.Count)
    for ($i = 0; $i -lt $max; $i++) {
        $a = if ($i -lt $left.Count) { [int]$left[$i] } else { 0 }
        $b = if ($i -lt $right.Count) { [int]$right[$i] } else { 0 }
        if ($a -gt $b) { return $true }
        if ($a -lt $b) { return $false }
    }

    return $true
}

function Resolve-CudaImageRef {
    param([Parameter(Mandatory = $true)][string]$ComposePath)

    $result = Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('compose', '-f', $ComposePath, '--env-file', $script:EnvFile, 'config', '--format', 'json') -IgnoreErrors
    if ($result.ExitCode -ne 0) { return $null }

    try {
        $config = ($result.Output -join "`n") | ConvertFrom-Json
        if ($null -ne $config.services.'guideants-ai'.image) {
            return [string]$config.services.'guideants-ai'.image
        }
    }
    catch {
        return $null
    }

    return $null
}

function Invoke-Http {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Uri,
        [hashtable]$Headers = @{},
        [string]$Body = $null,
        [string]$ContentType = 'application/json',
        [int]$TimeoutSeconds = 30
    )

    try {
        Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue | Out-Null
    }
    catch {
    }

    $client = [System.Net.Http.HttpClient]::new()
    $request = $null
    $response = $null
    try {
        $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
        $methodValue = [System.Net.Http.HttpMethod]::new($Method.ToUpperInvariant())
        $request = [System.Net.Http.HttpRequestMessage]::new($methodValue, $Uri)
        foreach ($key in $Headers.Keys) {
            $null = $request.Headers.TryAddWithoutValidation([string]$key, [string]$Headers[$key])
        }

        if ($null -ne $Body) {
            $request.Content = [System.Net.Http.StringContent]::new($Body, [System.Text.Encoding]::UTF8, $ContentType)
        }

        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $responseBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Body = $responseBody
        }
    }
    catch {
        return [pscustomobject]@{
            StatusCode = 0
            Body = ''
        }
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        if ($null -ne $request) { $request.Dispose() }
        $client.Dispose()
    }
}

function Get-ImageRepoAndTag {
    param([Parameter(Mandatory = $true)][string]$ImageRef)

    $lastSlash = $ImageRef.LastIndexOf('/')
    $lastColon = $ImageRef.LastIndexOf(':')
    if ($lastColon -gt $lastSlash) {
        return [pscustomobject]@{
            Repo = $ImageRef.Substring(0, $lastColon)
            Tag = $ImageRef.Substring($lastColon + 1)
        }
    }

    return [pscustomobject]@{
        Repo = $ImageRef
        Tag = 'latest'
    }
}

function Detect-CudaRequirements {
    param([Parameter(Mandatory = $true)][string]$ImageRef)

    $script:MinCudaVersion = $script:FallbackMinCudaVersion
    $script:MinNvidiaDriver = $script:FallbackMinNvidiaDriver

    $envResult = Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('inspect', $ImageRef, '--format', '{{range .Config.Env}}{{println .}}{{end}}') -IgnoreErrors
    $envs = if ($envResult.ExitCode -eq 0) { @($envResult.Output) } else { @() }

    if ($envs.Count -eq 0) {
        Write-Log 'Image not available locally; pulling config from registry...'
        $parts = Get-ImageRepoAndTag -ImageRef $ImageRef
        if ($parts.Repo -like 'ghcr.io/*') {
            $ghcrPath = $parts.Repo.Substring('ghcr.io/'.Length)
            $tokenResponse = Invoke-Http -Method 'GET' -Uri "https://ghcr.io/token?scope=repository:$ghcrPath`:pull" -TimeoutSeconds 20
            if ($tokenResponse.StatusCode -eq 200 -and -not [string]::IsNullOrWhiteSpace($tokenResponse.Body)) {
                try {
                    $token = (($tokenResponse.Body | ConvertFrom-Json).token)
                    if (-not [string]::IsNullOrWhiteSpace([string]$token)) {
                        $manifestHeaders = @{
                            Authorization = "Bearer $token"
                            Accept = 'application/vnd.oci.image.manifest.v1+json,application/vnd.docker.distribution.manifest.v2+json'
                        }
                        $manifest = Invoke-Http -Method 'GET' -Uri "https://ghcr.io/v2/$ghcrPath/manifests/$($parts.Tag)" -Headers $manifestHeaders -TimeoutSeconds 30
                        if ($manifest.StatusCode -eq 200) {
                            $manifestJson = $manifest.Body | ConvertFrom-Json
                            $configDigest = [string]$manifestJson.config.digest
                            if (-not [string]::IsNullOrWhiteSpace($configDigest)) {
                                $configResponse = Invoke-Http -Method 'GET' -Uri "https://ghcr.io/v2/$ghcrPath/blobs/$configDigest" -Headers @{ Authorization = "Bearer $token" } -TimeoutSeconds 30
                                if ($configResponse.StatusCode -eq 200) {
                                    $configJson = $configResponse.Body | ConvertFrom-Json
                                    $envs = @($configJson.config.Env)
                                }
                            }
                        }
                    }
                }
                catch {
                    $envs = @()
                }
            }
        }
    }

    if ($envs.Count -eq 0) {
        Write-WarnLog "Could not inspect CUDA image; using fallback minimums (CUDA >= $($script:MinCudaVersion), driver >= $($script:MinNvidiaDriver))."
        return
    }

    $requireLine = $envs | Where-Object { $_ -like 'NVIDIA_REQUIRE_CUDA=*' } | Select-Object -First 1
    if (-not [string]::IsNullOrWhiteSpace([string]$requireLine)) {
        $requireValue = ([string]$requireLine).Substring('NVIDIA_REQUIRE_CUDA='.Length)
        if ($requireValue -match 'cuda>=([0-9]+\.[0-9]+)') {
            $script:MinCudaVersion = $Matches[1]
        }

        $driverMatches = [regex]::Matches($requireValue, 'driver>=([0-9]+)')
        if ($driverMatches.Count -gt 0) {
            $minDriverMajor = @($driverMatches | ForEach-Object { [int]$_.Groups[1].Value } | Sort-Object | Select-Object -First 1)
            if ($minDriverMajor.Count -gt 0) {
                $script:MinNvidiaDriver = "$($minDriverMajor[0]).0"
            }
        }

        Write-Log "Image requires: CUDA >= $($script:MinCudaVersion), NVIDIA driver >= $($script:MinNvidiaDriver)"
    }
    else {
        Write-WarnLog 'Image has no NVIDIA_REQUIRE_CUDA env var; using fallback minimums.'
    }
}

function Check-GpuDrivers {
    param([Parameter(Mandatory = $true)][string]$Backend)

    if ($Backend -eq 'cuda13') {
        Write-HRule
        Write-Log 'Checking NVIDIA / CUDA driver versions...'

        $composePath = Join-Path $script:DockerDir (Get-ComposeFileForBackend -Backend $Backend)
        $cudaImageRef = Resolve-CudaImageRef -ComposePath $composePath
        if (-not [string]::IsNullOrWhiteSpace($cudaImageRef)) {
            Write-Log "CUDA image: $cudaImageRef"
            Detect-CudaRequirements -ImageRef $cudaImageRef
        }
        else {
            $script:MinCudaVersion = $script:FallbackMinCudaVersion
            $script:MinNvidiaDriver = $script:FallbackMinNvidiaDriver
            Write-WarnLog 'Could not resolve CUDA image from compose file; using fallback minimums.'
        }

        $driver = Get-NvidiaDriverFull
        if ([string]::IsNullOrWhiteSpace($driver)) {
            Write-WarnLog 'nvidia-smi not found or not working. CUDA backend requires NVIDIA drivers.'
            Write-WarnLog 'Install the latest NVIDIA drivers: https://www.nvidia.com/Download/index.aspx'
            return
        }

        Write-Log "NVIDIA driver version: $driver"
        if (-not (Test-VersionGte -Value $driver -Minimum $script:MinNvidiaDriver)) {
            Write-WarnLog "NVIDIA driver $driver is below the minimum ($($script:MinNvidiaDriver)) required by the CUDA image."
            Write-WarnLog 'The CUDA container will refuse to start without a compatible driver.'
            Write-WarnLog 'Update your drivers: https://www.nvidia.com/Download/index.aspx'
            if ($script:OsName -eq 'linux') {
                Write-WarnLog "  Ubuntu/Debian: sudo apt update && sudo apt install --upgrade nvidia-driver-$($script:MinNvidiaDriver.Split('.')[0])"
                Write-WarnLog '  RHEL/Fedora:   sudo dnf upgrade nvidia-driver'
            }
            elseif ($script:OsName -eq 'windows') {
                Write-WarnLog '  Download the latest driver from the NVIDIA website, or use GeForce Experience to update.'
            }
            Stop-WithError "Aborting. Update your NVIDIA drivers to >= $($script:MinNvidiaDriver) and rerun, or use --backend cpu."
        }

        Write-Log "NVIDIA driver $driver meets minimum requirement (>= $($script:MinNvidiaDriver))."
        $cudaVersion = Get-NvidiaCudaVersion
        if (-not [string]::IsNullOrWhiteSpace($cudaVersion)) {
            Write-Log "CUDA version reported by driver: $cudaVersion"
            if (-not (Test-VersionGte -Value $cudaVersion -Minimum $script:MinCudaVersion)) {
                Write-WarnLog "CUDA $cudaVersion is below the minimum ($($script:MinCudaVersion)) required by the CUDA image."
                Write-WarnLog "The CUDA container will refuse to start without CUDA >= $($script:MinCudaVersion) support."
                Write-WarnLog "Update your NVIDIA drivers to get CUDA $($script:MinCudaVersion)+ support."
                Stop-WithError 'Aborting. Update your NVIDIA drivers and rerun, or use --backend cpu.'
            }
            Write-Log "CUDA $cudaVersion meets minimum requirement (>= $($script:MinCudaVersion))."
        }
        else {
            Write-WarnLog 'Could not detect CUDA version from nvidia-smi.'
        }
    }
    elseif ($Backend -eq 'rocm') {
        Write-HRule
        Write-Log 'Checking AMD ROCm driver version...'
        $rocm = Get-RocmVersion -OsName $script:OsName -IsWsl $script:IsWsl
        if (-not [string]::IsNullOrWhiteSpace($rocm)) {
            Write-Log "ROCm version: $rocm"
            if (-not (Test-VersionGte -Value $rocm -Minimum $script:MinRocmVersion)) {
                Write-WarnLog "ROCm $rocm is below the minimum ($($script:MinRocmVersion))."
                Write-WarnLog 'Update ROCm: https://rocm.docs.amd.com/projects/install-on-linux/en/latest/'
                Write-WarnLog '  Ubuntu/Debian: sudo apt update && sudo apt upgrade rocm'
                Write-WarnLog '  RHEL/Fedora:   sudo dnf upgrade rocm'
                if (-not (Ask-YesNo -Prompt 'Continue anyway with outdated ROCm? [y/N]' -Default 'N')) {
                    Stop-WithError 'Aborting. Update ROCm and rerun.'
                }
            }
            else {
                Write-Log "ROCm $rocm meets minimum requirement (>= $($script:MinRocmVersion))."
            }
        }
        else {
            if (Test-AmdGpuDetected -OsName $script:OsName -IsWsl $script:IsWsl) {
                Write-WarnLog 'AMD GPU detected but could not determine ROCm version.'
                Write-WarnLog 'Ensure ROCm is properly installed: https://rocm.docs.amd.com/projects/install-on-linux/en/latest/'
                Write-WarnLog 'Without a working ROCm installation, GPU acceleration may not function.'
                if ($script:OsName -eq 'windows' -and -not $script:IsWsl) {
                    Write-RocmInstallHint -RootDir $script:RootDir -WarnFn ${function:Write-WarnLog}
                }
            }
            else {
                Write-WarnLog 'No AMD GPU device found. ROCm backend requires an AMD GPU with ROCm drivers.'
                if ($script:OsName -eq 'windows' -and -not $script:IsWsl) {
                    Write-RocmInstallHint -RootDir $script:RootDir -WarnFn ${function:Write-WarnLog}
                }
                else {
                    Write-WarnLog 'Install ROCm: https://rocm.docs.amd.com/projects/install-on-linux/en/latest/'
                }
                if (-not (Ask-YesNo -Prompt 'Continue anyway without ROCm? [y/N]' -Default 'N')) {
                    Stop-WithError 'Aborting. Install ROCm drivers and rerun.'
                }
            }
        }
    }
    elseif ($Backend -eq 'vulkan') {
        Write-HRule
        $runtime = if ($env:GA_VULKAN_RUNTIME) { $env:GA_VULKAN_RUNTIME } else { 'runc' }
        $device = if ($env:GA_VULKAN_DEVICE) { $env:GA_VULKAN_DEVICE } else { '/dev/dxg' }
        $icdPath = if ($env:GA_VULKAN_ICD) { $env:GA_VULKAN_ICD } else { 'dzn_icd.json' }
        $icd = [System.IO.Path]::GetFileName($icdPath)
        Write-Log "Vulkan GPU wiring resolved (runtime='$runtime', device='$device', icd='$icd')."
    }
}

function Recommend-Backend {
    $major = Get-NvidiaDriverMajor
    if ($major -match '^[0-9]+$') {
        if ([int]$major -ge 580) {
            $script:Recommended = 'cuda13'
            $script:RecommendationReason = "NVIDIA driver R$major detected (>= R580)."
            return
        }

        $script:Recommended = 'cpu'
        $script:RecommendationReason = "NVIDIA driver R$major is below the CUDA 13 minimum (R580); using CPU."
        return
    }

    if (Test-AmdGpuDetected -OsName $script:OsName -IsWsl $script:IsWsl) {
        $script:Recommended = 'rocm'
        $script:RecommendationReason = 'AMD ROCm-capable GPU detected.'
        return
    }

    if ($script:LowRam) {
        $script:Recommended = 'slim'
        $script:RecommendationReason = 'No usable local GPU and limited RAM; slim (cloud AI) recommended.'
    }
    else {
        $script:Recommended = 'cpu'
        $script:RecommendationReason = 'No usable local GPU detected; using CPU.'
    }
}

function Get-InstallerStateValue {
    param([Parameter(Mandatory = $true)][string]$Key)

    if (-not (Test-Path -LiteralPath $script:StateFile)) { return $null }
    foreach ($line in Get-Content -LiteralPath $script:StateFile) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#')) { continue }
        $separatorIndex = $trimmed.IndexOf('=')
        if ($separatorIndex -lt 1) { continue }
        $lineKey = $trimmed.Substring(0, $separatorIndex).Trim()
        if ($lineKey -eq $Key) {
            return $trimmed.Substring($separatorIndex + 1).Trim()
        }
    }

    return $null
}

function Choose-Backend {
    if (-not [string]::IsNullOrWhiteSpace($script:BackendOverride)) {
        $script:SelectedBackend = $script:BackendOverride
        Write-Log "Backend forced via --backend: $($script:SelectedBackend)"
        return
    }

    if (-not $script:Reconfigure -and (Test-Path -LiteralPath $script:StateFile)) {
        $savedBackend = Get-InstallerStateValue -Key 'BACKEND'
        if ($savedBackend -match '^(cpu|cuda13|rocm|slim|vulkan)$') {
            $script:SelectedBackend = $savedBackend
            Write-Log "Using previously saved backend: $($script:SelectedBackend) (run with --reconfigure to change)"
            return
        }
    }

    Recommend-Backend
    Write-HRule
    Write-Log "Recommended backend: $($script:Recommended)"
    Write-Log "  ($($script:RecommendationReason))"

    $backendKeys = New-Object System.Collections.Generic.List[string]
    $backendLabels = New-Object System.Collections.Generic.List[string]
    $major = Get-NvidiaDriverMajor
    if ($major -match '^[0-9]+$' -and [int]$major -ge 580) {
        $backendKeys.Add('cuda13') | Out-Null
        $backendLabels.Add("cuda13  Local AI on NVIDIA GPU (R$major driver detected, ~50 GB disk)") | Out-Null
    }
    if (Test-AmdGpuDetected -OsName $script:OsName -IsWsl $script:IsWsl) {
        $backendKeys.Add('rocm') | Out-Null
        $backendLabels.Add('rocm    Local AI on AMD GPU (ROCm device detected, ~50 GB disk)') | Out-Null
    }

    $backendKeys.Add('vulkan') | Out-Null
    $backendLabels.Add('vulkan  Local AI on any GPU via Vulkan (NVIDIA/AMD/Intel, ~30 GB disk)') | Out-Null
    $backendKeys.Add('cpu') | Out-Null
    $backendLabels.Add('cpu     Local AI, no GPU (slower, ~25 GB disk)') | Out-Null
    $backendKeys.Add('slim') | Out-Null
    $backendLabels.Add('slim    No local model runtime; use cloud AI providers (lightest, ~20 GB disk)') | Out-Null

    Write-Host ''
    Write-Host '  Choose a backend:'
    for ($i = 0; $i -lt $backendKeys.Count; $i++) {
        Write-Host ('    {0}) {1}' -f ($i + 1), $backendLabels[$i])
    }
    Write-Host ''

    if ($script:AssumeYes) {
        $script:SelectedBackend = $script:Recommended
        Write-Log "--yes: using recommended backend ($($script:SelectedBackend))."
        return
    }

    $choice = Read-Host "Enter 1-$($backendKeys.Count), or press Enter for recommended [$($script:Recommended)]"
    if ([string]::IsNullOrWhiteSpace($choice)) {
        $script:SelectedBackend = $script:Recommended
    }
    elseif ($choice -match '^[0-9]+$' -and [int]$choice -ge 1 -and [int]$choice -le $backendKeys.Count) {
        $script:SelectedBackend = $backendKeys[[int]$choice - 1]
    }
    else {
        Write-WarnLog "Unrecognized choice '$choice'; using recommended."
        $script:SelectedBackend = $script:Recommended
    }
}

function Add-ComposeOverrideIfValid {
    param(
        [Parameter(Mandatory = $true)][string[]]$ComposeArgs,
        [Parameter(Mandatory = $true)][string]$OverrideFile
    )

    $overridePath = Join-Path $script:DockerDir $OverrideFile
    if (-not (Test-Path -LiteralPath $overridePath)) { return $ComposeArgs }

    $configCheck = Invoke-ExternalCapture -FilePath 'docker' -ArgumentList (@('compose') + $ComposeArgs + @('-f', $overridePath, '--env-file', $script:EnvFile, 'config')) -IgnoreErrors
    if ($configCheck.ExitCode -eq 0) {
        Write-Log "Including compose override: $OverrideFile"
        return @($ComposeArgs + @('-f', $overridePath))
    }

    Write-WarnLog "Ignoring invalid compose override docker/$OverrideFile."
    return $ComposeArgs
}

function Build-ComposeArgs {
    $args = @(Resolve-InstallerComposeArgs -DockerDir $script:DockerDir -FragmentFiles $script:SelectedComposeFragments)
    $args = Add-ComposeOverrideIfValid -ComposeArgs $args -OverrideFile $script:HostMountOverrideFile
    $args = Add-ComposeOverrideIfValid -ComposeArgs $args -OverrideFile $script:RocmRuntimeOverrideFile
    $args = Add-ComposeOverrideIfValid -ComposeArgs $args -OverrideFile $script:VoicePackOverrideFile
    return $args
}

function Select-RocmRuntime {
    if ($script:SelectedAiBackend -ne 'rocm') {
        $override = Join-Path $script:DockerDir $script:RocmRuntimeOverrideFile
        if (Test-Path -LiteralPath $override) {
            Remove-Item -LiteralPath $override -Force
        }
        return
    }

    $helper = Join-Path $script:RootDir 'scripts/rocm-runtime-compose.ps1'
    & $helper -DockerDir $script:DockerDir -Backend $script:SelectedAiBackend -RootDir $script:RootDir
}

function Select-VulkanRuntime {
    if ($script:SelectedAiBackend -ne 'vulkan') { return }

    $dockerOs = (Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('info', '--format', '{{.OperatingSystem}}') -IgnoreErrors).Output | Select-Object -First 1
    if ([string]$dockerOs -match 'Docker Desktop') {
        Write-Log 'Vulkan: Docker Desktop -> Mesa dzn over D3D12 (/dev/dxg). Using compose defaults.'
        return
    }

    $dev = '/dev/null'
    if (Test-Path -LiteralPath '/dev/dri') { $dev = '/dev/dri' }
    $env:GA_VULKAN_DEVICE = $dev
    $env:GA_VULKAN_DRIVER_LIBS = '/usr/lib'
    $env:GA_VULKAN_LD_LIBRARY_PATH = '/usr/lib/x86_64-linux-gnu'

    $runtimes = (Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('info', '--format', '{{json .Runtimes}}') -IgnoreErrors).Output -join "`n"
    if ($runtimes -match '"nvidia"') {
        $env:GA_VULKAN_RUNTIME = 'nvidia'
        $env:GA_VULKAN_ICD = '/usr/share/vulkan/icd.d/nvidia_icd.json'
        Write-Log "Vulkan: native Linux NVIDIA -> nvidia runtime injects the Vulkan ICD (device $dev)."
        Write-Log "       (If the GPU isn't found, the injected ICD path may differ; override GA_VULKAN_ICD.)"
    }
    elseif (Test-Path -LiteralPath '/dev/dri') {
        $icd = ''
        $vendorFiles = @(Get-ChildItem -Path '/sys/class/drm/renderD*/device/vendor' -ErrorAction SilentlyContinue)
        foreach ($vendorFile in $vendorFiles) {
            $vendor = ''
            try { $vendor = (Get-Content -LiteralPath $vendorFile.FullName -Raw).Trim() } catch { }
            if ($vendor -eq '0x1002') { $icd = '/usr/share/vulkan/icd.d/radeon_icd.x86_64.json'; break }
            if ($vendor -eq '0x8086') { $icd = '/usr/share/vulkan/icd.d/intel_icd.x86_64.json'; break }
        }

        if (-not [string]::IsNullOrWhiteSpace($icd)) {
            $env:GA_VULKAN_ICD = $icd
            Write-Log "Vulkan: native Linux Mesa via /dev/dri (ICD $([System.IO.Path]::GetFileName($icd)))."
        }
        else {
            $env:GA_VULKAN_ICD = '/usr/share/vulkan/icd.d/radeon_icd.x86_64.json'
            Write-WarnLog 'Vulkan: /dev/dri present but GPU vendor undetermined; assuming AMD RADV. Override GA_VULKAN_ICD if this is an Intel GPU.'
        }
    }
    else {
        Write-WarnLog 'Vulkan: native Linux with no nvidia runtime and no /dev/dri; no GPU device found.'
        Write-WarnLog '        LLM and image generation will run on CPU. Install Mesa (AMD/Intel) or the nvidia-container-toolkit (NVIDIA).'
    }
}

function Get-ComposeFileForBackend {
    param([Parameter(Mandatory = $true)][string]$Backend)

    if ($script:ComposeMode -eq 'local') {
        switch ($Backend) {
            'slim' { return 'docker-compose.slim.yml' }
            'cuda13' { return 'docker-compose.cuda.yml' }
            'rocm' { return 'docker-compose.rocm.yml' }
            'vulkan' { return 'docker-compose.vulkan.yml' }
            default { return 'docker-compose.cpu.yml' }
        }
    }

    switch ($Backend) {
        'slim' { return 'docker-compose.ghcr-slim.yml' }
        'cuda13' { return 'docker-compose.ghcr-cuda13.yml' }
        'rocm' { return 'docker-compose.ghcr-rocm.yml' }
        'vulkan' { return 'docker-compose.ghcr-vulkan.yml' }
        default { return 'docker-compose.ghcr-cpu.yml' }
    }
}

function Get-RemoteDigest {
    param([Parameter(Mandatory = $true)][string]$ImageRef)

    $buildx = Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('buildx', 'version') -IgnoreErrors
    if ($buildx.ExitCode -eq 0) {
        $inspect = Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('buildx', 'imagetools', 'inspect', $ImageRef) -IgnoreErrors
        if ($inspect.ExitCode -eq 0) {
            foreach ($line in $inspect.Output) {
                if ($line -match '^Digest:\s*(\S+)') {
                    return $Matches[1]
                }
            }
        }
    }

    $manifest = Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('manifest', 'inspect', '-v', $ImageRef) -IgnoreErrors
    if ($manifest.ExitCode -eq 0) {
        $text = $manifest.Output -join "`n"
        if ($text -match '"digest"\s*:\s*"(sha256:[a-f0-9]+)"') {
            return $Matches[1]
        }
    }

    return $null
}

function Get-LocalDigest {
    param([Parameter(Mandatory = $true)][string]$ImageRef)

    $result = Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('image', 'inspect', '--format', '{{range .RepoDigests}}{{println .}}{{end}}', $ImageRef) -IgnoreErrors
    if ($result.ExitCode -ne 0) { return $null }
    foreach ($line in $result.Output) {
        $value = ([string]$line).Trim()
        if ($value -match '@(.+)$') {
            return $Matches[1]
        }
    }

    return $null
}

function Get-ServicesForImages {
    param(
        [Parameter(Mandatory = $true)][string]$ComposePath,
        [string[]]$Images = @()
    )

    if ($Images.Count -eq 0) { return @() }

    $result = Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('compose', '-f', $ComposePath, '--env-file', $script:EnvFile, 'config', '--format', 'json') -IgnoreErrors
    if ($result.ExitCode -ne 0) { return @() }

    try {
        $services = New-Object System.Collections.Generic.List[string]
        $config = ($result.Output -join "`n") | ConvertFrom-Json
        foreach ($property in $config.services.PSObject.Properties) {
            $serviceName = $property.Name
            $image = [string]$property.Value.image
            if ($Images -contains $image) {
                $services.Add($serviceName) | Out-Null
            }
        }

        return @($services)
    }
    catch {
        return @()
    }
}

function Plan-Pull {
    param([Parameter(Mandatory = $true)][string]$ComposeFileName)

    $composePath = Join-Path $script:DockerDir $ComposeFileName
    $script:PullAlwaysServices = @()
    $script:PullMissingServices = @()

    $imagesResult = Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('compose', '-f', $composePath, '--env-file', $script:EnvFile, 'config', '--images') -IgnoreErrors
    if ($imagesResult.ExitCode -ne 0) {
        Write-WarnLog "Could not resolve image list; will let Compose pull what's missing."
        $script:UpdateDecision = 'skip'
        return
    }

    Write-Log 'Checking for image updates (this reads the registry, downloads nothing)...'
    $missing = 0
    $stale = 0
    $missingImages = New-Object System.Collections.Generic.List[string]
    $staleImages = New-Object System.Collections.Generic.List[string]

    foreach ($imageLine in $imagesResult.Output) {
        $image = ([string]$imageLine).Trim()
        if ([string]::IsNullOrWhiteSpace($image)) { continue }

        $localDigest = Get-LocalDigest -ImageRef $image
        if ([string]::IsNullOrWhiteSpace($localDigest)) {
            $missing++
            $missingImages.Add($image) | Out-Null
            continue
        }

        $remoteDigest = Get-RemoteDigest -ImageRef $image
        if (-not [string]::IsNullOrWhiteSpace($remoteDigest) -and $remoteDigest -ne $localDigest) {
            $stale++
            $staleImages.Add($image) | Out-Null
        }
    }

    if ($missing -gt 0) {
        $unavailableImages = New-Object System.Collections.Generic.List[string]
        foreach ($missingImage in $missingImages) {
            $remoteDigest = Get-RemoteDigest -ImageRef $missingImage
            if ([string]::IsNullOrWhiteSpace($remoteDigest)) {
                $unavailableImages.Add($missingImage) | Out-Null
            }
        }

        if ($unavailableImages.Count -gt 0) {
            $imageList = ($unavailableImages | ForEach-Object { "  - $_" }) -join "`n"
            if ($script:SelectedBackend -eq 'vulkan' -and ($unavailableImages | Where-Object { $_ -match 'guideants-ai-vulkan' })) {
                Stop-WithError "The GHCR Vulkan AI image is not currently pullable:`n$imageList`nBuild it locally, then rerun with local compose:`n  powershell -ExecutionPolicy Bypass -File .\docker\build\build_guideants_ai.ps1 -Backend vulkan`n  powershell -ExecutionPolicy Bypass -File .\installer\guideants.ps1 --backend vulkan --compose local --reconfigure`nOr choose a published backend such as cuda13, cpu, or slim."
            }

            Stop-WithError "One or more Compose images are not pullable from the registry:`n$imageList`nIf these are private images, run 'docker login' for the registry or switch to --compose local after building them locally."
        }

        Write-Log "$missing image(s) not present locally - they will be downloaded on first start."
    }

    if ($stale -gt 0) {
        Write-HRule
        Write-Log "Updates available for $stale image(s) ($($script:SelectedBackend))."
        if (Ask-YesNo -Prompt 'Update now before starting? [Y/n]' -Default 'Y') {
            $script:PullAlwaysServices = @(Get-ServicesForImages -ComposePath $composePath -Images @($staleImages))
        }
        else {
            Write-Log 'Keeping current images.'
        }
    }

    if ($missing -gt 0) {
        $script:PullMissingServices = @(Get-ServicesForImages -ComposePath $composePath -Images @($missingImages))
    }

    if ($script:PullAlwaysServices.Count -gt 0 -or $script:PullMissingServices.Count -gt 0) {
        $script:UpdateDecision = 'pull'
    }
    elseif ($stale -eq 0 -and $missing -eq 0) {
        Write-Log 'All images are up to date.'
        $script:UpdateDecision = 'skip'
    }
    else {
        $script:UpdateDecision = 'skip'
    }
}

function Detect-PriorInstall {
    $result = Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('volume', 'ls', '--filter', 'name=guideants', '--format', '{{.Name}}') -IgnoreErrors
    if ($result.ExitCode -eq 0) {
        $existing = $result.Output | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -First 1
        if (-not [string]::IsNullOrWhiteSpace([string]$existing)) {
            Write-Log 'Existing GuideAnts data detected (named volumes). Your projects, DB, and models will be reused.'
        }
    }
}

function ConvertFrom-JsonChecked {
    param(
        [Parameter(Mandatory = $true)][string]$Json,
        [Parameter(Mandatory = $true)][string]$Context
    )

    try {
        return $Json | ConvertFrom-Json
    }
    catch {
        Stop-WithError "Malformed $Context response from server. Try again or check the stack logs."
    }
}

function Get-ApiErrorMessage {
    param([string]$Body)

    if ([string]::IsNullOrWhiteSpace($Body)) { return '' }
    try {
        $parsed = $Body | ConvertFrom-Json
        foreach ($field in @('message', 'detail', 'title', 'error')) {
            if ($parsed.PSObject.Properties.Name -contains $field -and -not [string]::IsNullOrWhiteSpace([string]$parsed.$field)) {
                return [string]$parsed.$field
            }
        }
    }
    catch {
    }

    return $Body
}

function Acquire-Token {
    $staleTokenPath = Join-Path $script:DockerDir 'volumes/content-files/.cli-auth-token'
    Remove-Item -LiteralPath $staleTokenPath -Force -ErrorAction SilentlyContinue

    $response = $null
    for ($attempt = 1; $attempt -le 15; $attempt++) {
        $response = Invoke-Http -Method 'POST' -Uri "$($script:ApiBase)/api/cli/sessions" -TimeoutSeconds 5
        if ($response.StatusCode -eq 200) { break }
        if ($attempt -eq 15) {
            Stop-WithError "Could not create CLI session (HTTP $($response.StatusCode)). Ensure the stack is running and you are logged in at $($script:HealthUrl)."
        }
        Start-Sleep -Seconds 2
    }

    $session = ConvertFrom-JsonChecked -Json $response.Body -Context 'session'
    $sessionId = [string]$session.sessionId
    $deviceSecret = [string]$session.deviceSecret
    if ([string]::IsNullOrWhiteSpace($sessionId) -or [string]::IsNullOrWhiteSpace($deviceSecret)) {
        Stop-WithError 'Malformed session response from server. Try again or check the stack logs.'
    }

    $approveUrl = "$($script:ApiBase)/cli/authorize?session=$sessionId"
    Open-Browser -Url $approveUrl
    Write-Log 'Authorize this request in your browser:'
    Write-Log "  $approveUrl"
    Write-Log 'Approve the command-line mount request in your browser, then return here...'

    for ($attempt = 1; $attempt -le 150; $attempt++) {
        Start-Sleep -Seconds 2
        $poll = Invoke-Http -Method 'GET' -Uri "$($script:ApiBase)/api/cli/sessions/$sessionId/token" -Headers @{ 'X-Device-Secret' = $deviceSecret } -TimeoutSeconds 5
        switch ($poll.StatusCode) {
            200 {
                $tokenResponse = ConvertFrom-JsonChecked -Json $poll.Body -Context 'token'
                $script:AuthToken = [string]$tokenResponse.token
                if ([string]::IsNullOrWhiteSpace($script:AuthToken)) {
                    Stop-WithError 'Server returned 200 but the token was empty.'
                }
                Write-Log 'Authorized.'
                return
            }
            202 {
                if ($attempt % 5 -eq 0) { [Console]::Error.Write('.') }
            }
            403 { Stop-WithError 'Authorization request was denied in the browser. Rerun with --mount to try again.' }
            410 { Stop-WithError 'Authorization request expired or was already used. Rerun with --mount and approve promptly.' }
            404 { Stop-WithError 'Authorization session not found. Rerun and try again.' }
            401 { Stop-WithError 'Authorization failed (device secret rejected).' }
            default { }
        }
    }

    Stop-WithError 'Timed out waiting for browser approval. Rerun with --mount and approve in the browser.'
}

function Invoke-ApiWithToken {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Body = $null,
        [string]$ContentType = 'application/json'
    )

    return Invoke-Http -Method $Method -Uri "$($script:ApiBase)$Path" -Headers @{ Authorization = "Bearer $($script:AuthToken)" } -Body $Body -ContentType $ContentType -TimeoutSeconds 30
}

function Select-ItemId {
    param(
        [Parameter(Mandatory = $true)][object[]]$Items,
        [Parameter(Mandatory = $true)][string]$IdProperty,
        [Parameter(Mandatory = $true)][string]$TitleProperty,
        [Parameter(Mandatory = $true)][string]$Prompt,
        [string]$EmptyError = 'No items found.',
        [string]$OnlyItemLogPrefix = 'Auto-selecting only item'
    )

    if ($Items.Count -eq 0) { Stop-WithError $EmptyError }

    Write-Host ''
    Write-Host $Prompt
    for ($i = 0; $i -lt $Items.Count; $i++) {
        $title = [string]$Items[$i].$TitleProperty
        Write-Host ('    {0}) {1}' -f ($i + 1), $title)
    }
    Write-Host ''

    if ($Items.Count -eq 1) {
        $title = [string]$Items[0].$TitleProperty
        if ($script:AssumeYes) {
            Write-Log "$OnlyItemLogPrefix`: $title"
        }
        else {
            $null = Read-Host "Enter 1 or press Enter for [$title]"
        }
        return [string]$Items[0].$IdProperty
    }

    $choice = Read-Host "Enter 1-$($Items.Count)"
    if ($choice -match '^[0-9]+$' -and [int]$choice -ge 1 -and [int]$choice -le $Items.Count) {
        return [string]$Items[[int]$choice - 1].$IdProperty
    }

    Stop-WithError 'Invalid selection.'
}

function Apply-HostMount {
    if ([string]::IsNullOrWhiteSpace($script:MountPath)) { return }

    if (-not [System.IO.Path]::IsPathRooted($script:MountPath)) {
        $resolvedPath = Resolve-Path -LiteralPath $script:MountPath -ErrorAction SilentlyContinue
        if ($null -eq $resolvedPath) {
            Stop-WithError "Directory not found: $($script:MountPath)"
        }
        $script:MountPath = $resolvedPath.Path
    }

    if (-not (Test-Path -LiteralPath $script:MountPath -PathType Container)) {
        Stop-WithError "Mount path does not exist or is not a directory: $($script:MountPath)"
    }

    Acquire-Token

    Write-HRule
    Write-Log 'Fetching projects...'
    $projectsResponse = Invoke-ApiWithToken -Method 'GET' -Path '/api/projects'
    if ($projectsResponse.StatusCode -ne 200) {
        Stop-WithError "Failed to fetch projects (HTTP $($projectsResponse.StatusCode))."
    }
    $projects = @(ConvertFrom-JsonChecked -Json $projectsResponse.Body -Context 'projects')
    if ($projects.Count -eq 0) {
        Stop-WithError 'No projects found. Create a project in GuideAnts first.'
    }

    $selectedProjectId = Select-ItemId `
        -Items $projects `
        -IdProperty 'id' `
        -TitleProperty 'title' `
        -Prompt "  Select a project to mount `"$($script:MountPath)`" into:" `
        -EmptyError 'No projects found. Create a project in GuideAnts first.' `
        -OnlyItemLogPrefix 'Auto-selecting only project'

    Write-HRule
    Write-Log 'Fetching notebooks...'
    $notebooksResponse = Invoke-ApiWithToken -Method 'GET' -Path "/api/projects/$selectedProjectId/notebooks"
    if ($notebooksResponse.StatusCode -ne 200) {
        Stop-WithError "Failed to fetch notebooks (HTTP $($notebooksResponse.StatusCode))."
    }
    $notebooks = @(ConvertFrom-JsonChecked -Json $notebooksResponse.Body -Context 'notebooks')

    $selectedScope = 'Project'
    $selectedNotebookId = ''
    if ($notebooks.Count -eq 0) {
        Write-Log 'No notebooks found in this project. Mounting at the project root (applies to every notebook).'
    }
    else {
        Write-Host ''
        Write-Host "  Select a notebook to mount `"$($script:MountPath)`" into:"
        for ($i = 0; $i -lt $notebooks.Count; $i++) {
            Write-Host ('    {0}) {1}' -f ($i + 1), [string]$notebooks[$i].title)
        }
        $allChoice = $notebooks.Count + 1
        Write-Host ('    {0}) Entire project (project root + every notebook)' -f $allChoice)
        Write-Host ''

        if ($script:AssumeYes) {
            Write-Log '--yes: mounting at the project root (applies to every notebook).'
        }
        else {
            $notebookChoice = Read-Host "Enter 1-$allChoice"
            if ($notebookChoice -match '^[0-9]+$' -and [int]$notebookChoice -ge 1 -and [int]$notebookChoice -lt $allChoice) {
                $selectedScope = 'Notebook'
                $selectedNotebookId = [string]$notebooks[[int]$notebookChoice - 1].id
                Write-Log "Mounting to notebook: $([string]$notebooks[[int]$notebookChoice - 1].title)"
            }
            elseif ($notebookChoice -match '^[0-9]+$' -and [int]$notebookChoice -eq $allChoice) {
                Write-Log 'Mounting at the project root (applies to every notebook).'
            }
            else {
                Stop-WithError 'Invalid selection.'
            }
        }
    }

    Write-Log 'Creating host mount...'
    if ($selectedScope -eq 'Notebook') {
        $payload = [ordered]@{
            scope = 'Notebook'
            notebookId = $selectedNotebookId
            hostPath = $script:MountPath
        }
    }
    else {
        $payload = [ordered]@{
            scope = 'Project'
            hostPath = $script:MountPath
        }
    }

    $createBody = $payload | ConvertTo-Json -Depth 5 -Compress
    $createResponse = Invoke-ApiWithToken -Method 'POST' -Path "/api/projects/$selectedProjectId/host-folder-mounts" -Body $createBody
    if ($createResponse.StatusCode -ne 201) {
        $err = Get-ApiErrorMessage -Body $createResponse.Body
        Stop-WithError "Failed to create mount (HTTP $($createResponse.StatusCode)): $err"
    }

    $createResult = ConvertFrom-JsonChecked -Json $createResponse.Body -Context 'mount create'
    $mountId = [string]$createResult.mountId
    $applyCommand = [string]$createResult.command
    Write-Log "Mount created (id: $mountId). Applying..."

    $mountScriptPs1 = Join-Path $script:RootDir 'scripts/guideants-host-mount.ps1'
    $mountScriptSh = Join-Path $script:RootDir 'scripts/guideants-host-mount.sh'
    if (Test-Path -LiteralPath $mountScriptPs1) {
        Invoke-HostMountHelperBestEffort -ScriptPath $mountScriptPs1 -Verb 'apply' -MountId $mountId -HostPath $script:MountPath -ProjectId $selectedProjectId
    }
    elseif ((Test-Path -LiteralPath $mountScriptSh) -and (Test-Command 'bash')) {
        $helperResult = Invoke-ExternalCapture -FilePath 'bash' -ArgumentList @($mountScriptSh, 'apply', '--mount-id', $mountId, '--host-path', $script:MountPath, '--project-id', $selectedProjectId) -IgnoreErrors
        if ($helperResult.ExitCode -ne 0) {
            Write-WarnLog "Host mount helper exited with code $($helperResult.ExitCode)."
        }
    }
    else {
        Write-WarnLog "Host mount script not found. Run manually: $applyCommand"
    }

    Fix-CrlfContainers
    Write-Log 'Host folder mounted successfully.'
}

function Get-PowerShellExecutable {
    try {
        $proc = Get-Process -Id $PID -ErrorAction Stop
        if (-not [string]::IsNullOrWhiteSpace($proc.Path)) { return $proc.Path }
    }
    catch {
    }

    if (Test-Command 'pwsh') { return 'pwsh' }
    return 'powershell.exe'
}

function Invoke-HostMountHelper {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$Verb,
        [Parameter(Mandatory = $true)][string]$MountId,
        [string]$HostPath = '',
        [string]$ProjectId = ''
    )

    $psExe = Get-PowerShellExecutable
    $arguments = @('-NoProfile')
    if ($script:OsName -eq 'windows') {
        $arguments += @('-ExecutionPolicy', 'Bypass')
    }
    $arguments += @('-File', $ScriptPath, $Verb, '-MountId', $MountId)
    if (-not [string]::IsNullOrWhiteSpace($HostPath)) { $arguments += @('-HostPath', $HostPath) }
    if (-not [string]::IsNullOrWhiteSpace($ProjectId)) { $arguments += @('-ProjectId', $ProjectId) }

    $oldToken = $env:GUIDEANTS_MOUNT_API_TOKEN
    $oldBase = $env:GUIDEANTS_API_BASE
    try {
        $env:GUIDEANTS_MOUNT_API_TOKEN = $script:AuthToken
        $env:GUIDEANTS_API_BASE = $script:ApiBase
        Invoke-External -FilePath $psExe -ArgumentList $arguments
    }
    finally {
        if ($null -eq $oldToken) { Remove-Item Env:\GUIDEANTS_MOUNT_API_TOKEN -ErrorAction SilentlyContinue } else { $env:GUIDEANTS_MOUNT_API_TOKEN = $oldToken }
        if ($null -eq $oldBase) { Remove-Item Env:\GUIDEANTS_API_BASE -ErrorAction SilentlyContinue } else { $env:GUIDEANTS_API_BASE = $oldBase }
    }
}

function Invoke-HostMountHelperBestEffort {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$Verb,
        [Parameter(Mandatory = $true)][string]$MountId,
        [string]$HostPath = '',
        [string]$ProjectId = ''
    )

    try {
        Invoke-HostMountHelper -ScriptPath $ScriptPath -Verb $Verb -MountId $MountId -HostPath $HostPath -ProjectId $ProjectId
    }
    catch {
        Write-WarnLog "Host mount helper failed: $($_.Exception.Message)"
    }
}

function Get-ActiveMounts {
    param([Parameter(Mandatory = $true)][string]$Json)

    $items = @(ConvertFrom-JsonChecked -Json $Json -Context 'mounts')
    return @($items | Where-Object { [string]$_.status -eq 'Active' })
}

function Remove-HostMount {
    if (-not $script:Unmount) { return }

    Acquire-Token

    Write-HRule
    Write-Log 'Fetching projects...'
    $projectsResponse = Invoke-ApiWithToken -Method 'GET' -Path '/api/projects'
    if ($projectsResponse.StatusCode -ne 200) {
        Stop-WithError "Failed to fetch projects (HTTP $($projectsResponse.StatusCode))."
    }

    $projects = @(ConvertFrom-JsonChecked -Json $projectsResponse.Body -Context 'projects')
    if ($projects.Count -eq 0) { Stop-WithError 'No projects found.' }

    $selectedProjectId = Select-ItemId `
        -Items $projects `
        -IdProperty 'id' `
        -TitleProperty 'title' `
        -Prompt '  Select a project to unmount from:' `
        -EmptyError 'No projects found.' `
        -OnlyItemLogPrefix 'Auto-selecting only project'

    Write-HRule
    Write-Log 'Fetching mounts...'
    $mountsResponse = Invoke-ApiWithToken -Method 'GET' -Path "/api/projects/$selectedProjectId/host-folder-mounts"
    if ($mountsResponse.StatusCode -ne 200) {
        Stop-WithError "Failed to fetch mounts (HTTP $($mountsResponse.StatusCode))."
    }

    $mounts = @(Get-ActiveMounts -Json $mountsResponse.Body)
    if ($mounts.Count -eq 0) {
        Write-Log 'No active mounts found for this project.'
        return
    }

    Write-Host ''
    Write-Host '  Select a mount to remove:'
    for ($i = 0; $i -lt $mounts.Count; $i++) {
        Write-Host ('    {0}) {1}  [scope: {2}]' -f ($i + 1), [string]$mounts[$i].displayName, [string]$mounts[$i].scope)
    }
    Write-Host ''

    if ($mounts.Count -eq 1) {
        if ($script:AssumeYes) {
            Write-Log "Auto-selecting only mount: $([string]$mounts[0].displayName)"
        }
        else {
            $null = Read-Host "Enter 1 or press Enter for [$([string]$mounts[0].displayName)]"
        }
        $selectedMountId = [string]$mounts[0].mountId
    }
    else {
        $choice = Read-Host "Enter 1-$($mounts.Count)"
        if ($choice -match '^[0-9]+$' -and [int]$choice -ge 1 -and [int]$choice -le $mounts.Count) {
            $selectedMountId = [string]$mounts[[int]$choice - 1].mountId
        }
        else {
            Stop-WithError 'Invalid selection.'
        }
    }

    Write-Log 'Removing mount...'
    $removeResponse = Invoke-ApiWithToken -Method 'POST' -Path "/api/projects/$selectedProjectId/host-folder-mounts/$selectedMountId/commands/remove"
    if ($removeResponse.StatusCode -ne 200) {
        $err = Get-ApiErrorMessage -Body $removeResponse.Body
        Stop-WithError "Failed to remove mount (HTTP $($removeResponse.StatusCode)): $err"
    }

    $mountScriptPs1 = Join-Path $script:RootDir 'scripts/guideants-host-mount.ps1'
    $mountScriptSh = Join-Path $script:RootDir 'scripts/guideants-host-mount.sh'
    if (Test-Path -LiteralPath $mountScriptPs1) {
        Invoke-HostMountHelperBestEffort -ScriptPath $mountScriptPs1 -Verb 'remove' -MountId $selectedMountId -ProjectId $selectedProjectId
    }
    elseif ((Test-Path -LiteralPath $mountScriptSh) -and (Test-Command 'bash')) {
        $helperResult = Invoke-ExternalCapture -FilePath 'bash' -ArgumentList @($mountScriptSh, 'remove', '--mount-id', $selectedMountId, '--project-id', $selectedProjectId) -IgnoreErrors
        if ($helperResult.ExitCode -ne 0) {
            Write-WarnLog "Host mount helper exited with code $($helperResult.ExitCode)."
        }
    }
    else {
        Write-WarnLog "Host mount script not found. Run manually: guideants-host-mount.sh remove --mount-id $selectedMountId"
    }

    Fix-CrlfContainers
    Write-Log 'Host folder mount removed successfully.'
}

function Wait-ForHealth {
    Write-Log "Waiting for GuideAnts at $($script:HealthUrl) ..."
    for ($i = 1; $i -le 45; $i++) {
        $response = Invoke-Http -Method 'GET' -Uri $script:HealthUrl -TimeoutSeconds 5
        if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 400) {
            return $true
        }
        Start-Sleep -Seconds 2
    }

    return $false
}

function Open-Browser {
    param([string]$Url = $script:HealthUrl)

    try {
        switch ($script:OsName) {
            'macos' {
                if (Test-Command 'open') { Invoke-ExternalCapture -FilePath 'open' -ArgumentList @($Url) -IgnoreErrors | Out-Null }
            }
            'windows' {
                if ($script:IsWsl) {
                    if (Test-Command 'wslview') {
                        Invoke-ExternalCapture -FilePath 'wslview' -ArgumentList @($Url) -IgnoreErrors | Out-Null
                    }
                    elseif (Test-Command 'cmd.exe') {
                        Invoke-ExternalCapture -FilePath 'cmd.exe' -ArgumentList @('/c', 'start', '', $Url) -IgnoreErrors | Out-Null
                    }
                }
                else {
                    Start-Process $Url | Out-Null
                }
            }
            default {
                if (Test-Command 'xdg-open') { Invoke-ExternalCapture -FilePath 'xdg-open' -ArgumentList @($Url) -IgnoreErrors | Out-Null }
            }
        }
    }
    catch {
    }
}

function Save-State {
    Save-InstallerState `
        -StateFile $script:StateFile `
        -DbLayout $script:SelectedDbLayout `
        -AiBackend $script:SelectedAiBackend `
        -Components $script:SelectedComponents `
        -ComposeFiles $script:SelectedComposeFragments `
        -ComposeMode $script:ComposeMode `
        -StartCommand 'guideants.ps1'
}

function Ensure-ShellScriptsUseLf {
    $buildDir = Join-Path $script:DockerDir 'build'
    if (-not (Test-Path -LiteralPath $buildDir)) { return }

    foreach ($file in Get-ChildItem -LiteralPath $buildDir -Recurse -Filter '*.sh' -File -ErrorAction SilentlyContinue) {
        try {
            $text = [System.IO.File]::ReadAllText($file.FullName)
            if ($text.Contains("`r")) {
                $text = $text -replace "`r`n", "`n"
                $text = $text -replace "`r", ''
                [System.IO.File]::WriteAllText($file.FullName, $text, [System.Text.UTF8Encoding]::new($false))
            }
        }
        catch {
        }
    }
}

function Fix-CrlfContainers {
    Start-Sleep -Seconds 3
    $containersResult = Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('ps', '-a', '--filter', 'label=com.docker.compose.project', '--format', '{{.Names}}') -IgnoreErrors
    if ($containersResult.ExitCode -ne 0) { return }

    foreach ($containerLine in $containersResult.Output) {
        $container = ([string]$containerLine).Trim()
        if ([string]::IsNullOrWhiteSpace($container)) { continue }

        $exitCodeResult = Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('inspect', '--format', '{{.State.ExitCode}}', $container) -IgnoreErrors
        $exitCode = ([string]($exitCodeResult.Output | Select-Object -First 1)).Trim()
        if ($exitCode -ne '127') { continue }

        $entrypointResult = Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('inspect', '--format', '{{join .Config.Entrypoint " "}}', $container) -IgnoreErrors
        $entrypoint = ([string]($entrypointResult.Output | Select-Object -First 1)).Trim()
        if ([string]::IsNullOrWhiteSpace($entrypoint)) { continue }

        $parts = @($entrypoint -split '\s+' | Where-Object { $_ -ne '' })
        if ($parts.Count -eq 0) { continue }
        $srcScript = $parts[$parts.Count - 1]
        if ($srcScript -notlike '*.sh') { continue }

        $tmp = [System.IO.Path]::GetTempFileName()
        try {
            $copyOut = Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('cp', "$container`:$srcScript", $tmp) -IgnoreErrors
            if ($copyOut.ExitCode -ne 0) { continue }

            $text = [System.IO.File]::ReadAllText($tmp)
            $text = $text -replace "`r`n", "`n"
            $text = $text -replace "`r", ''
            [System.IO.File]::WriteAllText($tmp, $text, [System.Text.UTF8Encoding]::new($false))

            Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('cp', $tmp, "$container`:$srcScript") -IgnoreErrors | Out-Null
            Invoke-ExternalCapture -FilePath 'docker' -ArgumentList @('restart', $container) -IgnoreErrors | Out-Null
        }
        finally {
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        }
    }
}

function Start-GuideAntsStack {
    if ($script:OsName -eq 'macos' -and $script:Arch -eq 'arm64' -and $script:ComposeMode -ne 'local') {
        $env:DOCKER_DEFAULT_PLATFORM = 'linux/amd64'
    }

    Ensure-ShellScriptsUseLf
    Set-InstallerLocalImageEnv -ComposeMode $script:ComposeMode
    $script:ComposeArgs = @(Build-ComposeArgs)

    Push-Location $script:DockerDir
    try {
        Invoke-InstallerProgressivePull -ComposeArgs $script:ComposeArgs -EnvFile $script:EnvFile -AssumeYes:$script:AssumeYes

        Write-Log 'Starting the stack...'
        Invoke-External -FilePath 'docker' -ArgumentList (@('compose') + $script:ComposeArgs + @('--env-file', $script:EnvFile, 'up', '-d'))

        $active = @(Get-InstallerActiveServices -DbLayout $script:SelectedDbLayout -AiBackend $script:SelectedAiBackend -Components $script:SelectedComponents)
        Invoke-InstallerPruneDeselected -ComposeArgs $script:ComposeArgs -EnvFile $script:EnvFile -KeepServices $active
    }
    finally {
        Pop-Location
    }
}

function Invoke-Main {
    Parse-Arguments @args
    Initialize-Platform

    Write-HRule
    Write-Log 'GuideAnts portable launcher'
    $wslNote = if ($script:IsWsl) { ' (WSL)' } else { '' }
    Write-Log "OS: $($script:OsName)$wslNote   Arch: $($script:Arch)"
    if ($script:OsName -eq 'macos' -and $script:Arch -eq 'arm64') {
        Write-WarnLog "Apple Silicon: images run as linux/amd64 under emulation; 'slim' is recommended here."
    }
    Write-HRule

    Check-Docker
    if ($script:InstallRocmWsl) {
        Invoke-InstallRocmWsl
    }
    Report-Resources

    $aiOverride = if ($script:BackendOverride -ne '') { $script:BackendOverride } else { '' }
    Invoke-InstallerWizard `
        -StateFile $script:StateFile `
        -Reconfigure:$script:Reconfigure `
        -AiBackendOverride $aiOverride `
        -AssumeYes:$script:AssumeYes

    $script:SelectedBackend = $script:SelectedAiBackend
    Write-Log "DB layout: $($script:SelectedDbLayout)   AI: $($script:SelectedAiBackend)   Optionals: $($script:SelectedComponents -join ', ')"
    Write-Log "Compose fragments: $($script:SelectedComposeFragments -join ', ')"

    Select-VulkanRuntime
    Select-RocmRuntime
    Check-GpuDrivers -Backend $script:SelectedAiBackend

    Detect-PriorInstall

    if ($script:Mode -eq 'doctor') {
        Write-HRule
        Write-Log 'Doctor mode complete. No changes were made.'
        $wouldArgs = @(Build-ComposeArgs)
        $wouldStart = 'docker compose ' + (($wouldArgs | ForEach-Object { if ($_ -like '*\*' -or $_ -like '*/*') { "-f `"$_`"" } else { $_ } }) -join ' ')
        Write-Log "Would start: $wouldStart --env-file docker/.env up -d"
        return
    }

    Start-GuideAntsStack
    Fix-CrlfContainers

    if (Wait-ForHealth) {
        Write-HRule
        Write-Log "GuideAnts is up: $($script:HealthUrl)"
        Apply-HostMount
        Remove-HostMount
        Open-Browser
    }
    else {
        Write-WarnLog "Health check timed out. Inspect with: docker compose $($script:ComposeArgs -join ' ') ps"
        if (-not [string]::IsNullOrWhiteSpace($script:MountPath) -or $script:Unmount) {
            Write-WarnLog 'Skipping mount/unmount operations because the health check failed.'
        }
    }

    Save-State
}

try {
    Invoke-Main @args
}
catch {
    [Console]::Error.WriteLine("[guideants][error] $($_.Exception.Message)")
    exit 1
}
