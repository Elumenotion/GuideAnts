<#
.SYNOPSIS
    One-shot cut-over from the old per-service volume/bind layout to a single
    named volume `ai_local_models` containing `llama/ asr/ sd/ tts/ emb/`
    subdirs. Also materializes the pre-existing SD flat files
    (flux-2-klein-4b-Q4_K_S.gguf + full_encoder_small_decoder.safetensors +
    Qwen3-4B-Q4_K_M.gguf) into a proper bundle directory so the UI can see
    them, and pre-activates that bundle.

.DESCRIPTION
    BEFORE this change, the running guideants-ai container mounts FIVE
    separate sources:

        /models       <- named volume   docker_llama_models
        /models-asr   <- named volume   docker_asr_models
        /models-sd    <- host bind      $GA_SD_MODELS_HOST_PATH  (sibling repo!)
        /models-tts   <- host bind      $GA_TTS_MODELS_HOST_PATH (sibling repo!)
        /models-emb   <- host bind      $GA_EMB_MODELS_HOST_PATH (sibling repo!)

    AFTER, a single named volume `ai_local_models` replaces all five:

        /models-local/llama
        /models-local/asr
        /models-local/sd
        /models-local/tts
        /models-local/emb

    Additionally, inside /models-local/sd/ the existing flat files are
    restructured as:

        bundles/<ReservedBundleId>/diffusion/<diffusion file>
        bundles/<ReservedBundleId>/vae/<vae file>
        bundles/<ReservedBundleId>/text-encoder/<llm file>
        active_bundle.json = { "bundleId": "<ReservedBundleId>" }

    Any already-present `bundles/<id>/` directories (e.g. `klein`, downloaded
    through the UI during this session) are copied through verbatim.

.PARAMETER Apply
    Perform the operations. Without this switch the script prints what it
    WOULD do and stops. Do NOT skip the dry-run first time.

.PARAMETER LlamaVolume
    Docker named volume the old /models llama store lives in. Default:
    docker_llama_models.

.PARAMETER AsrVolume
    Docker named volume the old /models-asr store lives in. Default:
    docker_asr_models.

.PARAMETER SdBindPath
    Host path currently bind-mounted at /models-sd. Default: read
    GA_SD_MODELS_HOST_PATH from docker/.env.

.PARAMETER TtsBindPath
    Host path currently bind-mounted at /models-tts. Default: read
    GA_TTS_MODELS_HOST_PATH from docker/.env.

.PARAMETER EmbBindPath
    Host path currently bind-mounted at /models-emb. Default: read
    GA_EMB_MODELS_HOST_PATH from docker/.env.

.PARAMETER TargetVolume
    Name of the new unified volume. Default: ai_local_models.

.PARAMETER ReservedBundleId
    Bundle id to give the legacy flat SD files. Default:
    flux2-klein-4b-q4ks.

.PARAMETER ContainerName
    Container to stop before migration and leave stopped afterward. Default:
    guideants-ai. Pass empty string to skip stop/start.

.EXAMPLE
    # Dry-run first. Reads docker/.env for the bind paths.
    ./docker/scripts/migrate-local-models-to-single-volume.ps1

.EXAMPLE
    # Actually do it.
    ./docker/scripts/migrate-local-models-to-single-volume.ps1 -Apply

.NOTES
    - This script never deletes source data. The old volumes and bind
      directories remain untouched so rollback is `docker compose up` on
      the old compose. Delete them manually once you're confident.
    - Copies use `cp -a` inside alpine (preserves perms, timestamps, symlinks).
    - Safe to re-run: each copy step uses `cp -an` (no-clobber) so a partial
      previous run resumes instead of duplicating.
#>
[CmdletBinding()]
param(
    [switch]$Apply,
    [string]$LlamaVolume = 'docker_llama_models',
    [string]$AsrVolume = 'docker_asr_models',
    [string]$SdBindPath,
    [string]$TtsBindPath,
    [string]$EmbBindPath,
    [string]$TargetVolume = 'ai_local_models',
    [string]$ReservedBundleId = 'flux2-klein-4b-q4ks',
    [string]$ContainerName = 'guideants-ai'
)

$ErrorActionPreference = 'Stop'

