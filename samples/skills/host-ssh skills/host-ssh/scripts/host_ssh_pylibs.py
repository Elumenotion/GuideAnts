#!/usr/bin/env python3
"""Paramiko-based runner for host_ssh.py when sshpass/ssh binaries are unavailable.

Reuses the official host_ssh.py CLI logic; the remote-execution entry point is
replaced with a paramiko implementation that supports all three backends:
windows (powershell.exe -NoProfile -NonInteractive -EncodedCommand <b64>, +
share bootstrap), wsl (bash inside the named distro via wsl.exe, base64
harness), and mac/linux (plain POSIX shell text). Remote commands come from
the same composers in ssh_common, so both transports send identical strings.
Hosts are resolved IPv4-first and the target OS is auto-detected via
`uname -s` when not declared.
"""
from __future__ import annotations

import argparse
import sys

import paramiko

import host_ssh
from ssh_common import (DETECT_SNIPPET, classify_uname, compose_bash,
                        compose_powershell, compose_posix, get_machine,
                        require_ssh_config, resolve_ipv4,
                        resolve_target_os)


def detect_os_paramiko(target: dict) -> str:
    """Detect windows/mac/linux over one paramiko connection."""
    user, password = require_ssh_config()
    port = target.get('port') or 22
    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    try:
        client.connect(resolve_ipv4(target['host']), port=port, username=user,
                       password=password, timeout=15, banner_timeout=15,
                       auth_timeout=15, allow_agent=False, look_for_keys=False)
        _stdin, stdout, _stderr = client.exec_command(DETECT_SNIPPET, timeout=30)
        out = stdout.read().decode('utf-8', 'replace')
    finally:
        client.close()
    return classify_uname(out)


def _exec_remote_cmd(remote_cmd: str, target: dict, user: str, password: str,
                     timeout: int):
    port = target.get('port') or 22
    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    try:
        client.connect(resolve_ipv4(target['host']), port=port, username=user,
                       password=password, timeout=15, banner_timeout=15,
                       auth_timeout=15, allow_agent=False, look_for_keys=False)
    except Exception as exc:
        raise RuntimeError(f"{type(exc).__name__}: {exc}") from exc
    try:
        _stdin, stdout, stderr = client.exec_command(remote_cmd, timeout=timeout)
        out = stdout.read().decode('utf-8', 'replace')
        err = stderr.read().decode('utf-8', 'replace')
        rc = stdout.channel.recv_exit_status()
    except Exception as exc:
        raise RuntimeError(f"exec_command failed: {type(exc).__name__}: {exc}") from exc
    finally:
        client.close()

    class _P:
        pass
    p = _P()
    p.stdout, p.stderr, p.returncode = out, err, rc
    return p


def run_remote_script_py(script: str, timeout: int, machine=None, shell=None):
    """Paramiko equivalent of run_remote_shell (same dispatch, same commands)."""
    if shell not in (None, 'bash', 'powershell'):
        raise ValueError(f"shell must be 'bash' or 'powershell', got {shell!r}")
    target, share = get_machine(machine)
    user, password = require_ssh_config()
    wsl = (target.get('wsl') or {}).get('distro')
    if shell is None:
        if wsl:
            shell = 'bash'
        else:
            remote_os = resolve_target_os(machine, detect=detect_os_paramiko)
            shell = 'bash' if remote_os in ('mac', 'linux') else 'powershell'
    if shell == 'powershell':
        remote_cmd = compose_powershell(script, share)
    elif wsl:
        remote_cmd = compose_bash(script, wsl)
    else:
        remote_os = resolve_target_os(machine, detect=detect_os_paramiko)
        if remote_os in ('mac', 'linux'):
            remote_cmd = compose_posix(script)
        else:
            remote_cmd = compose_bash(script, None)  # bash on the host
    return _exec_remote_cmd(remote_cmd, target, user, password, timeout)


host_ssh.run_remote_script = run_remote_script_py
host_ssh.detect_os = detect_os_paramiko


def main() -> int:
    parser = argparse.ArgumentParser(
        description='Run commands on a GuideAnts SSH machine (paramiko; '
                    'PowerShell on windows, bash in WSL, POSIX shell on mac/linux)')
    sub = parser.add_subparsers(dest='command', required=True)
    sub.add_parser('machines', help='List machines declared in the guide Environment')
    probe_p = sub.add_parser('probe', help='Verify reachability + capability profile')
    probe_p.add_argument('--machine')
    probe_p.add_argument('--all', action='store_true')
    probe_p.add_argument('--timeout', type=int, default=120)
    run_p = sub.add_parser('run', help='Run a command on a machine')
    run_p.add_argument('script')
    run_p.add_argument('--machine')
    run_p.add_argument('--shell', help="Override: 'bash' (WSL/posix) or 'powershell'")
    run_p.add_argument('-o', '--output', dest='output')
    run_p.add_argument('--timeout', type=int, default=120)
    args = parser.parse_args()
    if args.command == 'machines':
        return host_ssh.cmd_machines(args)
    if args.command == 'probe':
        return host_ssh.cmd_probe(args)
    try:
        script = host_ssh.read_shell_script(args.script)
        proc = run_remote_script_py(script, args.timeout, machine=args.machine,
                                    shell=args.shell)
    except Exception as exc:
        print(f"host_ssh_pylibs: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 2
    if proc.stdout:
        if args.output:
            with open(args.output, 'w', encoding='utf-8', newline='\n') as h:
                h.write(proc.stdout)
            print(f"wrote {len(proc.stdout.encode('utf-8'))} bytes to {args.output}")
        else:
            sys.stdout.write(proc.stdout)
            if not proc.stdout.endswith('\n'):
                sys.stdout.write('\n')
    if proc.stderr:
        sys.stderr.write(proc.stderr)
        if not proc.stderr.endswith('\n'):
            sys.stderr.write('\n')
    return proc.returncode


if __name__ == '__main__':
    sys.exit(main())
