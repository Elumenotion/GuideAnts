#!/usr/bin/env python3
"""Shared SSH + remote-command helpers for host-ssh skill scripts.

Single machine (legacy): GA_HOST_SSH_HOST / _USER / _PASSWORD (+ GA_HOST_SSH_PORT).
Multi machine: GA_SSH_MACHINES is a JSON array of machine records; the record
with "default": true is the target when no --machine is given. The guide
Environment is the source of truth for which machines exist.

    [
      {"name": "office", "host": "host.docker.internal", "default": true,
       "os": "windows",
       "share": {"unc": "\\\\FILESERVER\\content", "user": "DOMAIN\\GuideAnts", "drive": "R"},
       "wsl": {"distro": "Ubuntu"}},
      {"name": "laptop", "host": "192.168.1.60", "os": "mac"},
      {"name": "gpu", "host": "192.168.1.50", "os": "linux"}
    ]

Three remote backends, picked per machine record:

- "wsl" block (Windows machines): bash inside the named WSL distro, via
  `wsl.exe -d <distro> -- bash -c "<harness>"` on the existing Windows SSH
  session. The script rides in a base64 blob so quoting survives the
  Windows remote-command line; see run_remote_bash for the rules.
- "os": "windows" (declared, or auto-detected): PowerShell via
  powershell.exe -EncodedCommand (UTF-16LE base64, + share bootstrap).
- "os": "mac" / "linux": the script text is sent verbatim to the account's
  login shell (zsh on macOS) - plain POSIX shell, no encoding, no share
  bootstrap (SMB drive mapping is Windows-only).

Per-machine optional "os": "windows" | "mac" | "linux" (aliases: win, macos,
darwin, posix, unix) selects the non-WSL backend explicitly; when "os" is
absent it is auto-detected once via `uname -s` and cached in /tmp for 10
minutes (empty output -> windows). A "wsl" block wins over "os" (it is more
specific: a Windows machine running bash in a distro). Hosts resolve
IPv4-first (host.docker.internal can be an unroutable IPv6 from the sandbox).

Share passwords stay in guide Environment secrets (GA_HOST_SSH_SHARE_PASSWORD);
a per-machine "share.password" field is supported but discouraged.
"""
from __future__ import annotations

import base64
import json
import os
import re
import secrets
import shutil
import socket
import subprocess
import time

MACHINES_ENV = "GA_SSH_MACHINES"
_MACHINE_RE = re.compile(r"^[a-zA-Z0-9][a-zA-Z0-9_-]*$")
OS_ALIASES = {
    'windows': 'windows', 'win': 'windows',
    'mac': 'mac', 'macos': 'mac', 'darwin': 'mac',
    'linux': 'linux', 'posix': 'linux', 'unix': 'linux',
}
DETECT_SNIPPET = 'uname -s'
OS_CACHE = '/tmp/ga_host_ssh_os_cache.json'
OS_CACHE_TTL = 600  # seconds


def env(name: str) -> str:
    return os.environ.get(name, "").strip()


def ensure_sshpass() -> str:
    path = shutil.which('sshpass')
    if path:
        return path
    if not shutil.which('apt-get'):
        raise RuntimeError("sshpass not found and apt-get unavailable in this sandbox")
    subprocess.run(['apt-get', 'update', '-qq'], check=False, capture_output=True)
    proc = subprocess.run(['apt-get', 'install', '-y', '-qq', 'sshpass'],
                          capture_output=True, text=True, check=False)
    if proc.returncode != 0:
        raise RuntimeError("failed to install sshpass: "
                           + (proc.stderr or proc.stdout or 'unknown error')[:500])
    path = shutil.which('sshpass')
    if not path:
        raise RuntimeError("sshpass install reported success but binary not found")
    return path


def encode_powershell_command(command: str) -> str:
    """UTF-16LE base64 for powershell.exe -EncodedCommand."""
    return base64.b64encode(command.encode('utf-16-le')).decode('ascii')


