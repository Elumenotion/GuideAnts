#!/usr/bin/env python3
"""searxng_tool.py - web + image search via the stack's self-hosted SearXNG.

Stdlib only (urllib). Default base URL is the host-published port of the
readweb-searxng container in docker/docker-compose.cuda.yml:
127.0.0.1:8091 on the host -> http://host.docker.internal:8091 from the
sandbox. Override with SEARXNG_URL env or --base-url.

Subcommands:
  probe                 confirm the instance answers with JSON
  search <query>        web text search (ranked, engine-attributed)
  images <query>        image search, optionally download top hits to CWD
"""

import argparse
import json
import os
import struct
import sys
import urllib.error
import urllib.parse
import urllib.request

DEFAULT_BASE = "http://host.docker.internal:8091"
USER_AGENT = "Mozilla/5.0 (compatible; guideants-searxng-search/1.0)"

MAGIC_EXT = [
    (bytes([0xFF, 0xD8, 0xFF]), ".jpg"),
    (bytes([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]), ".png"),
    (b"GIF8", ".gif"),
]


def resolve_base(cli_value):
    if cli_value:
        return cli_value.rstrip("/")
    env = (os.environ.get("SEARXNG_URL") or "").strip()
    return env.rstrip("/") or DEFAULT_BASE


def http_get(url, referer=None, timeout=30):
    headers = {"User-Agent": USER_AGENT}
    if referer:
        headers["Referer"] = referer
    req = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.status, dict(resp.headers), resp.read()


def searx_search(base, params, timeout=30):
    url = base + "/search?" + urllib.parse.urlencode(params)
    try:
        status, headers, body = http_get(url, timeout=timeout)
    except urllib.error.HTTPError as exc:
        snippet = exc.read()[:400].decode("utf-8", "replace")
        if exc.code in (403, 429):
            sys.exit(
                "ERROR: HTTP %d from SearXNG at %s - limiter/abuse control is "
                "active for this client. The repo config has limiter: false; "
                "check the instance settings." % (exc.code, base)
            )
        sys.exit("ERROR: HTTP %d from %s\n%s" % (exc.code, url, snippet))
    except Exception as exc:
        sys.exit(
            "ERROR: cannot reach SearXNG at %s: %s\n"
            "Hints: is the stack up? The readweb-searxng container publishes "
            "127.0.0.1:8091 on the host, reachable from the sandbox via "
            "host.docker.internal. Verify on the host: docker ps and "
            "http://127.0.0.1:8091/search?q=test&format=json" % (base, exc)
        )
    try:
        return json.loads(body)
    except Exception:
        sys.exit(
            "ERROR: %s did not return JSON (body starts %r). Is the instance "
            "configured with formats: [json]?" % (base, body[:200])
        )


def unresponsive(data):
    out = []
    for entry in data.get("unresponsive_engines") or []:
        if isinstance(entry, (list, tuple)) and entry:
            out.append(str(entry[0]))
        else:
            out.append(str(entry))
    return out


def print_unresponsive(data):
    names = unresponsive(data)
    if names:
        shown = ",".join(names[:5])
        more = "..." if len(names) > 5 else ""
        print("(unresponsive engines: %s%s)" % (shown, more))


def jpeg_size(blob):
    i = 2
    while i + 9 < len(blob):
        if blob[i] != 0xFF:
            i += 1
            continue
        marker = blob[i + 1]
        if marker in (0xC0, 0xC1, 0xC2, 0xC3):
            height, width = struct.unpack(">HH", blob[i + 5 : i + 9])
            return width, height
        if marker in (0xD8, 0x01) or 0xD0 <= marker <= 0xD9:
            i += 2
            continue
        seg_len = struct.unpack(">H", blob[i + 2 : i + 4])[0]
        i += 2 + seg_len
    return None


def image_size(blob, ext):
    if ext == ".jpg":
        return jpeg_size(blob)
    if ext == ".png" and len(blob) > 24:
        return struct.unpack(">II", blob[16:24])
    return None


def sniff_ext(blob):
    if blob[:4] == b"RIFF" and blob[8:12] == b"WEBP":
        return ".webp"
    for magic, ext in MAGIC_EXT:
        if blob.startswith(magic):
            return ext
    return None


def slugify(text, max_words=3):
    words = []
    cur = []
    for ch in text.lower():
        if ch.isalnum():
            cur.append(ch)
        else:
            if cur:
                words.append("".join(cur))
                cur = []
            if len(words) >= max_words:
                break
    if cur and len(words) < max_words:
        words.append("".join(cur))
    return "-".join(words[:max_words]) or "img"


def download_image(img_url, referer, out_name, timeout=45):
    try:
        status, headers, blob = http_get(img_url, referer=referer, timeout=timeout)
    except Exception as exc:
        return None, "download failed: %s" % exc
    ext = sniff_ext(blob)
    if ext is None:
        ct = (headers.get("Content-Type") or "?").lower()
        return None, "not an image (content-type=%s, %d bytes)" % (ct, len(blob))
    out_path = out_name if out_name.lower().endswith(ext) else out_name + ext
    with open(out_path, "wb") as f:
        f.write(blob)
    detail = "%d bytes" % len(blob)
    size = image_size(blob, ext)
    if size:
        detail += " %dx%d" % size
    return out_path, detail


