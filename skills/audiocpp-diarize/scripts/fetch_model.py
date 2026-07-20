#!/usr/bin/env python3
"""Download a Hugging Face model snapshot without GuideAnts' catalog downloader.

Handles the blockers that kept models out of the GuideAnts catalog: prefix-scoped
downloads (--include), gated repos (HF_TOKEN), and composite models (run once per
repo with different --dest). Resumable: files already present with the right size
are skipped, so re-running after the ~5 min sandbox budget continues where it left
off. Stdlib-only.
"""
import argparse
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request

HF_BASE = "https://huggingface.co"
CHUNK_SIZE = 1024 * 1024


def _open(url: str, token: str | None, timeout: int = 60):
    headers = {"User-Agent": "guideants-skill-fetch/1.0"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    return urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=timeout)


def list_repo_files(repo: str, revision: str, token: str | None) -> list[dict]:
    url = f"{HF_BASE}/api/models/{repo}/tree/{urllib.parse.quote(revision)}?recursive=true"
    with _open(url, token) as response:
        entries = json.load(response)
    return [entry for entry in entries if entry.get("type") == "file"]


def main() -> None:
    parser = argparse.ArgumentParser(description="Scoped/gated/resumable HF snapshot download")
    parser.add_argument("repo", help="HF repo id, e.g. Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice")
    parser.add_argument("--dest", required=True, help="Target directory (model root the engine will load)")
    parser.add_argument("--revision", default="main")
    parser.add_argument("--include", action="append", default=[], help="Only files whose repo path starts with this prefix (repeatable)")
    parser.add_argument("--exclude", action="append", default=[], help="Skip files whose repo path starts with this prefix (repeatable)")
    parser.add_argument("--strip-prefix", default=None, help="Remove this leading path from files when writing to dest (e.g. languages/english/)")
    parser.add_argument("--max-gb", type=float, default=30.0, help="Refuse to download more than this in one run")
    parser.add_argument("--dry-run", action="store_true", help="List what would be downloaded and exit")
    args = parser.parse_args()

    token = os.environ.get("HF_TOKEN", "").strip() or None
    try:
        files = list_repo_files(args.repo, args.revision, token)
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")[:300]
        hint = " (gated repo? set HF_TOKEN in the guide's Environment variables)" if exc.code in (401, 403) else ""
        sys.stderr.write(f"Listing {args.repo} failed with HTTP {exc.code}{hint}: {detail}\n")
        sys.exit(1)

    selected = []
    for entry in files:
        path = entry["path"]
        if args.include and not any(path.startswith(prefix) for prefix in args.include):
            continue
        if any(path.startswith(prefix) for prefix in args.exclude):
            continue
        selected.append(entry)

    total_bytes = sum(entry.get("size") or 0 for entry in selected)
    if args.dry_run:
        print(json.dumps({
            "repo": args.repo,
            "files": [{"path": e["path"], "size": e.get("size")} for e in selected],
            "totalGb": round(total_bytes / (1024 ** 3), 2),
        }, indent=2))
        return
    if total_bytes > args.max_gb * (1024 ** 3):
        sys.stderr.write(
            f"Selection is {total_bytes / (1024 ** 3):.1f} GB, over the --max-gb {args.max_gb} guard. "
            "Narrow with --include or raise --max-gb deliberately.\n"
        )
        sys.exit(1)

    downloaded, skipped = [], []
    for entry in selected:
        repo_path = entry["path"]
        local_rel = repo_path
        if args.strip_prefix and local_rel.startswith(args.strip_prefix):
            local_rel = local_rel[len(args.strip_prefix):].lstrip("/")
        local_path = os.path.join(args.dest, local_rel)
        expected_size = entry.get("size")
        if expected_size is not None and os.path.isfile(local_path) and os.path.getsize(local_path) == expected_size:
            skipped.append(repo_path)
            continue
        os.makedirs(os.path.dirname(local_path) or ".", exist_ok=True)
        url = f"{HF_BASE}/{args.repo}/resolve/{urllib.parse.quote(args.revision)}/{urllib.parse.quote(repo_path)}"
        temp_path = local_path + ".part"
        try:
            with _open(url, token, timeout=120) as response, open(temp_path, "wb") as handle:
                while True:
                    chunk = response.read(CHUNK_SIZE)
                    if not chunk:
                        break
                    handle.write(chunk)
            os.replace(temp_path, local_path)
            downloaded.append(repo_path)
        except urllib.error.HTTPError as exc:
            sys.stderr.write(f"Download of {repo_path} failed with HTTP {exc.code}\n")
            sys.exit(1)
        except Exception:
            if os.path.exists(temp_path):
                os.unlink(temp_path)
            raise

    print(json.dumps({
        "dest": os.path.abspath(args.dest),
        "downloaded": len(downloaded),
        "skippedUpToDate": len(skipped),
        "totalSelected": len(selected),
        "note": "re-run the same command to resume if the script budget cut this off",
    }))


if __name__ == "__main__":
    main()
