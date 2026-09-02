#!/usr/bin/env python3
"""Shared SSH + PowerShell helpers for host-ssh skill scripts.

Single machine (legacy): GA_HOST_SSH_HOST / _USER / _PASSWORD (+ GA_HOST_SSH_PORT).
Multi machine: GA_SSH_MACHINES is a JSON array of machine records; the record
with "default": true is the target when no --machine is given. The guide
Environment is the source of truth for which machines exist.

    [
      {"name": "office", "host": "host.docker.internal", "default": true,
       "share": {"unc": "\\\\FILESERVER\\content", "user": "DOMAIN\\GuideAnts", "drive": "R"}},
      {"name": "gpu", "host": "192.168.1.50"}
    ]

Share passwords stay in guide Environment secrets (GA_HOST_SSH_SHARE_PASSWORD);
a per-machine "share.password" field is supported but discouraged.
"""
from __future__ import annotations

import base64
import json
import os
import re
import shutil
import subprocess

MACHINES_ENV = "GA_SSH_MACHINES"
_MACHINE_RE = re.compile(r"^[a-zA-Z0-9][a-zA-Z0-9_-]*$")


def env(name: str) -> str:
    return os.environ.get(name, "").strip()


def ensure_sshpass() -> str:
    path = shutil.which("sshpass")
    if path:
        return path
    if not shutil.which("apt-get"):
        raise RuntimeError("sshpass not found and apt-get unavailable in this sandbox")
    subprocess.run(["apt-get", "update", "-qq"], check=False, capture_output=True)
    proc = subprocess.run(["apt-get", "install", "-y", "-qq", "sshpass"],
                          capture_output=True, text=True, check=False)
    if proc.returncode != 0:
        raise RuntimeError("failed to install sshpass: "
                           + (proc.stderr or proc.stdout or "unknown error")[:500])
    path = shutil.which("sshpass")
    if not path:
        raise RuntimeError("sshpass install reported success but binary not found")
    return path


def encode_powershell_command(command: str) -> str:
    """UTF-16LE base64 for powershell.exe -EncodedCommand."""
    return base64.b64encode(command.encode("utf-16-le")).decode("ascii")


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
    return bs + bs + server + bs + "".join(out)


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
        name = str(entry.get("name", "")).strip()
        host = str(entry.get("host", "")).strip()
        if not name or not host:
            raise ValueError(f"{MACHINES_ENV}[{i}] needs 'name' and 'host'")
        if not _MACHINE_RE.match(name):
            raise ValueError(f"{MACHINES_ENV}[{i}].name must match {_MACHINE_RE.pattern}")
        port = entry.get("port")
        if port is not None:
            port = int(port)
        share = entry.get("share") or None
        if share is not None:
            if not isinstance(share, dict) or not str(share.get("unc", "")).strip():
                raise ValueError(f"{MACHINES_ENV}[{i}].share needs a 'unc'")
            share = {
                "unc": normalize_unc(str(share["unc"]).strip()),
                "user": str(share.get("user", "")).strip(),
                "password": (str(share.get("password", "")).strip()
                             or env("GA_HOST_SSH_SHARE_PASSWORD")
                             or env("GA_HOST_SSH_PASSWORD")),
                "drive": str(share.get("drive", "R")).strip().upper() or "R",
            }
        machines.append({"name": name, "host": host, "port": port,
                         "default": bool(entry.get("default", False)), "share": share})
    if not any(m["default"] for m in machines):
        machines[0]["default"] = True
    return machines


def get_machine(name: str | None = None) -> tuple[dict, dict | None]:
    """Resolve (machine, share). Falls back to the legacy single machine."""
    machines = parse_machines()
    if machines:
        if name:
            for m in machines:
                if m["name"].lower() == name.lower():
                    return m, m["share"]
            available = ", ".join(m["name"] for m in machines)
            raise ValueError(f"unknown machine '{name}' (GA_SSH_MACHINES has: {available})")
        for m in machines:
            if m["default"]:
                return m, m["share"]
        raise ValueError("GA_SSH_MACHINES has no 'default': true entry")
    host = env("GA_HOST_SSH_HOST") or "host.docker.internal"
    m = {"name": "host", "host": host,
         "port": int(env("GA_HOST_SSH_PORT") or "22"),
         "default": True, "share": None}
    unc = normalize_unc(env("GA_HOST_SSH_SHARE_UNC"))
    if unc:
        m["share"] = {
            "unc": unc,
            "user": env("GA_HOST_SSH_SHARE_USER"),
            "password": env("GA_HOST_SSH_SHARE_PASSWORD") or env("GA_HOST_SSH_PASSWORD"),
            "drive": (env("GA_HOST_SSH_SHARE_DRIVE") or "R").upper(),
        }
    return m, m["share"]


def require_ssh_config() -> tuple[str, str]:
    """(user, password) from guide env; raises RuntimeError when missing."""
    user = env("GA_HOST_SSH_USER")
    password = env("GA_HOST_SSH_PASSWORD")
    missing = [k for k, v in (("GA_HOST_SSH_USER", user), ("GA_HOST_SSH_PASSWORD", password)) if not v]
    if missing:
        raise RuntimeError("Missing guide Environment: " + ", ".join(missing)
                           + ". See host-ssh README for setup.")
    return user, password


def wrap_powershell_with_share_bootstrap(powershell_script: str, share: dict | None) -> str:
    """Reconnect the UNC share in each SSH session (drive letters never carry over).

    net use output is captured and the first error line is echoed when the
    connect fails (system error 67 = bad UNC, 71 = SMB session table full) —
    never silently swallowed.
    """
    if not share:
        return powershell_script
    unc = share["unc"]
    user = (share.get("user") or "").strip()
    password = (share.get("password") or "").strip()
    unc_ps = unc.replace("'", "''")
    user_ps = user.replace("'", "''")
    pass_ps = password.replace("'", "''")
    if user_ps and pass_ps:
        use_cmd = f"net use $__gaUnc /user:{user_ps} '{pass_ps}'"
    elif user_ps:
        use_cmd = f"net use $__gaUnc /user:{user_ps}"
    else:
        use_cmd = "net use $__gaUnc"
    drive = (share.get("drive") or "R").strip().upper()
    if not (len(drive) == 1 and drive.isalpha()):
        drive = "R"
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


def run_remote_powershell(powershell_script: str, timeout: int,
                          machine: str | None = None) -> subprocess.CompletedProcess[str]:
    target, share = get_machine(machine)
    user, password = require_ssh_config()
    port = target.get("port") or int(env("GA_HOST_SSH_PORT") or "22")
    sshpass = ensure_sshpass()
    wrapped = wrap_powershell_with_share_bootstrap(powershell_script, share)
    remote_cmd = ("powershell.exe -NoProfile -NonInteractive -EncodedCommand "
                  + encode_powershell_command(wrapped))
    cmd = [sshpass, "-e", "ssh", "-p", str(port),
           "-o", "StrictHostKeyChecking=no",
           "-o", "PreferredAuthentications=password",
           "-o", "PubkeyAuthentication=no",
           "-o", "ConnectTimeout=15",
           f"{user}@{target['host']}", remote_cmd]
    env_vars = os.environ.copy()
    env_vars["SSHPASS"] = password
    return subprocess.run(cmd, env=env_vars, capture_output=True, text=True,
                          timeout=timeout, check=False)
