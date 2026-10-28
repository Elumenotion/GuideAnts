---
name: host-ssh
description: >-
  Run PowerShell on Windows hosts, bash in a WSL distro on Windows machines,
  or a POSIX shell on macOS/Linux hosts over SSH, via the machine registry in
  the guide Environment (GA_SSH_MACHINES). Use when the user needs host OS
  tasks — drives, dotnet, Playwright, processes, SSH into their laptop or
  desktop — not file I/O on paths already visible in the workspace file list.
  Per-machine "wsl" block (bash in the distro) or "os" field / auto-detection
  (uname -s) picks the command style; hosts resolve IPv4-first; paramiko
  fallback used when sshpass is unavailable.
---

# Host SSH (Windows PowerShell / WSL bash / macOS+Linux POSIX shell)

## Sandbox paths — authoritative, do not discover

Your **system context includes a file list** (`[@files]` / context options). That list,
plus the rules below, fully defines what exists and where. **Do not run `pwd`,
`find`, `ls /`, `cd /home`, or any other command to discover paths.** Exploration
wastes turns and breaks working-directory assumptions.

**SANDBOX CWD is already the notebook output directory** (private notebooks: the
`Output/` folder). Every `run_bash` / `run_python` call **starts there**.

| Rule | Do | Never |
|------|-----|--------|
| Skill scripts | `python3 Skills/host-ssh/scripts/host_ssh.py …` | `Output/Skills/…`, `/app/…`, `/home/…` |
| Deliverables | bare filenames in CWD | prefix `Output/` in commands |
| Change directory | stay in CWD | `cd /home`, `cd Output`, `cd` before skill commands |
| Inputs in the file list | use the **exact path shown** (`../repo/…`, `Skills/…`, bare names) | guess, probe, or re-derive |
| Host repo edits | edit via **mounted paths in the file list** (bash heredoc) | `run_python` one-liners that embed backslashes |

If `Skills/host-ssh/scripts/host_ssh.py` appears in the file list, run it **as written**
from CWD. If a path is in the list, the file exists — **trust the list**.

Host setup (OpenSSH, guide Environment variables) is in the operator README bundled with
this skill or `samples/skills/host-ssh skills/README.md` on the repo. Missing
`GA_HOST_SSH_*` env vars → stop and tell the user to set them in the guide editor; do
not scan or guess credentials.

## Machines — the guide Environment is the source of truth

`GA_SSH_MACHINES` (JSON array in the guide Environment) declares which machines this
guide can reach. **Run `host_ssh.py machines` to list them** — never guess a host or IP.
When no `--machine` is given, the record with `"default": true` is the target. When
`GA_SSH_MACHINES` is absent, the legacy single machine from `GA_HOST_SSH_HOST` is the
default (named `host`).

```json
[
  {"name": "office", "host": "host.docker.internal", "default": true, "os": "windows",
   "share": {"unc": "\\\\FILESERVER\\content", "user": "DOMAIN\\GuideAnts", "drive": "R"},
   "wsl": {"distro": "Ubuntu"}},
  {"name": "laptop", "host": "192.168.1.60", "os": "mac"},
  {"name": "gpu", "host": "192.168.1.50", "os": "linux"}
]
```

A `share` block (optional, per machine) makes `host_ssh.py` re-map the drive on every
run, because **mapped drive letters in the desktop session do not carry into SSH
sessions** (Windows isolates sessions). Use the mapped drive (default **`R:`**) in
commands; the skill re-maps it from `share.unc` each time. `share.password`, when
present, is a secret; it falls back to `GA_HOST_SSH_SHARE_PASSWORD`, then
`GA_HOST_SSH_PASSWORD`.

## Shell selection (per machine record)

Dispatch priority: an explicit `--shell` override, then the record:

| Record | Remote command style |
|---|---|
| `"wsl": {"distro": "Ubuntu"}` | **bash inside the WSL distro** — `wsl.exe -d <distro> -- bash -c "<harness>"` over the existing Windows SSH session (no second sshd, same account) |
| `"os": "windows"` (declared) | `powershell.exe -NoProfile -NonInteractive -EncodedCommand <b64>` (UTF-16LE base64, + share bootstrap) |
| `"os": "mac"` / `"linux"` (declared) | plain POSIX shell text via the login shell (zsh on macOS) — no encoding, no share bootstrap (SMB drive mapping is Windows-only) |
| no `wsl`, no `os` | OS **auto-detected once** (`uname -s`; empty output means Windows) and cached in `/tmp/ga_host_ssh_os_cache.json` for 10 minutes, then the matching style above |

