# Host SSH skills

GuideAnts sandbox skills that run **PowerShell on a machine over SSH**.

Use this when a task needs the real Windows (or Linux) machine — `dotnet`,
Playwright, listing drives, host processes — not file I/O on paths already in
the workspace file list (use sandbox + host mounts for those).

**Default path:** password auth via guide Environment variables (no SSH keys in
the sandbox). Each machine runs **OpenSSH Server**; the sandbox reaches the
Docker host at `host.docker.internal` (Docker Desktop on Windows) and other LAN
machines by their LAN IP.

For **host file read/write** without shell access, prefer [host folder
mounts](../../../docs/host-folder-mounts.md) instead of SSH.

## Skills

| Skill | What it does |
|-------|--------------|
| [`host-ssh`](host-ssh/) | `host_ssh.py` — PowerShell on a machine via SSH (heredoc-first) |

## Required Environment (guide → sandbox)

Set these on the **Creative Guide** (or crew member) in the guide editor →
**Environment variables**. Mark the password **secret**.

| Variable | Example | Secret |
|----------|---------|--------|
| `GA_HOST_SSH_USER` | `GuideAnts` | No |
| `GA_HOST_SSH_PASSWORD` | *(machine account password)* | **Yes** |
| `GA_SSH_MACHINES` | *(see below)* | No |

### Machine registry (`GA_SSH_MACHINES`)

A JSON array describing every machine this guide may reach. The record with
`"default": true` is used when a command omits `--machine`. When the variable
is absent, the legacy `GA_HOST_SSH_HOST` single machine is used as the default.

```json
[
  {"name": "office", "host": "host.docker.internal", "default": true,
   "share": {"unc": "\\\\FILESERVER\\content", "user": "DOMAIN\\GuideAnts", "drive": "R"}},
  {"name": "gpu", "host": "192.168.1.50"}
]
```

Fields:

- `name` — short id used with `--machine` (letters, digits, `-`, `_`)
- `host` — `host.docker.internal` for the Docker host, or a LAN IP for other machines
- `port` — optional, default `22`
- `default` — mark exactly one
- `share` — optional; re-map a network share on each SSH run (see below)

**One account, many machines:** create the same local user (e.g. `GuideAnts`,
non-admin) on every machine you want reachable. The single
`GA_HOST_SSH_USER`/`GA_HOST_SSH_PASSWORD` pair then authenticates to all of
them.

### Share access (optional, per machine)

**Mapped drive letters do not appear in SSH sessions** — Windows isolates
sessions. Your desktop `R:` does not carry over. A `share` block makes
`host_ssh.py` reconnect the UNC and map the drive (default **`R:`**) on every
run; agents use paths under **`R:\`** on that machine.

```json
"share": {"unc": "\\\\FILESERVER\\content", "user": "DOMAIN\\GuideAnts", "drive": "R"}
```

- On the machine that **hosts the share**, grant the account access to the
  share folder (SMB share permission + NTFS/`icacls`).
- `share.password` (**secret**) is optional; it falls back to
  `GA_HOST_SSH_SHARE_PASSWORD`, then `GA_HOST_SSH_PASSWORD`.
- UNC values are normalized to canonical form; connect/map failures are
  reported on stdout (system error 67 = bad UNC, 71 = SMB session table full
  on the machine) instead of being swallowed.

Legacy single-machine share variables (used only when `GA_SSH_MACHINES` is
absent):

| Variable | Default | Purpose |
|----------|---------|---------|
| `GA_HOST_SSH_HOST` | `host.docker.internal` | Legacy default machine host |
| `GA_HOST_SSH_PORT` | `22` | Legacy SSH port |
| `GA_HOST_SSH_SHARE_UNC` | *(none)* | UNC root for network content (e.g. `\\FILESERVER\\content` — your desktop `R:` target) |
| `GA_HOST_SSH_SHARE_USER` | *(none)* | Share account (e.g. `DOMAIN\\GuideAnts`) |
| `GA_HOST_SSH_SHARE_PASSWORD` | *(none)* | Share password (**secret**); falls back to `GA_HOST_SSH_PASSWORD` |
| `GA_HOST_SSH_SHARE_DRIVE` | `R` | Maps the drive to the UNC on each SSH run |

## Windows machine setup (one-time, per machine)

Run on each **machine** (Docker host or other) as an administrator. The steps
below are identical for every machine; OpenSSH on port 22, a **non-admin**
local user, password auth enabled.

### 1. Create the local user (if needed)

```powershell
$UserName = 'GuideAnts'
New-LocalUser -Name $UserName -Password (Read-Host 'Password' -AsSecureString) `
  -FullName 'GuideAnts sandbox host access' -PasswordNeverExpires
Add-LocalGroupMember -Group 'Users' -Member $UserName
# Do NOT add to Administrators.
```

If you need `docker` access on this machine, add the user to the local
`docker-users` group (run once as admin):

```powershell
New-LocalGroup -Name 'docker-users' -Description 'Docker users' -ErrorAction SilentlyContinue | Out-Null
Add-LocalGroupMember -Group 'docker-users' -Member $UserName
# restart the Docker service so the ACL takes effect, or have the user log in again
```

### 2. Install and configure OpenSSH Server

