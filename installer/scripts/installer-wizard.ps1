# Shared installer wizard: component metadata, state, compose assembly, progressive pull.
# Dot-source from guideants.ps1 and stop_guideants.ps1.

$script:InstallerComposeDir = 'compose'
$script:InstallerOptionalComponents = @('docling', 'documentserver', 'plantuml', 'searxng')
$script:InstallerAiBackends = @('none', 'slim', 'cpu', 'cuda13', 'rocm', 'vulkan')
$script:InstallerLocalAiBackends = @('cpu', 'cuda13', 'rocm', 'vulkan')

function Get-InstallerComponentCatalog {
    return [ordered]@{
        db_bundled = @{
            Label = 'Bundled (webapi-ui-mssql)'
            SizeGb = 7.3
            Summary = 'UI + database in one image.'
        }
        db_separate = @{
            Label = 'Separate (webapi-ui-slim + mssql-express)'
            SizeGb = 7.6
            Summary = 'UI and SQL Server in separate containers.'
        }
        ai_none = @{
            Label = 'No AI container'
            SizeGb = 0
            Summary = 'Cloud chat only. Without AI: sandbox, scripted skills, sandboxed MCP servers, and local model services will not work.'
        }
        ai_slim = @{
            Label = 'AI slim (sandbox, no local model runtime)'
            SizeGb = 4.3
            Summary = 'Sandbox for all providers, skills with script deps, and local sandboxed MCP servers.'
        }
        ai_cpu = @{
            Label = 'AI CPU (local models, no GPU)'
            SizeGb = 8.2
            Summary = 'Sandbox plus local CPU model runtime. Image size does not include model weights.'
        }
        ai_cuda13 = @{
            Label = 'AI CUDA 13 (NVIDIA GPU)'
            SizeGb = 14
            Summary = 'Sandbox plus NVIDIA CUDA local runtime. Image size does not include model weights.'
        }
        ai_rocm = @{
            Label = 'AI ROCm (AMD GPU)'
            SizeGb = 20
            Summary = 'Sandbox plus AMD ROCm local runtime. Image size does not include model weights.'
        }
        ai_vulkan = @{
            Label = 'AI Vulkan (broad GPU path)'
            SizeGb = 8.5
            Summary = 'Sandbox plus Vulkan local runtime. Image size does not include model weights.'
        }
        docling = @{
            Label = 'DocLing (local document intelligence)'
            SizeGb = 7.1
            Summary = 'Local doc conversion. Fungible with Azure Document Intelligence in Settings.'
            Missing = 'Without DocLing and without Azure DI: document intelligence features will not work.'
        }
        documentserver = @{
            Label = 'DocumentServer (Office editing)'
            SizeGb = 7.2
            Summary = 'In-app Office document editing.'
            Missing = 'Without it: DocumentServer open/edit will not work.'
        }
        plantuml = @{
            Label = 'PlantUML (diagram rendering)'
            SizeGb = 0.7
            Summary = 'PlantUML diagrams; host-mount target when enabled.'
            Missing = 'Without it: PlantUML generation/rendering will not work.'
        }
        searxng = @{
            Label = 'SearXNG (web search + browser render)'
            SizeGb = 4.2
            Summary = 'In-product web search and browser rendering.'
            Missing = 'Without it: web search / browser-render features will not work.'
        }
    }
}

function Get-InstallerStateMap {
    param([Parameter(Mandatory = $true)][string]$StateFile)

    $map = @{}
    if (-not (Test-Path -LiteralPath $StateFile)) { return $map }
    foreach ($line in Get-Content -LiteralPath $StateFile) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#')) { continue }
        $sep = $trimmed.IndexOf('=')
        if ($sep -lt 1) { continue }
        $map[$trimmed.Substring(0, $sep).Trim()] = $trimmed.Substring($sep + 1).Trim()
    }
    return $map
}

function Get-InstallerStateValue {
    param(
        [Parameter(Mandatory = $true)][string]$StateFile,
        [Parameter(Mandatory = $true)][string]$Key
    )
    $map = Get-InstallerStateMap -StateFile $StateFile
    if ($map.ContainsKey($Key)) { return $map[$Key] }
    return $null
}