def normalize_unc(value: str) -> str:
    """Canonicalize a stored UNC to its canonical form (two leading, one inner bs).

    Values may arrive double-escaped (JSON / PowerShell heredocs give 4 or
    more leading backslashes) or under-escaped (a single leading backslash).
    net use rejects both (system error 67: network name cannot be found).
    Collapse the leading run to exactly two and every inner run to exactly
    one; local paths pass through untouched.
    """
    bs = chr(92)
    s = value.strip()
    if not s.startswith(bs):
        return s
    body = s.lstrip(bs)
    if bs not in body:
        return bs + bs + body
    server, rest = body.split(bs, 1)
    rest = rest.lstrip(bs)
    out = []
    prev_bs = False
    for ch in rest:
        if ch == bs:
            if prev_bs:
                continue
            prev_bs = True
        else:
            prev_bs = False
        out.append(ch)
    return bs + bs + server + bs + ''.join(out)


def parse_machines() -> list[dict]:
    """Parse GA_SSH_MACHINES into normalized records; [] when unset.

    Raises ValueError on malformed JSON or invalid records so the CLI can
    report the problem clearly.
    """
    raw = env(MACHINES_ENV)
    if not raw:
        return []
    try:
        data = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ValueError(f"{MACHINES_ENV} is not valid JSON: {exc}") from exc
    if not isinstance(data, list) or not data:
        raise ValueError(f"{MACHINES_ENV} must be a non-empty JSON array")
    machines: list[dict] = []
    for i, entry in enumerate(data):
        if not isinstance(entry, dict):
            raise ValueError(f"{MACHINES_ENV}[{i}] must be an object")
        name = str(entry.get('name', '')).strip()
        host = str(entry.get('host', '')).strip()
        if not name or not host:
            raise ValueError(f"{MACHINES_ENV}[{i}] needs 'name' and 'host'")
        if not _MACHINE_RE.match(name):
            raise ValueError(f"{MACHINES_ENV}[{i}].name must match {_MACHINE_RE.pattern}")
        port = entry.get('port')
        if port is not None:
            port = int(port)
        os_name = str(entry.get('os', '')).strip().lower()
        if os_name:
            if os_name not in OS_ALIASES:
                raise ValueError(f"{MACHINES_ENV}[{i}].os must be one of "
                                 f"windows/mac/linux (got {os_name!r})")
            os_name = OS_ALIASES[os_name]
        else:
            os_name = None
        wsl_raw = entry.get('wsl')
        if wsl_raw is not None:
            if not isinstance(wsl_raw, dict) or not str(wsl_raw.get('distro') or '').strip():
                raise ValueError(f"{MACHINES_ENV}[{i}].wsl needs a 'distro'")
            wsl = {'distro': str(wsl_raw['distro']).strip()}
        else:
            wsl = None
        share = entry.get('share') or None
        if share is not None:
            if not isinstance(share, dict) or not str(share.get('unc', '')).strip():
                raise ValueError(f"{MACHINES_ENV}[{i}].share needs a 'unc'")
            share = {
                'unc': normalize_unc(str(share['unc']).strip()),
                'user': str(share.get('user', '')).strip(),
                'password': (str(share.get('password', '')).strip()
                             or env('GA_HOST_SSH_SHARE_PASSWORD')
                             or env('GA_HOST_SSH_PASSWORD')),
                'drive': str(share.get('drive', 'R')).strip().upper() or 'R',
            }
        machines.append({'name': name, 'host': host, 'port': port, 'os': os_name,
                         'default': bool(entry.get('default', False)),
                         'wsl': wsl, 'share': share})
    if not any(m['default'] for m in machines):
        machines[0]['default'] = True
    return machines


def get_machine(name: str | None = None) -> tuple[dict, dict | None]:
    """Resolve (machine, share). Falls back to the legacy single machine."""
    machines = parse_machines()
    if machines:
        if name:
            for m in machines:
                if m['name'].lower() == name.lower():
                    return m, m['share']
            available = ', '.join(m['name'] for m in machines)
            raise ValueError(f"unknown machine '{name}' (GA_SSH_MACHINES has: {available})")
        for m in machines:
            if m['default']:
                return m, m['share']
        raise ValueError("GA_SSH_MACHINES has no 'default': true entry")
    host = env('GA_HOST_SSH_HOST') or 'host.docker.internal'
    m = {'name': 'host', 'host': host,
         'port': int(env('GA_HOST_SSH_PORT') or '22'),
         'os': None, 'default': True, 'wsl': None, 'share': None}
    unc = normalize_unc(env('GA_HOST_SSH_SHARE_UNC'))
    if unc:
        m['share'] = {
            'unc': unc,
            'user': env('GA_HOST_SSH_SHARE_USER'),
            'password': env('GA_HOST_SSH_SHARE_PASSWORD') or env('GA_HOST_SSH_PASSWORD'),
            'drive': (env('GA_HOST_SSH_SHARE_DRIVE') or 'R').upper(),
        }
    return m, m['share']