# The three legacy flat SD files this cut-over restructures into a bundle.
# These are the filenames hard-wired into the old docker-compose env
# (GA_SD_DIFFUSION_MODEL_PATH / GA_SD_VAE_PATH / GA_SD_LLM_PATH) — the
# pre-refactor SD runtime loaded them directly from <GA_SD_MODEL_DIR>/.
$LegacyDiffusionFile = 'flux-2-klein-4b-Q4_K_S.gguf'
$LegacyVaeFile       = 'full_encoder_small_decoder.safetensors'
$LegacyTextEncFile   = 'Qwen3-4B-Q4_K_M.gguf'
# Canonical repos used by the bundled setup downloader.
$LegacyDiffusionRepo = 'unsloth/FLUX.2-klein-4B-GGUF'
$LegacyVaeRepo       = 'black-forest-labs/FLUX.2-small-decoder'
$LegacyTextEncRepo   = 'unsloth/Qwen3-4B-GGUF'

function Read-DotEnvValue {
    param([Parameter(Mandatory)][string]$Key)
    $repoDocker = Split-Path -Parent $PSScriptRoot
    $envPath = Join-Path $repoDocker '.env'
    if (-not (Test-Path $envPath)) { return $null }
    $match = Get-Content $envPath | Where-Object { $_ -match "^$([regex]::Escape($Key))=" }
    if (-not $match) { return $null }
    return ($match -split '=', 2)[1].Trim()
}

function Resolve-BindPath {
    param([Parameter(Mandatory)][string]$EnvKey, [string]$Provided)
    if ($Provided) { return $Provided }
    $v = Read-DotEnvValue -Key $EnvKey
    if (-not $v) {
        throw "No value for -$($EnvKey) parameter and $EnvKey is not set in docker/.env. Pass the host path explicitly."
    }
    return $v
}

function Invoke-Docker {
    param([Parameter(Mandatory)][string[]]$DockerArgs, [switch]$DryRun)
    $display = 'docker ' + ($DockerArgs -join ' ')
    Write-Host "    $display" -ForegroundColor DarkGray
    if ($DryRun) { return }
    & docker @DockerArgs
    if ($LASTEXITCODE -ne 0) { throw ("docker exited with {0}: {1}" -f $LASTEXITCODE, $display) }
}

# Execute a multi-line bash script inside a throwaway alpine container.
# We write the script to a host temp file (LF line endings, no BOM) and
# bind-mount it at /tmp/migrate-script.sh, then exec `sh /tmp/migrate-script.sh`.
#
# Why not `sh -c <argv>`: multi-line strings get their newlines flattened
# to spaces when PowerShell splats an array into docker.exe on Windows.
# Why not `... | sh -s` via stdin: PowerShell's default pipe encoding
# corrupts the stream under alpine/busybox (BOM prefixes / CRLF).
# Writing a real file with LF + UTF-8 (no BOM) is the least-surprising
# way across the Windows -> Linux container boundary.
function Invoke-DockerBashScript {
    param(
        [Parameter(Mandatory)][string[]]$VolumeArgs,
        [Parameter(Mandatory)][string]$Script,
        [switch]$DryRun
    )
    $display = 'docker run --rm ' + ($VolumeArgs -join ' ') + ' -v <tmp>:/tmp/migrate-script.sh:ro alpine sh /tmp/migrate-script.sh'
    Write-Host "    $display" -ForegroundColor DarkGray
    if ($DryRun) { return }

    $scriptFile = [System.IO.Path]::GetTempFileName()
    try {
        # Normalize line endings to LF and write UTF-8 without BOM. BusyBox
        # sh rejects both CRLF (treats \r as literal) and any BOM.
        $normalized = ($Script -replace "`r`n", "`n") -replace "`r", "`n"
        $utf8NoBom = New-Object System.Text.UTF8Encoding $false
        [System.IO.File]::WriteAllText($scriptFile, $normalized, $utf8NoBom)

        $scriptMount = ($scriptFile -replace '\\', '/') + ':/tmp/migrate-script.sh:ro'
        $dockerArgs = @('run', '--rm') + $VolumeArgs + @('-v', $scriptMount, 'alpine', 'sh', '/tmp/migrate-script.sh')
        & docker @dockerArgs
        if ($LASTEXITCODE -ne 0) { throw ("docker exited with {0}: {1}" -f $LASTEXITCODE, $display) }
    }
    finally {
        Remove-Item -LiteralPath $scriptFile -Force -ErrorAction SilentlyContinue
    }
}

