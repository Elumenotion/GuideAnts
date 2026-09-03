#!/usr/bin/env python3
"""Preflight for host-ssh skill (operator diagnostics).

Loops every machine declared in the guide Environment (GA_SSH_MACHINES, or the
legacy GA_HOST_SSH_HOST single machine) and checks: credentials, sshpass,
TCP port, SSH login (hostname). Prints one JSON verdict.
"""
from __future__ import annotations

import argparse
import json
import socket
import subprocess
import sys

from ssh_common import (ensure_sshpass, env, get_machine, parse_machines,
                        require_ssh_config, run_remote_powershell)


def check_port(host: str, port: int) -> dict:
    try:
        with socket.create_connection((host, port), timeout=5):
            return {"ok": True, "host": host, "port": port}
    except OSError as exc:
        return {"ok": False, "host": host, "port": port,
                "error": f"{type(exc).__name__}: {exc}"}


def check_sshpass() -> dict:
    try:
        return {"ok": True, "path": ensure_sshpass()}
    except RuntimeError as exc:
        return {"ok": False, "error": str(exc)}


def check_ssh_login(machine: dict) -> dict:
    try:
        proc = run_remote_powershell("hostname", timeout=30, machine=machine["name"])
    except subprocess.TimeoutExpired:
        return {"ok": False, "error": "ssh timed out after 30s"}
    except (RuntimeError, ValueError) as exc:
        return {"ok": False, "error": str(exc)}
    stdout = (proc.stdout or "").strip()
    stderr = (proc.stderr or "").strip()
    return {"ok": proc.returncode == 0 and bool(stdout),
            "exitCode": proc.returncode,
            "stdout": stdout[:200], "stderr": stderr[:300]}


def run_preflight(scenario: str) -> dict:
    blockers: list[str] = []
    warnings: list[str] = []
    evidence: dict = {"vars": {}, "machines": {}}

    try:
        require_ssh_config()
        for key in ("GA_HOST_SSH_USER", "GA_HOST_SSH_PASSWORD"):
            evidence["vars"][key] = "set"
    except RuntimeError:
        for key in ("GA_HOST_SSH_USER", "GA_HOST_SSH_PASSWORD"):
            evidence["vars"][key] = "set" if env(key) else "missing"
            if not env(key):
                blockers.append(f"{key} is not set in guide Environment")

    machines: list[dict] = []
    try:
        machines = parse_machines()
    except ValueError as exc:
        blockers.append(str(exc))
    if not machines:
        try:
            m, _share = get_machine(None)
            machines = [m]
        except ValueError as exc:
            blockers.append(str(exc))

    sshpass_check = check_sshpass()
    evidence["sshpass"] = sshpass_check
    if not sshpass_check.get("ok"):
        blockers.append("sshpass unavailable: " + sshpass_check.get("error", ""))

    if not blockers:
        for m in machines:
            port = m.get("port") or int(env("GA_HOST_SSH_PORT") or "22")
            entry: dict = {"tcp": check_port(m["host"], port)}
            if entry["tcp"]["ok"]:
                entry["sshLogin"] = check_ssh_login(m)
                if not entry["sshLogin"]["ok"]:
                    blockers.append(f"machine '{m['name']}': SSH login test failed")
                    if entry["sshLogin"].get("stderr"):
                        warnings.append(f"{m['name']}: {entry['sshLogin']['stderr'][:300]}")
            else:
                blockers.append(f"machine '{m['name']}': cannot reach {m['host']}:{port}")
            evidence["machines"][m["name"]] = entry

    return {"scenario": scenario, "open": not blockers,
            "blockers": blockers, "warnings": warnings,
            "evidence": evidence, "route": "route_host_ssh"}


def main() -> int:
    parser = argparse.ArgumentParser(description="Host SSH preflight")
    parser.add_argument("--for", dest="scenario", default="probe",
                        choices=("probe", "run"),
                        help="Scenario name (probe and run use the same checks)")
    args = parser.parse_args()
    result = run_preflight(args.scenario)
    print(json.dumps(result, indent=2))
    return 0 if result["open"] else 1


if __name__ == "__main__":
    sys.exit(main())