def require_ssh_config() -> tuple[str, str]:
    """(user, password) from guide env; raises RuntimeError when missing."""
    user = env('GA_HOST_SSH_USER')
    password = env('GA_HOST_SSH_PASSWORD')
    missing = [k for k, v in (('GA_HOST_SSH_USER', user), ('GA_HOST_SSH_PASSWORD', password)) if not v]
    if missing:
        raise RuntimeError('Missing guide Environment: ' + ', '.join(missing)
                           + '. See host-ssh README for setup.')
    return user, password


def resolve_ipv4(host: str) -> str:
    """Prefer an IPv4 address; host.docker.internal often resolves to an
    unroutable IPv6 (fdc4:...) from the sandbox."""
    try:
        infos = socket.getaddrinfo(host, None, socket.AF_INET, socket.SOCK_STREAM)
        return infos[0][4][0]
    except socket.gaierror:
        return host


def classify_uname(uname_out: str) -> str:
    """Map `uname -s` output to windows/mac/linux.

    Darwin -> mac; Linux -> linux; anything else (empty output on Windows
    cmd/PowerShell, MINGW* from Git-Bash-as-shell) -> windows."""
    u = (uname_out or '').strip().upper()
    if u.startswith('DARWIN'):
        return 'mac'
    if u.startswith('LINUX'):
        return 'linux'
    return 'windows'


def _os_cache_read() -> dict:
    try:
        with open(OS_CACHE, encoding='utf-8') as h:
            data = json.load(h)
        return data if isinstance(data, dict) else {}
    except Exception:
        return {}


def _os_cache_write(data: dict) -> None:
    try:
        with open(OS_CACHE, 'w', encoding='utf-8') as h:
            json.dump(data, h)
    except Exception:
        pass


def os_cache_get(key: str) -> str | None:
    entry = _os_cache_read().get(key)
    if not isinstance(entry, dict):
        return None
    if time.time() - float(entry.get('ts', 0)) > OS_CACHE_TTL:
        return None
    os_name = entry.get('os')
    return os_name if os_name in ('windows', 'mac', 'linux') else None


def os_cache_put(key: str, os_name: str) -> None:
    data = _os_cache_read()
    data[key] = {'os': os_name, 'ts': time.time()}
    _os_cache_write(data)


def resolve_target_os(machine: str | None = None, detect=None) -> str:
    """windows/mac/linux for a machine name.

    Priority: declared per-machine 'os' field, then the /tmp cache, then the
    supplied detect(target_dict) callable, else the legacy 'windows' default.
    """
    target, _share = get_machine(machine)
    declared = target.get('os')
    if declared:
        return declared
    key = target.get('name') or target['host']
    cached = os_cache_get(key)
    if cached:
        return cached
    if detect is not None:
        try:
            detected = detect(target)
        except Exception:
            detected = None
        if detected:
            os_cache_put(key, detected)
            return detected
    return 'windows'