function ConvertFrom-InstallerLegacyState {
    param([hashtable]$State)

    if (-not $State.ContainsKey('DB_LAYOUT') -or [string]::IsNullOrWhiteSpace($State['DB_LAYOUT'])) {
        $composeFile = if ($State.ContainsKey('COMPOSE_FILE')) { $State['COMPOSE_FILE'] } else { '' }
        if ($composeFile -match 'ghcr-slim|docker-compose\.slim') {
            $State['DB_LAYOUT'] = 'bundled'
        }
        else {
            $State['DB_LAYOUT'] = 'separate'
        }
    }

    if (-not $State.ContainsKey('AI_BACKEND') -or [string]::IsNullOrWhiteSpace($State['AI_BACKEND'])) {
        $backend = if ($State.ContainsKey('BACKEND')) { $State['BACKEND'] } else { 'slim' }
        if ($backend -match '^(none|slim|cpu|cuda13|rocm|vulkan)$') {
            $State['AI_BACKEND'] = $backend
        }
        else {
            $State['AI_BACKEND'] = 'slim'
        }
    }

    if (-not $State.ContainsKey('COMPONENTS') -or [string]::IsNullOrWhiteSpace($State['COMPONENTS'])) {
        $State['COMPONENTS'] = 'docling,documentserver,plantuml,searxng'
    }

    if (-not $State.ContainsKey('COMPOSE_MODE') -or [string]::IsNullOrWhiteSpace($State['COMPOSE_MODE'])) {
        $State['COMPOSE_MODE'] = 'ghcr'
    }

    return $State
}

function Get-InstallerSelectionFromState {
    param([Parameter(Mandatory = $true)][string]$StateFile)

    $state = ConvertFrom-InstallerLegacyState -State (Get-InstallerStateMap -StateFile $StateFile)
    $components = @()
    if ($state.ContainsKey('COMPONENTS') -and -not [string]::IsNullOrWhiteSpace($state['COMPONENTS'])) {
        $components = @($state['COMPONENTS'] -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
    }

    return [pscustomobject]@{
        DbLayout = [string]$state['DB_LAYOUT']
        AiBackend = [string]$state['AI_BACKEND']
        Components = $components
        ComposeMode = [string]$state['COMPOSE_MODE']
        ComposeFiles = if ($state.ContainsKey('COMPOSE_FILES')) { @($state['COMPOSE_FILES'] -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }) } else { @() }
    }
}

function Get-InstallerDoclingFragment {
    param([Parameter(Mandatory = $true)][string]$AiBackend)
    if ($AiBackend -eq 'cuda13') { return 'docling-cuda.yml' }
    return 'docling-cpu.yml'
}

function Get-InstallerComposeFragments {
    param(
        [Parameter(Mandatory = $true)][string]$DbLayout,
        [Parameter(Mandatory = $true)][string]$AiBackend,
        [string[]]$Components = @()
    )

    $files = New-Object System.Collections.Generic.List[string]
    $files.Add('base.yml') | Out-Null
    if ($DbLayout -eq 'separate') {
        $files.Add('core-separate.yml') | Out-Null
    }
    else {
        $files.Add('core-bundled.yml') | Out-Null
    }

    if ($AiBackend -ne 'none') {
        $files.Add("ai-$AiBackend.yml") | Out-Null
    }

    foreach ($component in $script:InstallerOptionalComponents) {
        if ($Components -contains $component) {
            if ($component -eq 'docling') {
                $files.Add((Get-InstallerDoclingFragment -AiBackend $AiBackend)) | Out-Null
            }
            else {
                $files.Add("$component.yml") | Out-Null
            }
        }
    }

    return @($files)
}

function Get-InstallerEstimatedSizeGb {
    param(
        [Parameter(Mandatory = $true)][string]$DbLayout,
        [Parameter(Mandatory = $true)][string]$AiBackend,
        [string[]]$Components = @()
    )

    $catalog = Get-InstallerComponentCatalog
    $total = 0.0
    if ($DbLayout -eq 'separate') { $total += [double]$catalog['db_separate'].SizeGb }
    else { $total += [double]$catalog['db_bundled'].SizeGb }

    if ($AiBackend -ne 'none') {
        $total += [double]$catalog["ai_$AiBackend"].SizeGb
    }

    foreach ($component in $Components) {
        if ($catalog.Contains($component)) {
            $size = [double]$catalog[$component].SizeGb
            if ($component -eq 'docling' -and $AiBackend -eq 'cuda13') { $size = 13.8 }
            $total += $size
        }
    }

    return [math]::Round($total, 1)
}