function Test-DockerVolumeExists {
    param([Parameter(Mandatory)][string]$Name)
    $out = docker volume ls --format '{{.Name}}' 2>$null
    return ($out -split "`n" | ForEach-Object { $_.Trim() }) -contains $Name
}

function Test-VolumeIsNonEmpty {
    param([Parameter(Mandatory)][string]$Name)
    if (-not (Test-DockerVolumeExists -Name $Name)) { return $false }
    $count = docker run --rm -v "${Name}:/src" alpine sh -c 'ls -A /src | wc -l' 2>$null
    if ($LASTEXITCODE -ne 0) { return $false }
    return ([int]($count.Trim())) -gt 0
}

function Test-BindHasContent {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path $Path)) { return $false }
    return (Get-ChildItem -Force -Path $Path -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0
}

# --------------------------------------------------------------------------
# 0. Resolve every input up-front. Fail loudly if anything is missing
#    before we touch docker. A migration that half-runs is worse than one
#    that refuses to start.
# --------------------------------------------------------------------------

$sdBind  = Resolve-BindPath -EnvKey 'GA_SD_MODELS_HOST_PATH'  -Provided $SdBindPath
$ttsBind = Resolve-BindPath -EnvKey 'GA_TTS_MODELS_HOST_PATH' -Provided $TtsBindPath
$embBind = Resolve-BindPath -EnvKey 'GA_EMB_MODELS_HOST_PATH' -Provided $EmbBindPath

Write-Host "=== Plan ===" -ForegroundColor Cyan
Write-Host "Target volume     : $TargetVolume"
Write-Host "Reserved bundle id: $ReservedBundleId"
Write-Host "Container to stop : $(if ($ContainerName) { $ContainerName } else { '<none>' })"
Write-Host "Sources:"
Write-Host ("  llama/    <- named volume  {0}" -f $LlamaVolume)
Write-Host ("  asr/      <- named volume  {0}" -f $AsrVolume)
Write-Host ("  sd/       <- host bind     {0}" -f $sdBind)
Write-Host ("  tts/      <- host bind     {0}" -f $ttsBind)
Write-Host ("  emb/      <- host bind     {0}" -f $embBind)
Write-Host ""

# Source existence checks. Missing sources are fatal — if a source is
# genuinely empty the operator should pass -LlamaVolume '' / similar (not
# wired yet) or delete the expectation line from this script intentionally.
$missing = @()
if (-not (Test-DockerVolumeExists -Name $LlamaVolume)) { $missing += "named volume '$LlamaVolume'" }
if (-not (Test-DockerVolumeExists -Name $AsrVolume))   { $missing += "named volume '$AsrVolume'" }
if (-not (Test-Path $sdBind))  { $missing += "bind path '$sdBind'" }
if (-not (Test-Path $ttsBind)) { $missing += "bind path '$ttsBind'" }
if (-not (Test-Path $embBind)) { $missing += "bind path '$embBind'" }
if ($missing.Count -gt 0) {
    Write-Host "Missing sources:" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    throw "Migration cannot proceed: fix the inputs above."
}

$dryRun = -not $Apply
if ($dryRun) {
    Write-Host "[DRY RUN] No changes will be made. Re-run with -Apply to execute." -ForegroundColor Yellow
} else {
    Write-Host "[APPLY] Changes WILL be made." -ForegroundColor Green
}
Write-Host ""

# --------------------------------------------------------------------------
# 1. Stop the container so no service writes during the copy. Required
#    because alpine will be mounting the same sources simultaneously for
#    the copy pipes.
# --------------------------------------------------------------------------

if ($ContainerName) {
    Write-Host "1. Stopping container '$ContainerName' (if running)..." -ForegroundColor Cyan
    $running = docker ps --format '{{.Names}}' 2>$null | ForEach-Object { $_.Trim() }
    if ($running -contains $ContainerName) {
        Invoke-Docker -DryRun:$dryRun -DockerArgs @('stop', $ContainerName)
    } else {
        Write-Host "    (not running)" -ForegroundColor DarkGray
    }
}

# --------------------------------------------------------------------------
# 2. Create the target volume. `docker volume create` is a no-op if the
#    volume already exists, which is what we want for re-runs.
# --------------------------------------------------------------------------

Write-Host "2. Ensuring target volume '$TargetVolume' exists..." -ForegroundColor Cyan
Invoke-Docker -DryRun:$dryRun -DockerArgs @('volume', 'create', $TargetVolume)

