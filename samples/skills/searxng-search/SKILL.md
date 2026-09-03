---
name: searxng-search
description: "Web and image search through the stack's self-hosted SearXNG (readweb-searxng container): ranked text results with engine attribution, and image search with direct downloads of photos into the notebook. Use when the user wants to search the web, find a photo of something, or get source URLs - no API key and no network scanning needed."
metadata:
  guideants:
    enabled: true
    display_order: 30
    requires_toolsets: [sandbox]
---

# searxng-search

Paths - fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. This skill's scripts live under
`Skills/searxng-search/scripts/` relative to it, so run the commands in this
file exactly as written. Write every deliverable to the CWD with a **bare
filename**: never prefix an output path with `Output/` - the CWD *is* the
output directory, so `Output/...` would create a nested `Output/` folder.

Searches **web text** and **images** through the GuideAnts stack's own
self-hosted SearXNG - the same instance the product's web-search / ReadWeb
tools are wired to. Stdlib Python only (urllib): no pip installs, no API keys.

## How the instance is wired (verified against this repo)

| Where | What |
|-------|------|
| `docker/docker-compose.cuda.yml` | service `searxng` (container `readweb-searxng`), image `guideants-searxng:latest`, port `127.0.0.1:8091:8080` |
| `docker/build/searxng/Dockerfile` | custom image: SearXNG + Chromium + `/browser/` render sidecar behind nginx :8080 |
| `docker/volumes/searxng/config/settings.yml` | `formats: [json]`, `limiter: false` - the JSON API is open to local clients |
| sandbox reachability | `http://host.docker.internal:8091` (the host-published port) |

`--base-url` or the `SEARXNG_URL` environment variable overrides the default,
so the skill also works against any other SearXNG instance (public or
self-hosted).

## Dependencies

Python 3 standard library only. The only prerequisite is that the stack is up
and `readweb-searxng` is running.

## What to run

Probe first (cheap; confirms the instance answers with JSON):

```bash
python3 Skills/searxng-search/scripts/searxng_tool.py probe
```

Web search:

```bash
python3 Skills/searxng-search/scripts/searxng_tool.py search "duck" --limit 10
python3 Skills/searxng-search/scripts/searxng_tool.py search "python async" --engines bing,duckduckgo --time-range week
python3 Skills/searxng-search/scripts/searxng_tool.py search "fastapi" --json   # raw JSON for parsing
```

Image search (list direct image URLs):

```bash
python3 Skills/searxng-search/scripts/searxng_tool.py images "duck" --list 10
```

Image search + download the top hits into the notebook:

```bash
python3 Skills/searxng-search/scripts/searxng_tool.py images "duck" --download 2 --prefix duck
```

### Flags

| Flag | search | images | Notes |
|------|--------|--------|-------|
| `--base-url` | yes | yes | overrides `SEARXNG_URL` / default `http://host.docker.internal:8091` |
| `--limit` | results (10) | results fetched (30) | |
| `--category` | `general` | - | `news`, `science`, `social media`, ... |
| `--engines` | e.g. `bing,duckduckgo` | same | restrict engines (default: all enabled) |
| `--time-range` | `day`/`week`/`month`/`year` | - | |
| `--safesearch` | `0`/`1`/`2` | - | |
| `--list` | - | rows printed (10) | |
| `--download` | - | top N to save (0) | saved to CWD as `<prefix>_01.jpg`, ... |
| `--prefix` | - | filename prefix | default: slug of the query |
| `--json` | raw result JSON | raw image-list JSON | for programmatic use |
| `--timeout` | seconds (30) | seconds (30) | |

## Behavior notes (observed live)

- Image queries return ~100+ results; useful engines are `bing images`,
  `duckduckgo images`, `openverse`, `wikicommons.images`. Expect some engines
  to be suspended (`brave: too many requests`, `startpage: CAPTCHA`) - that is
  normal upstream noise and is printed in the report.
- Downloads send a browser-like `User-Agent` plus the result page as
  `Referer` (several CDNs 403 without it), then verify the response by magic
  bytes (JPEG / PNG / WebP / GIF) **before** saving, and report width x
  height. The file extension comes from the sniffed bytes, not the URL.

## Failure modes

- **cannot reach SearXNG (connection error)** - the stack is not up or the
  port binding changed. Verify on the host: `docker ps` shows
  `readweb-searxng` and `http://127.0.0.1:8091/search?q=test&format=json`
  answers 200. Then retry via `host.docker.internal:8091`, or pass
  `--base-url` for another instance.
- **HTTP 403/429** - limiter/abuse control is enabled on that instance; the
  repo config has `limiter: false`.
- **no JSON returned** - the instance config is missing `formats: [json]`.
- **image skipped during download** - printed with the reason (403, HTML
  error page, non-image bytes); the tool moves on to the next result.

## Reporting

State the base URL used, the result count (plus which engines answered or
were suspended), and the exact CWD filenames of any downloaded images (the
UI displays them under `Output/`). If nothing could be downloaded, say why,
per the failure modes above.
