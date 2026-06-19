# GuideAnts — Portable Quick Start

This is one universal bundle for every operating system. It contains a single
launcher (`guideants.sh`) plus the Docker Compose files. Docker downloads the
actual application images on first run, so this download itself is tiny.

> **Requirement:** Docker must be installed and running.
>
> - **Windows / macOS:** [Docker Desktop](https://www.docker.com/products/docker-desktop/)
> - **Linux:** Docker Engine 24+ with the Compose plugin

## How to run

**Linux**

```bash
chmod +x guideants.sh
./guideants.sh
```

**macOS**

```bash
chmod +x guideants.sh
./guideants.sh
```

If macOS blocks it (Gatekeeper), either right-click → **Open**, or run:

```bash
xattr -d com.apple.quarantine guideants.sh
```

**Windows**

Docker Desktop installs WSL2, which includes bash. Open a WSL terminal
(or Git Bash), change into this folder, and run:

```bash
bash guideants.sh
```

## What the launcher does

1. Checks Docker is installed and running.
2. Reports your memory and disk.
3. Walks you through which backend to use:

   | Backend | Use for |
   |---------|---------|
   | `cuda13` | NVIDIA GPU |
   | `rocm` | AMD GPU (Linux / Windows via WSL2) |
   | `cpu` | No GPU |
   | `slim` | Cloud AI providers |

4. Checks the registry for newer images and **asks before updating**
   (no flag to remember — it happens automatically each launch).
5. Starts the stack and opens <http://localhost:5107/> in your browser.

On first load you'll be sent to `/register`; the first account becomes **Admin**.

## Options

| Command | What it does |
|---------|--------------|
| `./guideants.sh --doctor` | Run all checks, change nothing. |
| `./guideants.sh --backend slim` | Skip the backend prompt. |
| `./guideants.sh --reconfigure` | Re-prompt for backend even if one was previously saved. |
| `./guideants.sh --yes` | Accept prompts automatically. |
| `./guideants.sh --help` | Full help. |

## Stopping / data

Stop the stack:

```bash
cd docker && docker compose -f docker-compose.ghcr-<backend>.yml down
```

Your projects, database, and models live in Docker named volumes and in your
content-files folder; they persist across stops and updates.


**TODO**
1. find out why slim uses different database than other backend models