def cmd_probe(args):
    base = resolve_base(args.base_url)
    data = searx_search(base, {"q": "test", "format": "json", "limit": "1"},
                        timeout=args.timeout)
    results = data.get("results", [])
    engines = sorted({r.get("engine", "?") for r in results})
    print("OK SearXNG reachable at %s" % base)
    print("probe query: %d result(s)%s" % (
        len(results), " from " + ",".join(engines) if engines else ""))
    print("JSON format: enabled")
    print_unresponsive(data)
    return 0


def cmd_search(args):
    base = resolve_base(args.base_url)
    params = {
        "q": args.query,
        "format": "json",
        "limit": str(args.limit),
        "categories": args.category,
    }
    if args.engines:
        params["engines"] = args.engines
    if args.time_range:
        params["time_range"] = args.time_range
    if args.safesearch is not None:
        params["safesearch"] = str(args.safesearch)
    data = searx_search(base, params, timeout=args.timeout)
    results = data.get("results", [])
    if args.json:
        print(json.dumps(results, indent=2))
        return 0
    print("SearXNG web search: %r via %s - %d results" % (args.query, base, len(results)))
    print_unresponsive(data)
    for i, r in enumerate(results, 1):
        title = " ".join((r.get("title") or "").split())
        url = r.get("url") or ""
        content = " ".join((r.get("content") or "").split())
        print("%2d. [%s] %s" % (i, r.get("engine", "?"), title[:100]))
        print("    %s" % url)
        if content:
            print("    %s" % content[:160])
    return 0


def cmd_images(args):
    base = resolve_base(args.base_url)
    params = {
        "q": args.query,
        "format": "json",
        "categories": "images",
        "limit": str(args.limit),
    }
    if args.engines:
        params["engines"] = args.engines
    data = searx_search(base, params, timeout=args.timeout)
    results = data.get("results", [])
    with_img = [(i, r) for i, r in enumerate(results) if r.get("img_src")]
    print("SearXNG image search: %r via %s - %d results, %d with direct image URLs" % (
        args.query, base, len(results), len(with_img)))
    if args.json:
        slim = [{
            "engine": r.get("engine"),
            "img_src": r.get("img_src"),
            "url": r.get("url"),
            "title": r.get("title"),
        } for _, r in with_img]
        print(json.dumps(slim, indent=2))
        return 0
    print_unresponsive(data)
    shown = 0
    for i, r in with_img:
        shown += 1
        print("%2d. [%s] %s" % (shown, r.get("engine", "?"), r["img_src"][:120]))
        if r.get("url"):
            print("     page: %s" % r["url"][:120])
        if shown >= args.list_rows:
            break
    if args.download <= 0:
        return 0
    prefix = args.prefix or slugify(args.query)
    saved = []
    for i, r in with_img:
        if len(saved) >= args.download:
            break
        out_name = "%s_%02d" % (prefix, len(saved) + 1)
        path, detail = download_image(r["img_src"], r.get("url"), out_name,
                                      timeout=args.timeout)
        if path:
            print("saved %s (%s) <- %s" % (path, detail, r["img_src"][:100]))
            saved.append(path)
        else:
            print("skip  [%s] %s - %s" % (r.get("engine", "?"), detail, r["img_src"][:80]))
    if not saved:
        sys.exit("ERROR: no image could be downloaded (all blocked or non-image responses)")
    print("downloaded %d image(s)" % len(saved))
    return 0


def main():
    p = argparse.ArgumentParser(
        description="Web + image search via self-hosted SearXNG (stdlib only)")
    p.add_argument("--base-url", default="",
                   help="SearXNG base URL (default: SEARXNG_URL env or %s)" % DEFAULT_BASE)
    p.add_argument("--timeout", type=int, default=30, help="HTTP timeout seconds")
    sub = p.add_subparsers(dest="cmd", required=True)

    sp = sub.add_parser("probe", help="confirm the instance answers with JSON")
    sp.set_defaults(fn=cmd_probe)

    ss = sub.add_parser("search", help="web text search")
    ss.add_argument("query")
    ss.add_argument("--limit", type=int, default=10)
    ss.add_argument("--category", default="general",
                    help="general, news, science, social media, ...")
    ss.add_argument("--engines", default="", help="comma list, e.g. bing,duckduckgo")
    ss.add_argument("--time-range", default="", choices=["", "day", "week", "month", "year"])
    ss.add_argument("--safesearch", type=int, choices=[0, 1, 2], default=None)
    ss.add_argument("--json", action="store_true", help="print raw result JSON")
    ss.set_defaults(fn=cmd_search)

    si = sub.add_parser("images", help="image search (+ optional downloads)")
    si.add_argument("query")
    si.add_argument("--limit", type=int, default=30, help="results to fetch")
    si.add_argument("--list", type=int, default=10, dest="list_rows",
                    help="rows to print")
    si.add_argument("--engines", default="")
    si.add_argument("--download", type=int, default=0,
                    help="download top N images into the CWD")
    si.add_argument("--prefix", default="", help="filename prefix (default: query slug)")
    si.add_argument("--json", action="store_true", help="print raw image list JSON")
    si.set_defaults(fn=cmd_images)

    args = p.parse_args()
    sys.exit(args.fn(args) or 0)


if __name__ == "__main__":
    main()