function Set-InstallerLocalImageEnv {
    param([Parameter(Mandatory = $true)][string]$ComposeMode)

    if ($ComposeMode -ne 'local') { return }

    $pairs = @{
        GA_WEBAPI_UI_MSSQL_GHCR_IMAGE = 'GA_WEBAPI_UI_MSSQL_IMAGE'
        GA_WEBAPI_UI_SLIM_GHCR_IMAGE = 'GA_WEBAPI_UI_SLIM_IMAGE'
        GA_MSSQL_IMAGE = 'GA_MSSQL_IMAGE'
        GA_AI_SLIM_GHCR_IMAGE = 'GA_AI_SLIM_IMAGE'
        GA_AI_GHCR_IMAGE = 'GA_AI_CUDA_IMAGE'
        GA_PLANTUML_GHCR_IMAGE = 'GA_PLANTUML_IMAGE'
        GA_SEARXNG_GHCR_IMAGE = 'GA_SEARXNG_IMAGE'
    }

    foreach ($ghcrVar in $pairs.Keys) {
        $localVar = $pairs[$ghcrVar]
        $localValue = [Environment]::GetEnvironmentVariable($localVar)
        if (-not [string]::IsNullOrWhiteSpace($localValue)) {
            Set-Item -Path "Env:$ghcrVar" -Value $localValue
        }
    }

    switch ($script:SelectedAiBackend) {
        'cpu' { if ($env:GA_AI_CPU_IMAGE) { $env:GA_AI_GHCR_IMAGE = $env:GA_AI_CPU_IMAGE } }
        'rocm' { if ($env:GA_AI_ROCM_IMAGE) { $env:GA_AI_GHCR_IMAGE = $env:GA_AI_ROCM_IMAGE } }
        'vulkan' { if ($env:GA_AI_VULKAN_IMAGE) { $env:GA_AI_GHCR_IMAGE = $env:GA_AI_VULKAN_IMAGE } }
        'cuda13' { if ($env:GA_AI_CUDA_IMAGE) { $env:GA_AI_GHCR_IMAGE = $env:GA_AI_CUDA_IMAGE } }
    }
}

function Resolve-InstallerComposeArgs {
    param(
        [Parameter(Mandatory = $true)][string]$DockerDir,
        [Parameter(Mandatory = $true)][string[]]$FragmentFiles
    )

    $composeDir = Join-Path $DockerDir $script:InstallerComposeDir
    $args = New-Object System.Collections.Generic.List[string]
    foreach ($fragment in $FragmentFiles) {
        $path = Join-Path $composeDir $fragment
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Compose fragment not found: $path"
        }
        $args.Add('-f') | Out-Null
        $args.Add($path) | Out-Null
    }
    return @($args)
}