Copy [`setup-windows-host.ps1`](setup-windows-host.ps1) to the machine and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\setup-windows-host.ps1
```

Or run the commands in that file manually. It:

- Installs **OpenSSH Server** (Windows Capability)
- Starts `sshd` and opens firewall port 22
- Sets `AllowUsers GuideAnts`, `PasswordAuthentication yes`, `StrictModes no`
- Inserts settings **before** the `Match Group administrators` block (required)

### 2b. Network share access (if this machine should map a network drive)

Prerequisite on the machine that **hosts the share** (e.g. server `repos` →
`C:\repos`):

```powershell
Grant-SmbShareAccess -Name repos -AccountName 'GuideAnts' -AccessRight Change -Force
icacls 'C:\repos' /grant 'GuideAnts:(OI)(CI)M'
```

On the **SSH machine**, store share credentials for the sandbox user:

```powershell
powershell -ExecutionPolicy Bypass -File .\setup-guideants-share-access.ps1 `
  -ShareUnc '\\FILESERVER\\repos' -ShareUser 'DOMAIN\\GuideAnts'
```

Then add the `share` block to the machine's record in `GA_SSH_MACHINES` (or
the legacy `GA_HOST_SSH_SHARE_*` variables for the single-machine setup).
`host_ssh.py` reconnects the UNC before each command and maps **`R:`**.

**Container verify** (after guide env is set, or pass passwords explicitly):

```powershell
powershell -ExecutionPolicy Bypass -File .\verify-from-container.ps1 `
  -SshPassword '<GuideAnts SSH password>' `
  -SharePassword '<share password>' `
  -ShareUnc '\\FILESERVER\\repos' -ShareUser 'DOMAIN\\GuideAnts'
```

Expect `True` for `R:\` (root of the mapped drive).

### 3. Test from the machine (as your admin account)

```powershell
ssh GuideAnts@localhost
# enter the GuideAnts password — should land in cmd or PowerShell
hostname
exit
```

Optional PowerShell one-liner:

```powershell
ssh GuideAnts@localhost powershell.exe -NoProfile -Command "Get-PSDrive -PSProvider FileSystem"
```

### 4. Wire the guide Environment

Add `GA_HOST_SSH_USER`, `GA_HOST_SSH_PASSWORD`, and the machine to
`GA_SSH_MACHINES` (see above). For other LAN machines, use their **LAN IP**
as `host` — the sandbox can route to the LAN through the Docker host's NAT,
but cannot resolve machine names, so prefer IPs.

Sync/copy the skill into the notebook's `Skills/` tree if needed.

### 5. Test from the sandbox

In a notebook conversation (from sandbox CWD — do not `cd` first):

```bash
python3 Skills/host-ssh/scripts/host_ssh.py machines
python3 Skills/host-ssh/scripts/host_ssh.py run - <<'PS'
hostname
PS
python3 Skills/host-ssh/scripts/host_ssh.py run --machine gpu - <<'PS'
hostname
PS
```

## Operator diagnostics (optional)

`preflight.py` is for **operators debugging setup**, not for the agent to run
every turn. It loops every machine in the registry:

```bash
python3 Skills/host-ssh/scripts/preflight.py --for probe
```

## Linux host (brief)

If a machine is Linux instead of Windows:

```bash
sudo apt-get install -y openssh-server
sudo useradd -m -s /bin/bash guideants
sudo passwd guideants
```

Point the machine's `host` at its address (`host.docker.internal` for the
Docker host, LAN IP otherwise). Use `host_ssh.py run` with bash commands
instead of PowerShell, or extend the skill for your shell.

## Security notes

- Use a **dedicated non-admin** account (`GuideAnts`) with only the access you
  need.
- Mark `GA_HOST_SSH_PASSWORD` as a **secret** in the guide editor.
- Keep SSH on the LAN/Docker-internal path; do not expose port 22 to the
  internet without keys, firewall rules, and hardening.
- Prefer **host folder mounts** when the task is file I/O only; use SSH when
  you need OS-level commands on the machine.

## Troubleshooting

| Symptom | Likely cause |
|---------|----------------|
| `env missing` | Guide Environment variables not set |
| `unknown machine` | `--machine` value not in `GA_SSH_MACHINES` (run `machines`) |
| `GA_SSH_MACHINES is not valid JSON` | Check the registry — JSON escaping of backslashes is the usual culprit |
| Connection refused | `sshd` not running or firewall blocking 22 |
| Permission denied (password) | Wrong password or user not in `AllowUsers` |
| `sshd` won't start after config edit | Settings placed **inside** a `Match` block — re-run `setup-windows-host.ps1` |
| Works on host, fails from container | Wrong `host`; try `host.docker.internal` for the Docker host |
| Other machine resolves as name but not from sandbox | Use the LAN **IP** — the sandbox cannot resolve machine names |
| `'Select-Object' is not recognized` | Old skill build — re-sync; scripts must use `-EncodedCommand` |
| `Get-Volume` / CIM access denied | Non-admin machine user — use `Get-PSDrive`, `Win32_LogicalDisk`, or `vol` |
| Agent runs `cd /home` then fails | Agent error — CWD is already Output; paths are in the file list |
| Windows file corrupted after sandbox edit | Used `run_python` with `\` in strings — use bash heredoc on mounts instead |
| `R:` missing over SSH (only C/D/E) | No `share` block / legacy share vars for that machine — add `share.unc` + creds |
| `[host-ssh share] share connect failed: System error 67` | UNC wrong (server/share name) — check `share.unc` |
| `[host-ssh share] … System error 71` | SMB session table full on the machine — restart the Workstation service on that machine |
| `docker` works on desktop but not over SSH | User not in local `docker-users` group (see step 1) |

## Common rules (agents)

- Trust the **file list** in context; do not probe paths.
- Run `host_ssh.py` from CWD with `Skills/host-ssh/…` — never `cd` first.
- Use `machines` to discover targets; use `--machine` to pick one.
- Use **heredoc** for SSH PowerShell and for editing mounted repo files.
- Do not echo or log `GA_HOST_SSH_PASSWORD`.