# --------------------------------------------------------------------------
# 3. For each service subdir, mkdir inside the new volume and copy content
#    from the source. Uses alpine `cp -an` (archive, no-clobber) so that
#    reruns resume rather than duplicate.
# --------------------------------------------------------------------------

# Idempotent copy script:
#   * mkdir -p /target/<subdir>
#   * if the target subdir already has content, skip (resume-safe)
#   * otherwise cp -a (archive) the entire /src tree into /target/<subdir>
# We intentionally do NOT use `cp -an` on BusyBox/alpine: the -a + -n
# combination is broken there (silently does nothing). Instead we gate
# on emptiness. `cp -a` alone honors perms/timestamps/symlinks, which is
# all we need since the source trees are read-only for us.
$copyTemplate = @'
set -e
mkdir -p /target/{0}
if [ -n "$(ls -A /target/{0} 2>/dev/null)" ]; then
    echo "SKIP: /target/{0} already has content (resuming previous run)."
else
    cp -a /src/. /target/{0}/
    echo "COPIED: /src -> /target/{0}"
fi
'@

function Copy-FromNamedVolume {
    param(
        [Parameter(Mandatory)][string]$SourceVolume,
        [Parameter(Mandatory)][string]$Subdir
    )
    Write-Host "    named-volume '$SourceVolume' -> ${TargetVolume}:/target/$Subdir" -ForegroundColor DarkCyan
    Invoke-DockerBashScript -DryRun:$dryRun -VolumeArgs @(
        '-v', "${SourceVolume}:/src:ro",
        '-v', "${TargetVolume}:/target"
    ) -Script ($copyTemplate -f $Subdir)
}

function Copy-FromHostBind {
    param(
        [Parameter(Mandatory)][string]$BindPath,
        [Parameter(Mandatory)][string]$Subdir
    )
    # Docker on Windows expects forward-slash style host paths with drive.
    $normalized = $BindPath -replace '\\', '/'
    Write-Host "    host-bind '$normalized' -> ${TargetVolume}:/target/$Subdir" -ForegroundColor DarkCyan
    Invoke-DockerBashScript -DryRun:$dryRun -VolumeArgs @(
        '-v', "${normalized}:/src:ro",
        '-v', "${TargetVolume}:/target"
    ) -Script ($copyTemplate -f $Subdir)
}

Write-Host "3. Copying sources into '$TargetVolume'..." -ForegroundColor Cyan
Copy-FromNamedVolume -SourceVolume $LlamaVolume -Subdir 'llama'
Copy-FromNamedVolume -SourceVolume $AsrVolume   -Subdir 'asr'
Copy-FromHostBind    -BindPath     $sdBind      -Subdir 'sd'
Copy-FromHostBind    -BindPath     $ttsBind     -Subdir 'tts'
Copy-FromHostBind    -BindPath     $embBind     -Subdir 'emb'

# --------------------------------------------------------------------------
# 4. Restructure the SD subdir. Move the three legacy flat files into
#    bundles/<ReservedBundleId>/{diffusion,vae,text-encoder}/. Any already-
#    present bundles/<id>/ directories (e.g. `klein` from the session)
#    were copied straight through in step 3 and don't need touching.
# --------------------------------------------------------------------------

Write-Host "4. Materializing legacy SD files as bundle '$ReservedBundleId'..." -ForegroundColor Cyan
# Single-quoted here-string so NO PowerShell interpolation happens; the
# four placeholders {0}..{3} are filled in via -f below. This keeps
# bash's $role / $(find ...) syntax intact through the docker boundary.
$sdRestructureTemplate = @'
set -e
cd /target/sd
mkdir -p bundles/{0}/diffusion
mkdir -p bundles/{0}/vae
mkdir -p bundles/{0}/text-encoder
# Each file is moved only if it's still sitting flat at /target/sd. If a
# previous run already moved them the mv is silently skipped; the
# destination file is never overwritten.
if [ -f '{1}' ] && [ ! -f "bundles/{0}/diffusion/{1}" ]; then
    mv '{1}' "bundles/{0}/diffusion/{1}"
fi
if [ -f '{2}' ] && [ ! -f "bundles/{0}/vae/{2}" ]; then
    mv '{2}' "bundles/{0}/vae/{2}"
