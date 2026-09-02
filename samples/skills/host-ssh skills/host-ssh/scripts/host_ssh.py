#!/usr/bin/env python3
"""Run PowerShell on a GuideAnts SSH machine (password auth via guide env).

Machines come from GA_SSH_MACHINES (JSON) when set; otherwise the legacy
GA_HOST_SSH_HOST single-machine config is the default machine. Use
`host_ssh.py machines` to list what the guide Environment declares.
Use `host_ssh.py probe --all` to verify reachability + the account
capability profile after restarts (the denied set is by design; SKILL.md).
"""
from __future__ import annotations

import argparse
import subprocess
import sys

from ssh_common import get_machine, parse_machines, run_remote_powershell


def read_powershell_script(powershell: str) -> str:
    if powershell == "-":
        script = sys.stdin.read()
        if not script.strip():
            raise ValueError('stdin is empty; pass a PowerShell script or use run - <<\'PS\'')
        return script
    return powershell


def cmd_machines(_args) -> int:
    try:
        machines = parse_machines()
        if not machines:
            machine, _share = get_machine(None)
            machines = [machine]
    except ValueError as exc:
        print(f"host_ssh: {exc}", file=sys.stderr)
        return 2
    for m in machines:
        port = m.get("port") or 22
        share = ""
        if m.get("share"):
            share = f"  share: {m['share']['unc']} -> {m['share']['drive']}:"
        marker = " (default)" if m.get("default") else ""
        print(f"{m['name']}{marker}  {m['host']}:{port}{share}")
    return 0


PROBE_PS = r'''
"machine: " + $env:COMPUTERNAME
"user: " + "$($env:USERDOMAIN)$([char]92)$($env:USERNAME)"
"admin: " + ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
"identity: " + ([Security.Principal.WindowsIdentity]::GetCurrent()).Value
"fs: " + $(if (Get-PSDrive -PSProvider FileSystem -EA SilentlyContinue) { "ok" } else { "DENIED" })
foreach ($name in @("dotnet", "nvidia-smi", "docker", "node", "npm")) {
  $cmd = Get-Command $name -EA SilentlyContinue
  "tool:" + $name + ": " + $(if ($cmd) { "ok" } else { "absent" })
}
"now: " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
'''


def cmd_probe(args) -> int:
    if args.all:
        try:
            names = [m["name"] for m in parse_machines()]
        except ValueError:
            names = [""]
    else:
        names = [args.machine]
    bad = 0
    for name in names:
        label = name or "default"
        try:
            proc = run_remote_powershell(PROBE_PS, args.timeout, machine=(name or None))
        except subprocess.TimeoutExpired:
            print(f"host_ssh: {label}: timed out", file=sys.stderr)
            bad += 1
            continue
        except (RuntimeError, ValueError) as exc:
            print(f"host_ssh: {label}: {exc}", file=sys.stderr)
            bad += 1
            continue
        out = (proc.stdout or "").strip()
        lines = [l.strip() for l in out.splitlines() if l.strip()]
        if proc.returncode == 0 and lines and lines[0].startswith("machine:"):
            print(f"== {label}  ssh OK")
        elif proc.returncode == 0:
            print(f"== {label}  ssh OK (unexpected output)")
        else:
            first = lines[0] if lines else ""
            print(f"== {label}  remote shell OK (command error: {first[:120]})")
        for line in lines:
            print("   " + line)
    return 1 if bad else 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Run PowerShell on a GuideAnts SSH machine")
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("machines", help="List machines declared in the guide Environment")

    probe_p = sub.add_parser("probe",
                                  help="Verify reachability + capability profile (read-only, non-admin-safe)")
    probe_p.add_argument("--machine",
                         help="Probe one specific machine (default: the default machine)")
    probe_p.add_argument("--all", action="store_true",
                         help="Probe every machine in GA_SSH_MACHINES")
    probe_p.add_argument("--timeout", type=int, default=120,
                         help="SSH timeout seconds per machine (default 120)")

    run_p = sub.add_parser("run", help="Run a PowerShell command on a machine")
    run_p.add_argument("powershell",
                       help='PowerShell script, or "-" to read from stdin (heredoc-friendly)')
    run_p.add_argument("--machine",
                       help="Machine name from GA_SSH_MACHINES (default: the default machine)")
    run_p.add_argument("-o", "--output", dest="output",
                       help="Write stdout to this file in sandbox CWD (bare filename)")
    run_p.add_argument("--timeout", type=int, default=120,
                       help="SSH timeout seconds (default 120)")

    args = parser.parse_args()
    if args.command == "machines":
        return cmd_machines(args)
    elif args.command == "probe":
        return cmd_probe(args)
    try:
        script = read_powershell_script(args.powershell)
        proc = run_remote_powershell(script, args.timeout, machine=args.machine)
    except subprocess.TimeoutExpired:
        print("host_ssh: timed out", file=sys.stderr)
        return 124
    except (RuntimeError, ValueError) as exc:
        print(f"host_ssh: {exc}", file=sys.stderr)
        return 2

    if proc.stdout:
        if args.output:
            with open(args.output, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(proc.stdout)
            print(f"wrote {len(proc.stdout.encode('utf-8'))} bytes to {args.output}")
        else:
            sys.stdout.write(proc.stdout)
            if not proc.stdout.endswith("\n"):
                sys.stdout.write("\n")
    if proc.stderr:
        sys.stderr.write(proc.stderr)
        if not proc.stderr.endswith("\n"):
            sys.stderr.write("\n")
    return proc.returncode


if __name__ == "__main__":
    sys.exit(main())
