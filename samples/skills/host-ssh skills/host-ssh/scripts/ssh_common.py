#!/usr/bin/env python3
"""Shared SSH + remote-command helpers for host-ssh skill scripts.

Single machine (legacy): GA_HOST_SSH_HOST / _USER / _PASSWORD (+ GA_HOST_SSH_PORT).
Multi machine: GA_SSH_MACHINES is a JSON array of machine records; the record
with "default": true is the target when no --machine is given. The guide
Environment is the source of truth for which machines exist.

    [
      {"name": "office", "host": "host.docker.internal", "default": true,
       "os": "windows",
       "share": {"unc": "\\\\FILESERVER\\content", "user": "DOMAIN\\GuideAnts", "drive": "R"}},
      {"name": "laptop", "host": "host.docker.internal", "os": "mac"},
      {"name": "gpu", "host": "192.168.1.50", "os": "linux"}
    ]

Per-machine optional "os": "windows" | "mac" | "linux" selects the remote
command style: Windows targets run PowerShell (powershell.exe
-EncodedCommand, UTF-16LE base64); mac/linux targets run the script as
plain POSIX shell text. When "os" is absent it is auto-detected once via
`uname -s` and cached in /tmp for 10 minutes (empty output -> windows).

Share passwords stay in guide Environment secrets (GA_HOST_SSH_SHARE_PASSWORD);
a per-machine "share.password" field is supported but discouraged.
"""
from __future__ import annotations

import base64
import json
import os
import re
import shutil
import socket
import subprocess
import time

MACHINES_ENV = 'GA_SSH_MACHINES'
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
    return os.environ.get(name, '').strip()


def ensure_sshpass() -> str:
    path = shutil.which('sshpass')
    if path:
        return path
    if not shutil.which('apt-get'):
        raise RuntimeError('sshpass not found and apt-get unavailable in this sandbox')
    subprocess.run(['apt-get', 'update', '-qq'], check=False, capture_output=True)
    proc = subprocess.run(['apt-get', 'install', '-y', '-qq', 'sshpass'],
                          capture_output=True, text=True, check=False)
    if proc.returncode != 0:
        raise RuntimeError('failed to install sshpass: '
                           + (proc.stderr or proc.stdout or 'unknown error')[:500])
    path = shutil.which('sshpass')
    if not path:
        raise RuntimeError('sshpass install reported success but binary not found')
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
                         'default': bool(entry.get('default', False)), 'share': share})
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
         'os': None,
         'default': True, 'share': None}
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
    supplied detect(target_dict) callable, else the legacy 'windows' default."""
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


def compose_remote_command(script: str, remote_os: str, share: dict | None) -> str:
    """Windows: UTF-16LE base64 powershell.exe -EncodedCommand (+ share
    bootstrap). mac/linux: raw POSIX shell text (SMB share bootstrap is a
    Windows-only concept and is skipped)."""
    if remote_os in ('mac', 'linux'):
        return script
    return ('powershell.exe -NoProfile -NonInteractive -EncodedCommand '
            + encode_powershell_command(
                wrap_powershell_with_share_bootstrap(script, share)))


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


def run_remote_script(script: str, timeout: int,
                      machine: str | None = None) -> subprocess.CompletedProcess[str]:
    """Run a script on a machine: PowerShell on windows targets, POSIX
    shell on mac/linux targets (auto-detected)."""
    target, share = get_machine(machine)
    user, password = require_ssh_config()
    port = target.get('port')
    if port is None:
        port = int(env('GA_HOST_SSH_PORT') or '22')
    remote_os = resolve_target_os(machine, detect=detect_os_subprocess)
    remote_cmd = compose_remote_command(script, remote_os, share)
    sshpass = ensure_sshpass()
    cmd = [sshpass, '-e', 'ssh', '-4', '-p', str(port),
           '-o', 'StrictHostKeyChecking=no',
           '-o', 'PreferredAuthentications=password',
           '-o', 'PubkeyAuthentication=no',
           '-o', 'ConnectTimeout=15',
           f"{user}@{resolve_ipv4(target['host'])}", remote_cmd]
    env_vars = os.environ.copy()
    env_vars['SSHPASS'] = password
    return subprocess.run(cmd, env=env_vars, capture_output=True, text=True,
                          timeout=timeout, check=False)