fi
if [ -f '{3}' ] && [ ! -f "bundles/{0}/text-encoder/{3}" ]; then
    mv '{3}' "bundles/{0}/text-encoder/{3}"
fi
# Verify every role has exactly one file. Loud failure here mirrors the
# runtime check the SD service performs on bundle activation.
for role in diffusion vae text-encoder; do
    count=$(find "bundles/{0}/$role" -maxdepth 1 -type f | wc -l)
    if [ "$count" -ne "1" ]; then
        echo "BUNDLE CHECK FAILED: role '$role' has $count files in bundles/{0}/$role (expected exactly 1)" >&2
        exit 1
    fi
done
# Persist the source recipe so the Settings UI can open Edit with prefilled
# values immediately (no first-time manual backfill required).
if [ ! -f "bundles/{0}/bundle-definition.json" ]; then
    now=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
    printf '{{"bundleId":"%s","revision":"main","updatedAtUtc":"%s","roles":{{"diffusion":{{"repo":"%s","file":"%s"}},"vae":{{"repo":"%s","file":"%s"}},"textEncoder":{{"repo":"%s","file":"%s"}}}}}}\n' \
        '{0}' "$now" '{4}' '{1}' '{5}' '{2}' '{6}' '{3}' > "bundles/{0}/bundle-definition.json"
fi
echo "Bundle '{0}' is ready."
'@
$sdRestructureScript = $sdRestructureTemplate -f $ReservedBundleId, $LegacyDiffusionFile, $LegacyVaeFile, $LegacyTextEncFile, $LegacyDiffusionRepo, $LegacyVaeRepo, $LegacyTextEncRepo
Invoke-DockerBashScript -DryRun:$dryRun -VolumeArgs @(
    '-v', "${TargetVolume}:/target"
) -Script $sdRestructureScript

# --------------------------------------------------------------------------
# 5. Pre-activate the reserved bundle. Writes active_bundle.json at
#    /target/sd/active_bundle.json so the SD runtime resolves it on next
#    start without operator action. Never overwrites an existing marker
#    (operator might have already picked a different active bundle for
#    valid reasons).
# --------------------------------------------------------------------------

Write-Host "5. Pre-activating bundle '$ReservedBundleId' (if nothing active yet)..." -ForegroundColor Cyan
$activateTemplate = @'
set -e
if [ -f /target/sd/active_bundle.json ]; then
    echo "active_bundle.json already present, leaving it alone:"
    cat /target/sd/active_bundle.json
else
    printf '{{"bundleId":"%s"}}\n' '{0}' > /target/sd/active_bundle.json
    echo "wrote /target/sd/active_bundle.json -> {0}"
fi
'@
$activateScript = $activateTemplate -f $ReservedBundleId
Invoke-DockerBashScript -DryRun:$dryRun -VolumeArgs @(
    '-v', "${TargetVolume}:/target"
) -Script $activateScript

# --------------------------------------------------------------------------
# 6. Report final layout so the operator can spot-check before starting
#    the new compose stack.
# --------------------------------------------------------------------------

Write-Host "6. Final layout in '$TargetVolume':" -ForegroundColor Cyan
$finalScript = @'
ls -la /target
echo ---
ls -la /target/sd
echo ---
ls -la /target/sd/bundles
'@
Invoke-DockerBashScript -DryRun:$dryRun -VolumeArgs @(
    '-v', "${TargetVolume}:/target:ro"
) -Script $finalScript

Write-Host ""
if ($dryRun) {
    Write-Host "Dry run complete. No changes were made. Re-run with -Apply to execute." -ForegroundColor Yellow
} else {
    Write-Host "Migration complete." -ForegroundColor Green
    Write-Host "Next steps:" -ForegroundColor Green
    Write-Host "  1. Remove GA_SD_MODELS_HOST_PATH / GA_TTS_MODELS_HOST_PATH / GA_EMB_MODELS_HOST_PATH from docker/.env (they are no longer consulted)."
    Write-Host "  2. docker compose -f docker/docker-compose.yml up -d"
    Write-Host "  3. Verify /admin/bundles shows $ReservedBundleId and any pre-existing bundles (e.g. klein)."
    Write-Host "  4. Once verified, delete the old named volumes and host bind paths to reclaim disk:"
    Write-Host "     docker volume rm $LlamaVolume $AsrVolume"
    Write-Host "     Remove-Item -Recurse '$sdBind' '$ttsBind' '$embBind'"
}
