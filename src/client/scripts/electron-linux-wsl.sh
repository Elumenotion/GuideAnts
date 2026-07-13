#!/usr/bin/env bash
# GuideAnts Electron — Linux build & smoke test on WSL/Ubuntu.
# Run from an interactive WSL terminal (sudo password required once for apt).
set -euo pipefail

WSL_CLIENT="${WSL_CLIENT:-$HOME/GuideAnts/src/client}"
SYNC_FROM="${SYNC_FROM:-/mnt/d/repos/GuideAnts/src/client}"

if [[ -s "${HOME}/.nvm/nvm.sh" ]]; then
  # shellcheck source=/dev/null
  source "${HOME}/.nvm/nvm.sh"
  nvm use 22
fi

echo "==> Installing Electron runtime libraries (Ubuntu/WSL)"
sudo DEBIAN_FRONTEND=noninteractive apt-get update -qq
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y \
  libgtk-3-0t64 libnotify4 libnss3 libxss1 libxtst6 xdg-utils \
  libatspi2.0-0t64 libdrm2 libgbm1 libasound2t64 fuse libfuse2t64

echo "==> Syncing client to WSL filesystem: ${WSL_CLIENT}"
mkdir -p "$(dirname "${WSL_CLIENT}")"
rsync -a --delete "${SYNC_FROM}/" "${WSL_CLIENT}/"

cd "${WSL_CLIENT}"
npm ci

MODE="${1:-dir}"
if [[ "${MODE}" == "appimage" ]]; then
  npm run electron:build:linux
elif [[ "${MODE}" == "dev" ]]; then
  npm run electron:dev
else
  npm run electron:build:linux:dir
  BIN="${WSL_CLIENT}/dist/linux-unpacked/guideants-notebooks"
  timeout 8 "${BIN}" 2>&1 || true
  echo "Launch: ${BIN}"
fi