A `wsl` block wins over `os` (it is more specific: a Windows machine running bash in a
distro). Aliases: `win`, `macos`, `darwin`, `posix`, `unix`. Delete the cache file to
force re-detection. Hosts resolve **IPv4-first** (AF_INET lookup / `ssh -4`) because
`host.docker.internal` can resolve to an unroutable IPv6 address from the sandbox.

macOS targets need **Remote Login** enabled (System Settings → General → Sharing →
Remote Login), plus **"Allow full disk access for remote users"** so SSH can read
Desktop/Documents/Downloads; otherwise macOS TCC blocks those reads (`probe` reports
`documents: DENIED`). Apple's guide: [Turn on Remote Login]
(https://support.apple.com/guide/mac-help/turn-on-remote-login-mchlp1066/mac)

When sshpass cannot be installed (no root in the sandbox), use the paramiko
variant — identical CLI, pure-Python SSH from `pylibs/`:

```bash
PYTHONPATH=pylibs:Skills/host-ssh/scripts python3 Skills/host-ssh/scripts/host_ssh_pylibs.py probe --all
```

## Capability profile (verified 2026-09-01) — read before choosing commands

The SSH account (`GA_HOST_SSH_USER`) is a **standard (non-admin) user**. On both
current machines the following are **denied for it — by design, not a
regression. Do not use these, and do not burn round-trips discovering this**:

| Probe | Result |
|-------|--------|
| `Get-CimInstance Win32_*` (WMI — e.g. `LastBootUpTime` boot time) | **Access denied** |
| `Get-Volume` | **Access denied** |
| `HKLM` registry read | **Access denied** |
| `systeminfo` | **Access denied** (errors to stderr) |
| Filesystem / `Get-PSDrive -PSProvider FileSystem` | ok |
| `dotnet`, `docker` | ok on both |
| `nvidia-smi` | ok on OfficeDesktop, **absent on Max** |

(Windows PowerShell targets. WSL/POSIX targets: the Windows capability table does
not apply inside the distro; `probe` reports their fs/tool state directly.)

Consequences:

- **Boot time is NOT readable** through this account. The canonical
  post-restart verification is the **`probe`** command below — reachability +
  identity + clock — not `LastBootUpTime` / `systeminfo`.
- Prefer `Get-PSDrive -PSProvider FileSystem` over `Get-Volume` for drives.
- If a command fails, check this table first; re-run `probe` to refresh the
  profile before concluding anything changed.

One-command verification (read-only, non-admin-safe) — proves SSH auth,
machine, account, and clock, and reports tool availability:

```bash
python3 Skills/host-ssh/scripts/host_ssh.py probe --all
```

`probe` never fails on the denied capabilities above. If a line shows
`remote shell OK (command error: …)` the SSH transport itself worked — the
problem is in the command text (e.g. a bash idiom like `hostname` in a
PowerShell target, or a Windows idiom in a WSL/POSIX target).

## When to use SSH vs the workspace

| Need | Use |
|------|-----|
| Read/write files **already in the file list** (e.g. `../repo/src/…`) | **Bash in sandbox** — heredoc writes, `sed`, etc. Same bytes as the host when path is a host mount. |
| `dotnet`, `npm test`, Playwright, host processes, drives, services | **This skill** — `host_ssh.py` over SSH |
| Host HTTP | `http://host.docker.internal:…` directly |
| Host files **not** in the file list | Host folder mount (GuideAnts UI) or ask the user |

**Default:** file edits on a mounted repo = sandbox heredoc. SSH = run commands on the
machine OS, not to patch source files.

## Run bash via WSL (when the record has "wsl")

A machine record with `"wsl": {"distro": "..."}` runs **bash inside that WSL
distro** for both `run` and `probe`. The script is base64-encoded over SSH -
the bash equivalent of `-EncodedCommand` - so quotes, newlines and heredocs
survive; **always use `run -` + heredoc** as usual:

```bash
python3 Skills/host-ssh/scripts/host_ssh.py run - <<'SH'
set -euo pipefail
cd /mnt/d/repos/repo
dotnet build Server/Server.csproj -v q --nologo
SH
```

Inside the distro:

- Windows drives mount at **`/mnt/c`, `/mnt/d`, ...** - use those paths, not `C:\`.
- The `share` bootstrap (`net use` / drive letter) does **not** run in WSL
  mode; reach network shares by UNC (e.g. `\\FILESERVER\content`) or mount
  them inside the distro.
- You are a regular Linux user inside the distro (typically with `sudo` per
  the distro's policy) - the Windows capability table above does **not**
  apply. Windows tools (dotnet, node, git, ...) are reachable through the
  bridged Windows PATH where installed.
- `probe` on a WSL machine runs the same checks in bash; its `machine:` line
  shows the distro's hostname, not the Windows name.

## Run PowerShell on a machine (windows, no "wsl")

Scripts send PowerShell via **`-EncodedCommand`** (pipes and quotes survive Windows
OpenSSH). **Always use `run -` + bash heredoc** for anything non-trivial — never inline
Python, never nested quote gymnastics, never hand-rolled `ssh`/`sshpass`.

```bash
python3 Skills/host-ssh/scripts/host_ssh.py run - <<'PS'
Set-Location C:\repos\repo\src\server
dotnet build RepoApi\RepoApi.csproj -v q --nologo
PS
```

On mac/linux targets, write POSIX shell instead (same heredoc pattern):

```bash
python3 Skills/host-ssh/scripts/host_ssh.py run --machine laptop - <<'SH'
ls -lah ~/Documents
SH
```

Target a specific machine (names from `machines`):

```bash
python3 Skills/host-ssh/scripts/host_ssh.py run --machine gpu - <<'PS'
Get-PSDrive -PSProvider FileSystem |
  Select-Object Name, Root, Used, Free |
  Format-Table -AutoSize
PS
```

List drives (non-admin users often cannot use `Get-Volume`):

```bash
python3 Skills/host-ssh/scripts/host_ssh.py run - <<'PS'
Get-PSDrive -PSProvider FileSystem |
  Select-Object Name, Root, Used, Free |
  Format-Table -AutoSize
PS
```

Save remote stdout to a file in sandbox CWD:

```bash
python3 Skills/host-ssh/scripts/host_ssh.py run - -o drives.json <<'PS'
Get-PSDrive -PSProvider FileSystem | ConvertTo-Json -Depth 3
PS
```

Ignore `#< CLIXML` in stderr — harmless PowerShell remoting progress, not failure.

## Edit files on a mounted host repo (bash heredoc, not Python)

When the file list shows a mount (e.g. `../repo/src/…`), **patch with bash
heredoc** so backslashes and quotes are not mangled by the tool layer:

```bash
cat > ../repo/src/example.txt <<'XEOF'
line one
line two
XEOF
```

For a partial edit, use `sed` or write a small `.sh` / `.py` **via heredoc** and run it
once — do **not** pass Windows paths containing backslashes through `run_python` string
arguments (the platform collapses double backslashes and corrupts the file).

## Required Environment (guide → sandbox)

| Variable | Purpose |
|----------|---------|
| `GA_HOST_SSH_USER` | Local account on each machine (e.g. `GuideAnts`); on a Mac: the account short name |
| `GA_HOST_SSH_PASSWORD` | Password (**secret** in guide editor) |
| `GA_SSH_MACHINES` | JSON machine registry (see above) |

Legacy single machine (used when `GA_SSH_MACHINES` is absent): `GA_HOST_SSH_HOST`
(usually `host.docker.internal`) and optional `GA_HOST_SSH_PORT` (default `22`).

Legacy share variables (default machine only):

| Variable | Purpose |
|----------|---------|
| `GA_HOST_SSH_SHARE_UNC` | UNC behind `R:` (e.g. `\\FILESERVER\content`) |
| `GA_HOST_SSH_SHARE_USER` | Share account (e.g. `DOMAIN\GuideAnts`) |
| `GA_HOST_SSH_SHARE_PASSWORD` | Share password (**secret**); defaults to `GA_HOST_SSH_PASSWORD` |
| `GA_HOST_SSH_SHARE_DRIVE` | Drive letter (default **`R`**) |

Quick check (optional, one line — **not** a gate before work):

```bash
test -n "$GA_HOST_SSH_USER" && test -n "$GA_HOST_SSH_PASSWORD" && echo env ok || echo env missing
```

If SSH fails, report the error verbatim and point the operator at README troubleshooting.

## Rules

- **Trust the file list.** No path discovery commands.
- **Know the machines.** `host_ssh.py machines` — do not invent hosts or IPs.
- **Never `cd`** in the same script as `Skills/…` paths.
- **Heredoc first** — `<<'SH'` bash on wsl/posix machines, `<<'PS'` PowerShell on
  windows, and for file writes on mounts.
- **Never** print or log `GA_HOST_SSH_PASSWORD` (or any share password).
- **Probe, do not guess capabilities.** `host_ssh.py probe --all` reports what the
  account can actually do (the denied set is by design; see the capability table).
- **Declare `wsl`/`os` when you know them.** `wsl` → bash in the distro; `os` →
  PowerShell vs POSIX shell; auto-detection caches in `/tmp` — delete the cache file
  after a machine's OS changes.
- **Never** run `preflight.py` unless the user explicitly asks for diagnostics — it is
  not part of the normal workflow.

## Reporting

State which machine ran, what ran, whether it succeeded, and a short summary. If
blocked on missing env, unknown machine, or SSH auth, say so clearly and stop.
