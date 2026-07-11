# ROCm Container Runtime (Windows WSL + Native Linux) and WSL Setup

## 1. Purpose

Make the GuideAnts `rocm` backend run correctly in Docker containers on **both**:

- **Native Linux hosts** — GPU via the kernel fusion driver (`/dev/kfd` + `/dev/dri`).
- **Windows hosts (Docker Desktop / WSL2)** — GPU via AMD **ROCDXG** (`librocdxg`), which bridges the Linux ROCm runtime to the Windows GPU driver through Microsoft's DXCore interface (`/dev/dxg`).

The two paths need *different* container wiring. This is handled at launch time by a generated compose override, so the static compose files stay host-agnostic.

This document records the design, the files involved, the required WSL configuration, and the concrete failures that were fixed to get here.

## 2. Background: why two runtime paths

ROCm's normal Linux path opens `/dev/kfd` (kernel fusion driver) directly. That device **does not exist in WSL2**. On Windows/WSL, AMD provides **ROCDXG** — a user-mode library (`librocdxg.so`) that routes ROCm/HIP compute calls through the Windows GPU driver via DXCore (`/dev/dxg`).

Requirements for the WSL ROCDXG path (per AMD's [`librocdxg`](https://github.com/ROCm/librocdxg) container guidance):

| Flag / mount | Purpose |
| --- | --- |
| `--device /dev/dxg` | Expose the DXCore GPU device to the container. |
| `-v /usr/lib/wsl/lib/libdxcore.so:/usr/lib/libdxcore.so` | Microsoft DXCore library (comes from the WSL host). |
| `-v <librocdxg.so>:/usr/lib/librocdxg.so` | AMD ROCDXG bridge library (from the ROCm install inside a WSL distro). |
| `-e HSA_ENABLE_DXG_DETECTION=1` | Required for ROCm releases before 7.13 to load `librocdxg` and detect the GPU via `/dev/dxg`. |
| `--cap-add SYS_PTRACE`, `--security-opt seccomp=unconfined` | Required by the ROCDXG runtime. |

Hardware support note: AMD Strix Halo / Ryzen AI Max+ 395 (Radeon 8060S, `gfx1151`) is supported via ROCDXG starting with Adrenalin 26.2.2+ and ROCm 7.2.x with `librocdxg` 1.2.0.

## 3. Design: generated runtime override

At launch, for the `rocm` backend only, the launcher writes a compose override:

```
installer/docker/docker-compose.rocm-runtime.generated.yml   (installer flow)
docker/docker-compose.rocm-runtime.generated.yml             (repo flow)
```

The static rocm compose files (`docker-compose.rocm.yml`, `docker-compose.ghcr-rocm.yml`) intentionally carry **no** GPU `devices`, `group_add`, or ROCDXG binds. All of that lives in the generated override, which is layered on with an extra `-f` argument. The file is git-ignored and rewritten every launch.

### Native Linux override (written when NOT in WSL/Docker Desktop)

```yaml
services:
  guideants-ai:
    devices:
      - /dev/kfd
      - /dev/dri
    group_add:
      - video
      - render
  docling-serve:
    devices:
      - /dev/kfd
      - /dev/dri
    group_add:
      - video
      - render
```

### WSL ROCDXG override (written under Docker Desktop / WSL)

```yaml
services:
  guideants-ai:
    devices:
      - /dev/dxg
    cap_add:
      - SYS_PTRACE
    security_opt:
      - seccomp:unconfined
    environment:
      - HSA_ENABLE_DXG_DETECTION=1
    volumes:
      - type: bind
        source: /usr/lib/wsl/lib/libdxcore.so
        target: /usr/lib/libdxcore.so
        read_only: true
      - type: bind
        source: ./volumes/rocm-wsl/lib/librocdxg.so
        target: /lib/librocdxg.so
        read_only: true
      - type: bind
        source: ./volumes/rocm-wsl/lib/librocdxg.so
        target: /usr/lib/librocdxg.so
        read_only: true
      # (optional) dids.conf bind added only if present in the WSL distro
  docling-serve:
    # same devices / cap_add / security_opt / environment / volumes as guideants-ai
```

`docling-serve` receives the same GPU wiring as `guideants-ai`. The static ROCm compose files default to the CPU docling image; set `DOCLING_SERVE_ROCM_IMAGE` to a local `docling-serve-rocm72` build and `DOCLING_DEVICE=cuda` for GPU document intelligence (see section 12).

An example of both shapes is committed at `docker/docker-compose.rocm-runtime.generated.example.yml`.

## 4. The librocdxg staging step (Windows only)

Under Docker Desktop, `librocdxg.so` lives inside a **user WSL distro** (e.g. `/opt/rocm/lib/librocdxg.so` in `Ubuntu-24.04`), not on a path the Docker daemon can bind. Docker Desktop **cannot bind-mount a single file through `//wsl.localhost/...`** — it silently creates an empty *directory* at the target, which ROCm then rejects with:

```
Cannot load librocdxg.so, failed:/lib/librocdxg.so: cannot read file data: Is a directory
```

To avoid this, the launcher **stages** the real library onto a normal Windows path the daemon can bind:

```
installer/docker/volumes/rocm-wsl/lib/librocdxg.so
installer/docker/volumes/rocm-wsl/share/dids.conf   (only if the distro has it)
```

- PowerShell helper: `installer/scripts/rocm-runtime-compose.ps1` (`Stage-WslRocmLibs`), copies from the resolved WSL distro via `cp -L`.
- Bash helper: `installer/scripts/rocm-runtime-compose.sh` (`stage_rocm_wsl_libs_for_compose`), used when the launcher runs from inside WSL.

The staged file is bound to **both** `/lib/librocdxg.so` and `/usr/lib/librocdxg.so` because the HSA runtime looks in `/lib` first. `dids.conf` is optional and only bound when present.

The staging dirs are git-ignored:

```
/installer/docker/volumes/rocm-wsl/
/docker/volumes/rocm-wsl/
```

## 5. Files added / changed this session

### New

- `installer/scripts/rocm-runtime-compose.ps1` — Windows runtime selection + librocdxg staging + override generation.
- `installer/scripts/rocm-runtime-compose.sh` — bash equivalent (`select_rocm_runtime`), for Linux/WSL launcher flows.
- `installer/scripts/install-rocm-wsl.sh` — installs ROCm 7.2.4 + `librocdxg` 1.2.0 inside an Ubuntu WSL distro.
- `installer/scripts/rocm-wsl.profile` — `/etc/profile.d` env (`HSA_ENABLE_DXG_DETECTION`, `LD_LIBRARY_PATH`, `PATH`) for the WSL distro.
- `docker/docker-compose.rocm-runtime.generated.example.yml` — documented example of both override shapes.

### Changed

- `installer/guideants.ps1`
  - `Get-WslUserDistros` — lists user WSL distros; strips UTF-16 NULs from `wsl -l -q`; skips `docker-desktop`/`docker-desktop-data`.
  - `Invoke-WslUserProbe` — runs a probe in each user distro with `-d <distro>`.
  - `Test-AmdGpuDetected` — checks `/dev/kfd`, `rocminfo`, Windows video controllers, and `/dev/dxg`/`rocminfo` inside user distros.
  - `Get-RocmVersion` — reads `/opt/rocm/.info/version`, `rocminfo`, or `dpkg`, including via WSL probe on Windows.
  - `Select-RocmRuntime` — invokes the PS runtime helper for the `rocm` backend; removes any stale override otherwise.
  - `Start-GuideAntsStack` — layers the generated override via `Add-ComposeOverrideIfValid`.
- `installer/guideants.sh` — sources `rocm-runtime-compose.sh` and calls `select_rocm_runtime`.
- `start_windows.cmd`, `start_linux.sh`, `start_macos.sh` — call the runtime selector and include the override.
- `docker/docker-compose.rocm.yml`, `docker/docker-compose.ghcr-rocm.yml` (and `installer/docker/` copies) — removed static `devices`/`group_add`; GPU wiring now comes from the generated override.
- `.gitignore` — ignores the generated override and the `rocm-wsl` staging dirs.

## 6. Windows setup (WSL ROCDXG)

Prerequisites:

- Windows 11, AMD Adrenalin driver 26.2.2+ (ROCDXG-capable), WSL2, Docker Desktop.
- A **user** WSL distro (Ubuntu 24.04 recommended). The `docker-desktop` distro is not usable for this — it has no ROCm install.

Steps:

1. Install a user distro if you don't have one:

```powershell
wsl --install -d Ubuntu-24.04
```

2. Install ROCm + `librocdxg` inside it (run as root):

```powershell
wsl -d Ubuntu-24.04 -u root bash /mnt/c/repos/GuideAnts/installer/scripts/install-rocm-wsl.sh
```

This installs ROCm 7.2.4, removes the conflicting non-WSL HSA packages, installs `librocdxg` 1.2.0, writes `/etc/profile.d/rocm-wsl.sh`, and verifies with `rocminfo`.

3. Confirm the GPU is visible inside WSL:

```powershell
wsl -d Ubuntu-24.04 sh -lc "HSA_ENABLE_DXG_DETECTION=1 rocminfo | grep -A2 gfx"
```

Expected: `AMD Radeon(TM) 8060S Graphics` / `gfx1151` (for Strix Halo).

4. Launch GuideAnts with the ROCm backend:

```powershell
cd C:\repos\GuideAnts\installer
.\guideants.ps1 --backend rocm
```

The launcher stages `librocdxg`, writes the WSL override, and starts the stack.

## 7. Native Linux setup

Prerequisites:

- ROCm installed on the host (kernel driver present → `/dev/kfd` and `/dev/dri` exist).
- Host user in the `video`/`render` groups (for non-root Docker), or run the daemon appropriately.

Launch:

```bash
cd installer
./guideants.sh --backend rocm
```

The launcher detects native Linux (no Docker Desktop, no `/dev/dxg`) and writes the KFD override with `group_add: [video, render]`.

## 8. How detection and mode selection work

1. **Backend gate** — the override is only written when the selected backend is `rocm`; otherwise any stale override is deleted.
2. **Mode detection** (`is_rocm_wsl_mode` / `Test-RocmWslMode`):
   - `docker info` OperatingSystem contains `Docker Desktop` → **WSL ROCDXG**.
   - Running inside WSL (`/proc/version` mentions microsoft/wsl) **and** `/dev/dxg` exists → **WSL ROCDXG**.
   - Otherwise → **native Linux**.
3. **WSL library resolution** — the PS helper enumerates user distros (`wsl -l -q`, NUL-stripped, `docker-desktop` skipped), finds `librocdxg.so`/`.so.1.2.0`, and stages it to `volumes/rocm-wsl/lib`.
4. **Version reporting** — `Get-RocmVersion` reads `/opt/rocm/.info/version` (written by the install script), `rocminfo`, or `dpkg`, including through a WSL probe on Windows. Minimum enforced is `>= 6.0.0`.

`--doctor --backend rocm` runs all of the above read-only and prints the exact `docker compose ... up -d` it *would* run, including the override `-f`.

## 9. Failures fixed this session (root causes)

| Symptom | Root cause | Fix |
| --- | --- | --- |
| ROCm not detected on Windows | `wsl.exe` defaulted to `docker-desktop`; probes ran in the wrong distro | Enumerate user distros and probe with `-d`. |
| Distro names unparsable | `wsl -l -q` emits UTF-16 with NUL bytes | Strip `` `0 `` NULs before parsing. |
| Version probe failed | Embedded double-quotes broke under PowerShell | Simplified probe string. |
| `Cannot bind parameter 'Encoding' ... utf8NoBOM` | `-Encoding utf8NoBOM` is PowerShell 7+ only; launcher runs on Windows PowerShell 5.1 | Write files via .NET `UTF8Encoding($false)` (`Write-Utf8NoBomFile`). |
| Garbled comment bytes in generated YAML | Em dash in heredoc under PS 5.1 | Use ASCII hyphen in generated headers. |
| `unable to find group render` on container start | `group_add: [video, render]` in static compose; Docker Desktop VM has no `render` group | Move `group_add` into the **native Linux** override only. |
| `librocdxg.so ... Is a directory` | Docker Desktop can't bind a single file via `//wsl.localhost/...`; it makes an empty dir | Stage the real `.so` to a Windows path under `volumes/rocm-wsl/lib` and bind that. |
| `hsaKmtOpenKFD` undefined / segfault | ROCm fell back to the native KFD path because `librocdxg` wasn't loadable | Correct staged bind to `/lib` and `/usr/lib`, set `HSA_ENABLE_DXG_DETECTION=1`, add `SYS_PTRACE` + `seccomp:unconfined`. |
| `dids.conf` warning blocked launch | `dids.conf` treated as required | Made it optional; only bound when present. |

## 10. Verification

Confirm the merged compose has the right GPU wiring (read-only):

```powershell
docker compose `
  -f docker/docker-compose.ghcr-rocm.yml `
  -f docker/docker-compose.rocm-runtime.generated.yml `
  --env-file docker/.env config
```

Confirm GPU visibility inside the container image:

```powershell
docker run --rm --entrypoint sh `
  -v /usr/lib/wsl/lib/libdxcore.so:/usr/lib/libdxcore.so:ro `
  -v "C:/repos/GuideAnts/installer/docker/volumes/rocm-wsl/lib/librocdxg.so:/lib/librocdxg.so:ro" `
  -v "C:/repos/GuideAnts/installer/docker/volumes/rocm-wsl/lib/librocdxg.so:/usr/lib/librocdxg.so:ro" `
  --device /dev/dxg --cap-add SYS_PTRACE --security-opt seccomp=unconfined `
  -e HSA_ENABLE_DXG_DETECTION=1 `
  ghcr.io/elumenotion/guideants-ai-rocm:main `
  -c "/app/llama-server --list-devices"
```

Expected:

```
Available devices:
  ROCm0: AMD Radeon(TM) 8060S Graphics (97948 MiB, 95819 MiB free)
```

## 11. Known constraints

- Docker Desktop reports ~2.5 GB of `/dev/dxg` VRAM overhead (WDDM virtualization); expected, not a GuideAnts issue.
- The WSL library staging depends on a user WSL distro with ROCm installed. If none is found, the launcher warns and skips the ROCDXG override (it does not silently fall back to a broken KFD path).
- `HSA_ENABLE_DXG_DETECTION=1` is required for ROCm < 7.13; harmless on newer releases.

## 12. Docling on ROCm (optional GPU document intelligence)

Upstream does not publish ROCm docling images (~35 GB). Build locally from [docling-serve](https://github.com/docling-project/docling-serve) at your pinned tag:

```bash
git clone --branch v1.26.0 https://github.com/docling-project/docling-serve.git
cd docling-serve
# Match host ROCm: 7.2 on Windows/WSL and current Strix Halo stacks
make docling-serve-rocm72-image
docker tag ghcr.io/docling-project/docling-serve-rocm72:main docling-serve-rocm72:local
```

Add to `docker/.env` (or `installer/docker/.env`):

```env
DOCLING_SERVE_ROCM_IMAGE=docling-serve-rocm72:local
DOCLING_DEVICE=cuda
```

Launch the ROCm stack as usual (`--backend rocm`). The generated runtime override wires `docling-serve` with the same ROCDXG or native KFD devices as `guideants-ai`.

Verify GPU inside the docling image (WSL example):

```powershell
$lib = "C:/repos/GuideAnts/docker/volumes/rocm-wsl/lib/librocdxg.so"
docker run --rm --entrypoint python `
  -v /usr/lib/wsl/lib/libdxcore.so:/usr/lib/libdxcore.so:ro `
  -v "${lib}:/lib/librocdxg.so:ro" `
  -v "${lib}:/usr/lib/librocdxg.so:ro" `
  --device /dev/dxg --cap-add SYS_PTRACE --security-opt seccomp=unconfined `
  -e HSA_ENABLE_DXG_DETECTION=1 `
  docling-serve-rocm72:local `
  -c "import torch; print(torch.__version__); print(torch.cuda.is_available(), torch.cuda.get_device_name(0))"
```

Use **`docling-serve-rocm72`** (PyTorch `rocm7.2`), not `docling-serve-rocm` (PyTorch `rocm6.3`). On ROCDXG hosts the 6.3 build initializes HSA but returns `hipErrorNoDevice`.

Without `DOCLING_SERVE_ROCM_IMAGE`, ROCm stacks keep the CPU docling image (`DOCLING_DEVICE=cpu` default). GPU binds on `docling-serve` are harmless in that mode.
