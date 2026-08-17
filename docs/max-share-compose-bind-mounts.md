# Max Share and Compose Bind-Mount Architecture

## Purpose

This document defines how the Windows workstation and Max share GuideAnts content files with
Docker Compose stacks running on Max.

The content-files directory has one authoritative copy on the workstation. Max does not keep a
second local copy for these stacks, and a missing share must stop the stack rather than silently
switching to local storage.

## Machines and responsibilities

| Machine | Responsibility |
| --- | --- |
| `OFFICEDESKTOP` | Owns the repository files and publishes the `repos` SMB share. |
| `MAX` | Runs Docker Desktop, the ROCm containers, ComfyUI-video, and the SSH control session. |

The relevant workstation paths are:

```text
Physical source:
D:\repos\GuideAnts\docker\volumes\content-files

SMB share:
\\OFFICEDESKTOP\repos

Shared content-files path:
\\OFFICEDESKTOP\repos\GuideAnts\docker\volumes\content-files
```

The `GuideAnts-qwen38-27b-gguf\docker\volumes\content-files` directory is only a
placeholder directory in this workspace and is not the source for the Max video
stack.

On Max, the `R:` drive is the global mapping of the share:

```text
R:                         -> \\OFFICEDESKTOP\repos
R:\GuideAnts\docker\volumes\content-files
                           -> the workstation's content-files directory
```

The existing `C:\repos\GuideAnts` checkout on Max is a separate local checkout used by the
existing Max stack. It is not the authoritative content-files source for the cross-machine
video stack.

## Credential and share boundary

The workstation share is protected at both layers:

1. SMB share permissions grant access only to `OFFICEDESKTOP\LocalDoug`.
2. NTFS permissions on `D:\repos` grant that account the required repository access.

SMB encryption is enabled on the share. The share credential is not stored in:

- a Compose file;
- a checked-in `.env` file;
- a generated Compose override;
- a container environment variable; or
- a documentation example.

Max must use a Windows SMB global mapping, not a per-user `net use` mapping or a PowerShell
profile mapping:

```powershell
$credential = Get-Credential -UserName 'OFFICEDESKTOP\LocalDoug'

New-SmbGlobalMapping `
  -LocalPath 'R:' `
  -RemotePath '\\OFFICEDESKTOP\repos' `
  -Credential $credential `
  -Persistent $true `
  -RequireIntegrity $true `
  -RequirePrivacy $true
```

`New-SmbGlobalMapping` is the credential boundary for this deployment. It stores the credential
in Windows SMB mapping state, makes the mapping available to users, SSH sessions, Docker, and
services, and recreates it after reboot. `EnableLinkedConnections` does not replace this
mapping; that registry setting only addresses linked interactive UAC sessions.

If an `R:` mapping already exists, it must point to `\\OFFICEDESKTOP\repos`. Do not replace a
mapping pointing somewhere else without first identifying why it exists.

## Compose mount contract

The Max-only environment configuration sets the content source to the global mapping:

```dotenv
GA_CONTENT_FILES_HOST_PATH=R:/GuideAnts/docker/volumes/content-files
```

The Max Compose file uses a strict required-value expression. It must not use the local
development default `./volumes/content-files`:

```yaml
services:
  guideants-ai:
    volumes:
      - type: bind
        source: ${GA_CONTENT_FILES_HOST_PATH:?Set GA_CONTENT_FILES_HOST_PATH}
        target: /app/ContentFiles
        read_only: false

  comfyui-video:
    volumes:
      - type: bind
        source: ${GA_CONTENT_FILES_HOST_PATH:?Set GA_CONTENT_FILES_HOST_PATH}
        target: /app/ContentFiles
        read_only: false
```

The same source and target contract applies to any additional service that reads or writes
GuideAnts content files, including the full GuideAnts web/API and PlantUML services:

```text
Max host source:
R:\GuideAnts\docker\volumes\content-files

Container target:
/app/ContentFiles
```

The bind mount is read-write because the API, AI workflows, and video workflows may create or
update content files. Model caches, database state, and service-specific state remain named
Docker volumes; they are not placed on the workstation's content-files share unless explicitly
defined otherwise.

## Stack-specific usage