function Save-InstallerState {
    param(
        [Parameter(Mandatory = $true)][string]$StateFile,
        [Parameter(Mandatory = $true)][string]$DbLayout,
        [Parameter(Mandatory = $true)][string]$AiBackend,
        [Parameter(Mandatory = $true)][string[]]$Components,
        [Parameter(Mandatory = $true)][string[]]$ComposeFiles,
        [Parameter(Mandatory = $true)][string]$ComposeMode,
        [Parameter(Mandatory = $true)][string]$StartCommand
    )

    $epoch = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $lines = @(
        "DB_LAYOUT=$DbLayout",
        "AI_BACKEND=$AiBackend",
        "BACKEND=$AiBackend",
        "COMPONENTS=$($Components -join ',')",
        "COMPOSE_MODE=$ComposeMode",
        "COMPOSE_FILES=$($ComposeFiles -join ',')",
        "COMPOSE_FILE=$($ComposeFiles -join ',')",
        'HOST_MOUNT_OVERRIDE_FILE=docker-compose.host-mounts.generated.yml',
        'VOICE_PACK_OVERRIDE_FILE=docker-compose.voice-pack.local.yml',
        'DOCKER_DIRECTORY=docker',
        "START_COMMAND=$StartCommand",
        "LAST_RUN_EPOCH=$epoch"
    )
    [System.IO.File]::WriteAllText($StateFile, ($lines -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
}

function Invoke-InstallerWizard {
    param(
        [Parameter(Mandatory = $true)][string]$StateFile,
        [switch]$Reconfigure,
        [string]$DbLayoutOverride = '',
        [string]$AiBackendOverride = '',
        [string[]]$ComponentsOverride = @(),
        [switch]$AssumeYes
    )

    $catalog = Get-InstallerComponentCatalog
    $saved = $null
    if ((Test-Path -LiteralPath $StateFile) -and -not $Reconfigure) {
        $saved = Get-InstallerSelectionFromState -StateFile $StateFile
    }

    # DB layout
    if (-not [string]::IsNullOrWhiteSpace($DbLayoutOverride)) {
        $script:SelectedDbLayout = $DbLayoutOverride
    }
    elseif ($null -ne $saved) {
        $script:SelectedDbLayout = $saved.DbLayout
        Write-InstallerLog "Using saved DB layout: $($script:SelectedDbLayout)"
    }
    else {
        Write-Host ''
        Write-Host '  Database layout:'
        Write-Host ('    1) {0} (~{1} GB)' -f $catalog['db_bundled'].Label, $catalog['db_bundled'].SizeGb)
        Write-Host ('    2) {0} (~{1} GB)' -f $catalog['db_separate'].Label, $catalog['db_separate'].SizeGb)
        Write-Host ''
        if ($AssumeYes) {
            $script:SelectedDbLayout = 'bundled'
        }
        else {
            $choice = Read-Host 'Enter 1-2 [1=bundled]'
            $script:SelectedDbLayout = if ($choice -eq '2') { 'separate' } else { 'bundled' }
        }
    }

    # AI backend
    if (-not [string]::IsNullOrWhiteSpace($AiBackendOverride)) {
        $script:SelectedAiBackend = $AiBackendOverride
    }
    elseif ($null -ne $saved) {
        $script:SelectedAiBackend = $saved.AiBackend
        Write-InstallerLog "Using saved AI backend: $($script:SelectedAiBackend)"
    }
    else {
        Write-Host ''
        Write-Host '  AI container (sandbox, skills, local MCP servers, local models):'
        $aiOptions = @(
            @{ Key = 'none'; Num = 1 },
            @{ Key = 'slim'; Num = 2 },
            @{ Key = 'cpu'; Num = 3 },
            @{ Key = 'cuda13'; Num = 4 },
            @{ Key = 'rocm'; Num = 5 },
            @{ Key = 'vulkan'; Num = 6 }
        )
        foreach ($opt in $aiOptions) {
            $meta = $catalog["ai_$($opt.Key)"]
            Write-Host ('    {0}) {1} (~{2} GB)' -f $opt.Num, $meta.Label, $meta.SizeGb)
            Write-Host ('        {0}' -f $meta.Summary)
        }
        Write-Host ''
        if ($AssumeYes) {
            $script:SelectedAiBackend = 'slim'
        }
        else {
            $choice = Read-Host 'Enter 1-6 [2=slim]'
            $picked = $aiOptions | Where-Object { [string]$_.Num -eq $choice } | Select-Object -First 1
            $script:SelectedAiBackend = if ($null -ne $picked) { $picked.Key } elseif ([string]::IsNullOrWhiteSpace($choice)) { 'slim' } else { 'slim' }
        }
    }

    # Optional components
    if ($ComponentsOverride.Count -gt 0) {
        $script:SelectedComponents = @($ComponentsOverride)
    }
    elseif ($null -ne $saved) {
        $script:SelectedComponents = @($saved.Components)
        Write-InstallerLog "Using saved optional components: $($script:SelectedComponents -join ', ')"
    }
    else {
        $script:SelectedComponents = New-Object System.Collections.Generic.List[string]
        Write-Host ''
        Write-Host '  Optional components (y/n for each):'
        foreach ($component in $script:InstallerOptionalComponents) {
            $meta = $catalog[$component]
            Write-Host ''
            Write-Host ('  {0} (~{1} GB)' -f $meta.Label, $meta.SizeGb)
            Write-Host ('    {0}' -f $meta.Summary)
            if ($meta.Missing) { Write-Host ('    Without it: {0}' -f $meta.Missing) }
            $default = 'Y'
            if ($AssumeYes) {
                $script:SelectedComponents.Add($component) | Out-Null
                continue
            }
            $reply = Read-Host "  Include $component? [Y/n]"
            if ([string]::IsNullOrWhiteSpace($reply)) { $reply = $default }
            if ($reply -match '^[Yy]') { $script:SelectedComponents.Add($component) | Out-Null }
        }
        $script:SelectedComponents = @($script:SelectedComponents)
    }

    $prev = if ($null -ne $saved) { $saved } else { $null }
    if ($null -ne $prev -and $prev.DbLayout -ne $script:SelectedDbLayout) {
        Write-InstallerWarn 'DB layout changed. Data is not auto-migrated between bundled and separate SQL.'
    }

    $script:SelectedComposeFragments = @(Get-InstallerComposeFragments -DbLayout $script:SelectedDbLayout -AiBackend $script:SelectedAiBackend -Components $script:SelectedComponents)
    $est = Get-InstallerEstimatedSizeGb -DbLayout $script:SelectedDbLayout -AiBackend $script:SelectedAiBackend -Components $script:SelectedComponents
    Write-InstallerLog "Selected images ~ $est GB (not including model weights downloaded later inside the AI container)."
}

function Invoke-InstallerProgressivePull {
    param(
        [Parameter(Mandatory = $true)][string[]]$ComposeArgs,
        [Parameter(Mandatory = $true)][string]$EnvFile,
        [switch]$AssumeYes
    )

    $config = Invoke-InstallerDockerCapture -FilePath 'docker' -ArgumentList (@('compose') + $ComposeArgs + @('--env-file', $EnvFile, 'config', '--images')) -IgnoreErrors
    if ($config.ExitCode -ne 0) {
        Write-InstallerWarn 'Could not resolve image list from compose fragments.'
        return
    }

    $images = @($config.Output | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ -ne '' } | Select-Object -Unique)
    Write-InstallerLog "Pulling $($images.Count) image(s) sequentially..."
    foreach ($image in $images) {
        Write-InstallerLog "  docker pull $image"
        Invoke-InstallerDocker -FilePath 'docker' -ArgumentList @('pull', $image)
    }
}

function Invoke-InstallerPruneDeselected {
    param(
        [Parameter(Mandatory = $true)][string[]]$ComposeArgs,
        [Parameter(Mandatory = $true)][string]$EnvFile,
        [Parameter(Mandatory = $true)][string[]]$KeepServices
    )

    $allServices = @('mssql-express', 'guideants-webapi-ui', 'guideants-ai', 'docling-serve', 'documentserver', 'plantuml', 'readweb-searxng')
    $remove = @($allServices | Where-Object { $KeepServices -notcontains $_ })
    if ($remove.Count -eq 0) { return }

    Write-InstallerLog "Stopping deselected services: $($remove -join ', ')"
    Invoke-InstallerDockerCapture -FilePath 'docker' -ArgumentList (@('compose') + $ComposeArgs + @('--env-file', $EnvFile, 'stop') + $remove) -IgnoreErrors | Out-Null
    Invoke-InstallerDockerCapture -FilePath 'docker' -ArgumentList (@('compose') + $ComposeArgs + @('--env-file', $EnvFile, 'rm', '-f') + $remove) -IgnoreErrors | Out-Null
}

function Get-InstallerActiveServices {
    param(
        [Parameter(Mandatory = $true)][string]$DbLayout,
        [Parameter(Mandatory = $true)][string]$AiBackend,
        [Parameter(Mandatory = $true)][string[]]$Components
    )

    $services = New-Object System.Collections.Generic.List[string]
    if ($DbLayout -eq 'separate') { $services.Add('mssql-express') | Out-Null }
    $services.Add('guideants-webapi-ui') | Out-Null
    if ($AiBackend -ne 'none') { $services.Add('guideants-ai') | Out-Null }
    if ($Components -contains 'docling') { $services.Add('docling-serve') | Out-Null }
    if ($Components -contains 'documentserver') { $services.Add('documentserver') | Out-Null }
    if ($Components -contains 'plantuml') { $services.Add('plantuml') | Out-Null }
    if ($Components -contains 'searxng') { $services.Add('readweb-searxng') | Out-Null }
    return @($services)
}

function Build-InstallerComposeArgsFromState {
    param(
        [Parameter(Mandatory = $true)][string]$RootDir,
        [Parameter(Mandatory = $true)][string]$StateFile,
        [switch]$IncludeHostMountOverride,
        [switch]$IncludeVoicePackOverride,
        [switch]$IncludeRocmOverride
    )

    $dockerDir = Join-Path $RootDir 'docker'
    $selection = Get-InstallerSelectionFromState -StateFile $StateFile
    $fragments = if ($selection.ComposeFiles.Count -gt 0) {
        @($selection.ComposeFiles)
    }
    else {
        @(Get-InstallerComposeFragments -DbLayout $selection.DbLayout -AiBackend $selection.AiBackend -Components $selection.Components)
    }

    $args = @(Resolve-InstallerComposeArgs -DockerDir $dockerDir -FragmentFiles $fragments)

    if ($IncludeHostMountOverride.IsPresent) {
        $hostMountFile = Get-InstallerStateValue -StateFile $StateFile -Key 'HOST_MOUNT_OVERRIDE_FILE'
        if ([string]::IsNullOrWhiteSpace($hostMountFile)) { $hostMountFile = 'docker-compose.host-mounts.generated.yml' }
        $hostMountPath = Join-Path $dockerDir $hostMountFile
        if (Test-Path -LiteralPath $hostMountPath) {
            $args += @('-f', $hostMountPath)
        }
    }

    if ($IncludeVoicePackOverride.IsPresent) {
        $voicePackPath = Join-Path $dockerDir 'docker-compose.voice-pack.local.yml'
        if (Test-Path -LiteralPath $voicePackPath) {
            $args += @('-f', $voicePackPath)
        }
    }

    if ($IncludeRocmOverride.IsPresent -and $selection.AiBackend -eq 'rocm') {
        $rocmPath = Join-Path $dockerDir 'docker-compose.rocm-runtime.generated.yml'
        if (Test-Path -LiteralPath $rocmPath) {
            $args += @('-f', $rocmPath)
        }
    }

    return [pscustomobject]@{
        ComposeArgs = $args
        Selection = $selection
    }
}
    param([string]$Message)
    if ($null -ne $script:InstallerLogFn) { & $script:InstallerLogFn $Message } else { Write-Host "[guideants] $Message" }
}