WINPATH_PREAMBLE = r'''
# host-ssh: bridge the Windows environment into this WSL session.
# (a) PATH: Windows entries remapped to /mnt/<letter>/... (PE binaries run via
#     interop). (b) shims: Windows tools reachable only as name.exe / name.cmd
#     get tiny shims (in ~/.ga/shims, appended to PATH so native tools win).
if [ -z "${_GA_WINPATH:-}" ] && command -v cmd.exe >/dev/null 2>&1; then
  _GA_WINPATH="$(cmd.exe /c 'echo %PATH%' 2>/dev/null | tr -d '\r' | tail -n1 | tr ';' '\n' \
    | sed -e 's/^\([A-Za-z]\):\\/\/mnt\/\L1\//' -e 's/\\/\//g' | paste -sd: -)"
  if [ -n "$_GA_WINPATH" ]; then
    case ":$PATH:" in
      *":$_GA_WINPATH:"*) ;;
      *) PATH="$PATH:$_GA_WINPATH" ;;
    esac
  fi
fi
if [ -n "${_GA_WINPATH:-}" ] && [ -n "${HOME:-}" ]; then
  _GA_SHIM="${HOME}/.ga/shims"
  mkdir -p "$_GA_SHIM" 2>/dev/null
  for tool in dotnet node npm npx python python3 pip pip3 gh az pwsh git; do
    command -v "$tool" >/dev/null 2>&1 && continue   # native tool wins
    _found=""
    IFS=':' read -r -a _dirs <<< "$_GA_WINPATH"
    for _d in "${_dirs[@]}"; do
      for _ext in .exe .cmd .bat; do
        if [ -e "$_d/$tool$_ext" ]; then _found="$_d/$tool$_ext"; break 2; fi
      done
    done
    [ -n "$_found" ] || continue
    case "$_found" in
      *.exe)
        printf '#!/bin/sh\nexec "%s" "$@"\n' "$_found" > "$_GA_SHIM/$tool"
        ;;
      *)
        _wp="${_found//\//\\}"
        printf '#!/bin/sh\nexec cmd.exe /c /d "%s" "$@"\n' "$_wp" > "$_GA_SHIM/$tool"
        ;;
    esac
    chmod +x "$_GA_SHIM/$tool" 2>/dev/null
  done
  case ":$PATH:" in
    *":$_GA_SHIM:"*) ;;
    *) PATH="$_GA_SHIM:$PATH" ;;
  esac
fi
'''


def _distro_from(wsl: dict | None) -> str | None:
    """WSL distro from a machine record's "wsl" block, or None."""
    if not wsl:
        return None
    distro = str(wsl.get('distro') or '').strip()
    return distro or None


def _ssh_cmd_prefix(target: dict, user: str, port: int) -> list[str]:
    """sshpass + ssh argument prefix shared by all backends (identical transport).

    IPv4-first: host.docker.internal can resolve to an unroutable IPv6 from
    the sandbox, so resolve_ipv4() + `ssh -4`.
    """
    return [ensure_sshpass(), '-e', 'ssh', '-4', '-p', str(port),
            '-o', 'StrictHostKeyChecking=no',
            '-o', 'PreferredAuthentications=password',
            '-o', 'PubkeyAuthentication=no',
            '-o', 'ConnectTimeout=15',
            f"{user}@{resolve_ipv4(target['host'])}"]


def _run_remote(remote_cmd: str, user: str, target: dict, password: str,
                timeout: int) -> subprocess.CompletedProcess[str]:
    cmd = _ssh_cmd_prefix(target, user, int(target.get('port') or 22)) + [remote_cmd]
    env_vars = os.environ.copy()
    env_vars['SSHPASS'] = password
    return subprocess.run(cmd, env=env_vars, capture_output=True, text=True,
                          timeout=timeout, check=False)


def compose_powershell(script: str, share: dict | None) -> str:
    """Windows backend: UTF-16LE base64 powershell.exe -EncodedCommand, with
    the share bootstrap when the record has a share block."""
    return ('powershell.exe -NoProfile -NonInteractive -EncodedCommand '
            + encode_powershell_command(
                wrap_powershell_with_share_bootstrap(script, share)))


def compose_posix(script: str) -> str:
    """mac/linux backend: the script text verbatim (login shell = zsh/bash)."""
    return script


def compose_remote_command(script: str, remote_os: str, share: dict | None) -> str:
    """OS-only dispatch (no WSL): powershell.exe -EncodedCommand (+ share
    bootstrap) for windows; raw POSIX shell text for mac/linux. Used by the
    paramiko variant and kept for backward compatibility."""
    if remote_os in ('mac', 'linux'):
        return compose_posix(script)
    return compose_powershell(script, share)