### Existing GuideAnts ROCm stack

The existing ROCm Compose definition uses `GA_CONTENT_FILES_HOST_PATH` for the following
content-file consumers:

- `guideants-ai`;
- `guideants-webapi-ui`; and
- `plantuml`.

When that stack is launched on Max, its Max-specific environment must resolve the variable to
the `R:` path above. A local developer environment may continue to use its local
`./volumes/content-files` value, but that local default must not be used by the Max stack.

### New AI plus ComfyUI-video stack

The new Max stack contains:

- the ROCm `guideants-ai` service;
- the existing `comfyui-video` service; and
- any required ROCm runtime devices and service networks.

Both services bind the same workstation-backed source to `/app/ContentFiles`. They do not use
separate local content directories and do not copy content files into named volumes.

The Compose project directory and image/model volumes may remain on Max. Only the declared
content-files bind source crosses the SMB share.

## Startup and restart sequence

The intended dependency order is:

```text
OFFICEDESKTOP SMB service
        |
        v
MAX persistent global SMB mapping (R:)
        |
        v
MAX Docker Desktop / Docker engine
        |
        v
Compose configuration validation
        |
        v
GuideAnts ROCm and ComfyUI-video containers
```

The persistent mapping survives a Max reboot, but the share still has to be reachable when the
mapping reconnects. Compose startup must validate the source before creating containers.

Recommended Max preflight checks:

```powershell
Get-SmbGlobalMapping -LocalPath 'R:'
Test-Path 'R:\GuideAnts\docker\volumes\content-files'
docker compose config
```

The resolved Compose configuration must show the `R:` content source, and the path check must
return `True`. After startup, inspect each relevant container and verify that
`/app/ContentFiles` is mounted and readable.

The definitive mount check must also read a known workstation file through every
layer. The current demo file is:

```text
R:\GuideAnts\docker\volumes\content-files\asr_en_fake48.wav
```

Its workstation SHA-256 is:

```text
FBB71EAC490C7CC2AA67A3F9CB137550CEA975E7F8F18A254183271614F5ADA9
```

The Max-side verification script compares that size and hash with
`/app/ContentFiles/asr_en_fake48.wav` inside both containers. A mount record or a
successful directory test alone is not considered proof.

## Failure behavior

A missing or invalid mapping is a deployment error:

- `Test-Path` fails;
- `docker compose config` or container creation fails; or
- the container health check reports the content mount unavailable.

The stack must not respond to that condition by changing the source to
`./volumes/content-files`, a local named volume, or an arbitrary alternate path. That would
make the stack appear healthy while writing data to the wrong machine.

Troubleshooting order on Max:

1. Confirm `Get-SmbGlobalMapping -LocalPath 'R:'` reports the expected remote path.
2. Confirm the content-files directory exists on `OFFICEDESKTOP`.
3. Confirm the `OFFICEDESKTOP\LocalDoug` account has both share and NTFS access.
4. Confirm SMB-In firewall access and SMB encryption on `OFFICEDESKTOP`.
5. Confirm Docker Desktop can consume the globally mapped `R:` path.
6. Re-run Compose validation and start the affected stack.

Do not solve an SSH visibility problem by adding another per-session drive mapping. The
Compose and Docker processes need the global mapping created by the Windows SMB client.

## Security invariants

- The workstation remains the authoritative owner of shared content files.
- SMB access is limited to the dedicated `OFFICEDESKTOP\LocalDoug` account.
- SMB integrity and privacy are required for the Max mapping.
- The SMB password never appears in Compose, `.env`, Git, logs, or container environment.
- `R:` is a persistent global mapping, not a session-local mapping.
- The Compose source is explicit and required; there is no silent local fallback.
- Removing a container or Compose project must not delete the workstation source directory.
- Any future service mounting content files must use the same source and
  `/app/ContentFiles` target contract.

## Deliberately rejected alternative

This deployment does not use a Docker `local` volume with inline CIFS credentials. That would
move SMB mounting into the Docker engine and would require a separate credential-file delivery
mechanism inside the Docker Desktop Linux backend. The Windows global SMB mapping provides the
required persistence and visibility to Docker Desktop while keeping the credential in the
Windows credential boundary.
