#!/usr/bin/env bash
# Install ROCm userspace + ROCDXG for AMD GPUs on WSL2 (Windows Docker Desktop path).
#
# Supported hardware includes Strix Halo / Ryzen AI Max+ (Radeon 8060S) and other
# GPUs listed in AMD's ROCDXG compatibility matrix (Adrenalin 26.2.2+, ROCm 7.2.x+).
# See: https://github.com/ROCm/librocdxg
#
# Run as root inside a user WSL distro (not docker-desktop), e.g.:
#   wsl -d Ubuntu-24.04 -u root bash /mnt/c/path/to/GuideAnts/installer/scripts/install-rocm-wsl.sh
#
# Options:
#   --rocm-version <ver>       ROCm/amdgpu-install version (default: 7.2.4)
#   --librocdxg-version <ver>  librocdxg release (default: 1.2.0)
#   --force                    Reinstall even when rocminfo already succeeds
#   --yes, -y                  Skip reinstall prompt when already installed

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

ROCM_VERSION="7.2.4"
LIBROCDXG_VERSION="1.2.0"
FORCE="0"
ASSUME_YES="0"

usage() {
  sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rocm-version)
      [[ $# -ge 2 ]] || { echo "Missing value for --rocm-version" >&2; exit 1; }
      ROCM_VERSION="$2"
      shift 2
      ;;
    --librocdxg-version)
      [[ $# -ge 2 ]] || { echo "Missing value for --librocdxg-version" >&2; exit 1; }
      LIBROCDXG_VERSION="$2"
      shift 2
      ;;
    --force) FORCE="1"; shift ;;
    --yes|-y) ASSUME_YES="1"; shift ;;
    --help|-h) usage; exit 0 ;;
    *)
      echo "Unknown option: $1 (try --help)" >&2
      exit 1
      ;;
  esac
done

if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
  echo "Run as root: wsl -d Ubuntu-24.04 -u root bash $0" >&2
  exit 1
fi

export DEBIAN_FRONTEND=noninteractive
export HSA_ENABLE_DXG_DETECTION=1

rocm_already_installed() {
  dpkg -l rocm-core 2>/dev/null | grep -q '^ii' \
    && test -f /opt/rocm/lib/librocdxg.so
}

verify_rocminfo() {
  set +e
  # shellcheck disable=SC1091
  source /etc/profile.d/rocm-wsl.sh 2>/dev/null || true
  HSA_ENABLE_DXG_DETECTION=1 rocminfo >/dev/null 2>&1
  local rc=$?
  set -e
  return $rc
}

if [[ "$FORCE" != "1" ]] && rocm_already_installed && verify_rocminfo; then
  echo "[rocm-wsl] ROCm + ROCDXG already installed and rocminfo succeeds."
  if [[ "$ASSUME_YES" == "1" ]]; then
    echo "[rocm-wsl] Skipping reinstall (--yes)."
    exit 0
  fi
  read -r -p "Reinstall anyway? [y/N] " reply
  reply="${reply:-N}"
  if [[ ! "$reply" =~ ^[Yy]$ ]]; then
    echo "[rocm-wsl] Skipping reinstall."
    exit 0
  fi
fi

# amdgpu-install package version suffix varies by release; 7.2.4 uses 70204 build id.
case "$ROCM_VERSION" in
  7.2.4) AMDGPU_INSTALL_DEB="amdgpu-install_7.2.4.70204-1_all.deb" ;;
  *)
    echo "[rocm-wsl][error] Unsupported --rocm-version $ROCM_VERSION (add package mapping in $0)." >&2
    exit 1
    ;;
esac

echo "[rocm-wsl] Updating apt..."
apt-get update -y

echo "[rocm-wsl] Installing prerequisites..."
apt-get install -y wget ca-certificates gnupg python3-setuptools python3-wheel

echo "[rocm-wsl] Adding AMDGPU/ROCm ${ROCM_VERSION} repository..."
cd /tmp
wget -q "https://repo.radeon.com/amdgpu-install/${ROCM_VERSION}/ubuntu/noble/${AMDGPU_INSTALL_DEB}"
apt-get install -y "./${AMDGPU_INSTALL_DEB}"
apt-get update -y

echo "[rocm-wsl] Installing ROCm userspace (ROCDXG path; skip amdgpu-dkms on WSL)..."
apt-get install -y rocm

echo "[rocm-wsl] Removing conflicting Ubuntu HSA packages if present..."
apt-get remove -y libhsa-runtime64-1 libhsakmt1 2>/dev/null || true

echo "[rocm-wsl] Installing librocdxg ${LIBROCDXG_VERSION}..."
cd /tmp
wget -q "https://github.com/ROCm/librocdxg/releases/download/v${LIBROCDXG_VERSION}/rocdxg-roct_${LIBROCDXG_VERSION}_amd64.deb"
dpkg -i "rocdxg-roct_${LIBROCDXG_VERSION}_amd64.deb" || apt-get install -f -y

echo "[rocm-wsl] Writing /etc/profile.d/rocm-wsl.sh..."
tr -d '\r' <"$SCRIPT_DIR/rocm-wsl.profile" >/etc/profile.d/rocm-wsl.sh
chmod 644 /etc/profile.d/rocm-wsl.sh

echo "[rocm-wsl] Writing version marker for launcher probes..."
mkdir -p /opt/rocm/.info
installed_version=""
for vfile in /opt/rocm-*/.info/version /opt/rocm/.info/version-rocm; do
  if [[ -f "$vfile" ]]; then
    cp "$vfile" /opt/rocm/.info/version
    installed_version="$(head -n1 /opt/rocm/.info/version | tr -d ' ')"
    break
  fi
done
if [[ -z "$installed_version" ]]; then
  echo "$ROCM_VERSION" >/opt/rocm/.info/version
fi

echo "[rocm-wsl] Verifying packages..."
dpkg -l | grep -E 'rocm|rocr4wsl|rocdxg' || true

echo "[rocm-wsl] Running rocminfo..."
set +e
# shellcheck disable=SC1091
source /etc/profile.d/rocm-wsl.sh
rocminfo 2>&1 | head -40
rc=$?
set -e

if [[ $rc -ne 0 ]]; then
  echo "[rocm-wsl][warn] rocminfo did not succeed yet. Check Adrenalin driver (26.2.2+) and reboot WSL: wsl --shutdown" >&2
  exit 1
fi

echo "[rocm-wsl] Done."