def wrap_powershell_with_share_bootstrap(powershell_script: str, share: dict | None) -> str:
    """Reconnect the UNC share in each SSH session (drive letters never carry over).

    net use output is captured and the first error line is echoed when the
    connect fails (system error 67 = bad UNC, 71 = SMB session table full) -
    never silently swallowed.
    """
    if not share:
        return powershell_script
    unc = share['unc']
    user = (share.get('user') or '').strip()
    password = (share.get('password') or '').strip()
    unc_ps = unc.replace("'", "''")
    user_ps = user.replace("'", "''")
    pass_ps = password.replace("'", "''")
    if user_ps and pass_ps:
        use_cmd = f"net use $__gaUnc /user:{user_ps} '{pass_ps}'"
    elif user_ps:
        use_cmd = f"net use $__gaUnc /user:{user_ps}"
    else:
        use_cmd = 'net use $__gaUnc'
    drive = (share.get('drive') or 'R').strip().upper()
    if not (len(drive) == 1 and drive.isalpha()):
        drive = 'R'
    lines = [
        f"$__gaUnc = '{unc_ps}'",
        "if (-not (Test-Path -LiteralPath $__gaUnc)) {",
        f"  $__gaOut = {use_cmd} 2>&1 | Out-String",
        "  if ($LASTEXITCODE -ne 0) {",
        '    $__gaErr = ($__gaOut -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 1)',
        '    Write-Output ("[host-ssh share] share connect failed: " + $__gaErr)',
        "  }",
        "}",
        f"if (-not (Test-Path '{drive}:\\')) {{",
        f"  $__gaMapOut = net use {drive}: $__gaUnc 2>&1 | Out-String",
        "  if ($LASTEXITCODE -ne 0) {",
        '    $__gaErr2 = ($__gaMapOut -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 1)',
        f'    Write-Output ("[host-ssh share] {drive}: map failed: " + $__gaErr2)',
        "  }",
        "}",
    ]
    return "\n".join(lines) + "\n" + powershell_script


def detect_os_subprocess(target: dict) -> str:
    """Detect the target OS over one sshpass+ssh round-trip."""
    user, password = require_ssh_config()
    port = target.get('port')
    if port is None:
        port = int(env('GA_HOST_SSH_PORT') or '22')
    sshpass = ensure_sshpass()
    cmd = [sshpass, '-e', 'ssh', '-4', '-p', str(port),
           '-o', 'StrictHostKeyChecking=no',
           '-o', 'PreferredAuthentications=password',
           '-o', 'PubkeyAuthentication=no',
           '-o', 'ConnectTimeout=15',
           f"{user}@{resolve_ipv4(target['host'])}", DETECT_SNIPPET]
    env_vars = os.environ.copy()
    env_vars['SSHPASS'] = password
    proc = subprocess.run(cmd, env=env_vars, capture_output=True, text=True,
                          timeout=45, check=False)
    return classify_uname(proc.stdout or '')


def run_remote_bash(bash_script: str, timeout: int,
                    machine: str | None = None,
                    distro: str | None = None,
                    force: bool = False) -> subprocess.CompletedProcess[str]:
    """Run a bash script inside WSL on a Windows SSH machine (or, with no
    distro and force=True, run it in bash on a Linux host).

    The script is base64-encoded so the remote command is one line of pure
    ASCII - the bash equivalent of -EncodedCommand (quotes, newlines and
    heredocs survive with no quoting gymnastics). Quoting rule: the Windows
    OpenSSH server executes the remote command line through cmd.exe, and
    cmd.exe does NOT treat single quotes as quoting - only double quotes.
    So the bash -c argument is double-quoted, and the harness carries no $,
    double quote, backslash, or percent: the script rides in the base64
    blob, a random token replaces $$ (and fixes concurrent-run collisions),
    the user script's exit code propagates as the last command's status,
    and an EXIT trap removes the work file. The script must not ride on
    stdin (interop processes would drain it). The preamble bridges the
    Windows PATH into the WSL session.

    `distro` wins when given; otherwise the machine record's "wsl.distro".
    """
    target, _share = get_machine(machine)
    user, password = require_ssh_config()
    distro = distro or _distro_from(target.get('wsl'))
    if not distro and not force:
        raise ValueError(
            f"machine '{target['name']}' has no WSL distro - set "
            f'"wsl": {{"distro": ...}} in GA_SSH_MACHINES (or pass distro=)')
    full_script = WINPATH_PREAMBLE + bash_script
    b64 = base64.b64encode(full_script.encode('utf-8')).decode('ascii')
    token = secrets.token_hex(8)
    work = '/tmp/ga-host-ssh.' + token + '.sh'
    inner = (
        'echo ' + b64 + ' | base64 -d > ' + work + ' && '
        "trap 'rm -f " + work + "' EXIT && "
        'bash ' + work
    )
    if distro:
        remote_cmd = 'wsl.exe -d ' + distro + ' -- bash -c "' + inner + '"'
    else:
        # No distro (forced bash): the host itself runs bash (Linux machine).
        remote_cmd = 'bash -c "' + inner + '"'
    return _run_remote(remote_cmd, user, target, password, timeout)