function Write-InstallerWarn {
    param([string]$Message)
    if ($null -ne $script:InstallerWarnFn) { & $script:InstallerWarnFn $Message } else { Write-Warning "[guideants] $Message" }
}

function Invoke-InstallerDocker {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @()
    )
    if ($null -ne $script:InstallerDockerInvokeFn) {
        & $script:InstallerDockerInvokeFn $FilePath $ArgumentList
        return
    }
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) { throw "$FilePath failed with exit code $LASTEXITCODE" }
}

function Invoke-InstallerDockerCapture {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [switch]$IgnoreErrors
    )
    if ($null -ne $script:InstallerDockerCaptureFn) {
        return & $script:InstallerDockerCaptureFn $FilePath $ArgumentList $IgnoreErrors.IsPresent
    }
    try {
        $output = & $FilePath @ArgumentList 2>$null
        $code = if ($null -ne $LASTEXITCODE) { [int]$LASTEXITCODE } else { 0 }
    }
    catch {
        if ($IgnoreErrors) { return [pscustomobject]@{ ExitCode = 1; Output = @() } }
        throw
    }
    if (-not $IgnoreErrors -and $code -ne 0) { throw "$FilePath failed with exit code $code" }
    return [pscustomobject]@{ ExitCode = $code; Output = @($output) }
}