def compose_bash(bash_script: str, distro: str | None) -> str:
    """Pure composer for the WSL/Linux-bash backend (paramiko variant reuses
    it so both transports send byte-identical remote commands)."""
    full_script = WINPATH_PREAMBLE + bash_script
    b64 = base64.b64encode(full_script.encode('utf-8')).decode('ascii')
    token = secrets.token_hex(8)
    work = '/tmp/ga-host-ssh.' + token + '.sh'
    inner = (
        'echo ' + b64 + ' | base64 -d > ' + work + ' && '
        "trap 'rm -f " + work + "' EXIT && "
        'bash ' + work
    )
    if distro:
        return 'wsl.exe -d ' + distro + ' -- bash -c "' + inner + '"'
    return 'bash -c "' + inner + '"'


def run_remote_powershell_windows(powershell_script: str, timeout: int,
                                  machine: str | None = None) -> subprocess.CompletedProcess[str]:
    """Run PowerShell on a Windows SSH machine (the original backend)."""
    target, share = get_machine(machine)
    user, password = require_ssh_config()
    remote_cmd = compose_powershell(powershell_script, share)
    return _run_remote(remote_cmd, user, target, password, timeout)


def run_remote_posix(script: str, timeout: int,
                     machine: str | None = None) -> subprocess.CompletedProcess[str]:
    """mac/linux backend: send the script text verbatim to the account's
    login shell (zsh on macOS, bash on Linux). No encoding, no share
    bootstrap (SMB drive mapping is a Windows-only concept)."""
    target, _share = get_machine(machine)
    user, password = require_ssh_config()
    return _run_remote(compose_posix(script), user, target, password, timeout)


def run_remote_shell(script: str, timeout: int,
                     machine: str | None = None,
                     shell: str | None = None) -> subprocess.CompletedProcess[str]:
    """Run `script` on the machine's registered shell.

    Dispatch priority:
    1. explicit `shell` override ("bash" | "powershell");
    2. record has "wsl" -> bash inside the WSL distro;
    3. resolved os (declared "os" -> /tmp cache -> uname -s detection ->
       windows default): windows -> PowerShell, mac/linux -> plain POSIX.
    """
    if shell not in (None, 'bash', 'powershell'):
        raise ValueError(f"shell must be 'bash' or 'powershell', got {shell!r}")
    target, _share = get_machine(machine)
    if shell is None:
        if _distro_from(target.get('wsl')):
            shell = 'bash'
        else:
            remote_os = resolve_target_os(machine, detect=detect_os_subprocess)
            shell = 'bash' if remote_os in ('mac', 'linux') else 'powershell'
    if shell == 'powershell':
        return run_remote_powershell_windows(script, timeout, machine=machine)
    if _distro_from(target.get('wsl')):
        return run_remote_bash(script, timeout, machine=machine)
    remote_os = resolve_target_os(machine, detect=detect_os_subprocess)
    if remote_os in ('mac', 'linux'):
        return run_remote_posix(script, timeout, machine=machine)
    # bash forced on a windows machine without a wsl block: bash on the host.
    return run_remote_bash(script, timeout, machine=machine, force=True)


def run_remote_script(script: str, timeout: int,
                      machine: str | None = None) -> subprocess.CompletedProcess[str]:
    """OS-based dispatch without WSL: PowerShell on windows targets, plain
    POSIX shell on mac/linux targets (auto-detected). Backward-compatible
    entry point (the paramiko variant replaces it)."""
    remote_os = resolve_target_os(machine, detect=detect_os_subprocess)
    if remote_os in ('mac', 'linux'):
        return run_remote_posix(script, timeout, machine=machine)
    return run_remote_powershell_windows(script, timeout, machine=machine)


def run_remote_powershell(script: str, timeout: int,
                          machine: str | None = None) -> subprocess.CompletedProcess[str]:
    """Backward-compatible entry point (used by preflight.py).

    Dispatches per machine record: "wsl" -> bash inside the distro,
    otherwise the resolved-os backend (PowerShell / plain POSIX).
    """
    return run_remote_shell(script, timeout, machine=machine)
