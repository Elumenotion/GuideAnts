#!/usr/bin/env bash
# Install ROCm for AMD Strix Halo (8060S) on WSL2 via ROCDXG.
# Run as root inside Ubuntu-24.04: wsl -d Ubuntu-24.04 -u root bash /mnt/c/repos/GuideAnts/installer/scripts/install-rocm-wsl.sh

set -euo pipefail

if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
  echo "Run as root: wsl -d Ubuntu-24.04 -u root bash $0" >&2
  exit 1
fi

export DEBIAN_FRONTEND=noninteractive
export HSA_ENABLE_DXG_DETECTION=1

echo "[rocm-wsl] Updating apt..."
apt-get update -y

echo "[rocm-wsl] Installing prerequisites..."
apt-get install -y wget ca-certificates gnupg python3-setuptools python3-wheel

echo "[rocm-wsl] Adding AMDGPU/ROCm 7.2.4 repository..."
cd /tmp
wget -q https://repo.radeon.com/amdgpu-install/7.2.4/ubuntu/noble/amdgpu-install_7.2.4.70204-1_all.deb
apt-get install -y ./amdgpu-install_7.2.4.70204-1_all.deb
apt-get update -y

echo "[rocm-wsl] Installing ROCm userspace (ROCDXG path; skip amdgpu-dkms on WSL)..."
apt-get install -y rocm

echo "[rocm-wsl] Removing conflicting Ubuntu HSA packages if present..."
apt-get remove -y libhsa-runtime64-1 libhsakmt1 2>/dev/null || true

echo "[rocm-wsl] Installing librocdxg 1.2.0..."
cd /tmp
wget -q https://github.com/ROCm/librocdxg/releases/download/v1.2.0/rocdxg-roct_1.2.0_amd64.deb
dpkg -i rocdxg-roct_1.2.0_amd64.deb || apt-get install -f -y

echo "[rocm-wsl] Writing /etc/profile.d/rocm-wsl.sh..."
tr -d '\r' </mnt/c/repos/GuideAnts/installer/scripts/rocm-wsl.profile >/etc/profile.d/rocm-wsl.sh
chmod 644 /etc/profile.d/rocm-wsl.sh

echo "[rocm-wsl] Writing version marker for launcher probes..."
mkdir -p /opt/rocm/.info
if [[ -f /opt/rocm-7.2.1/.info/version ]]; then
  cp /opt/rocm-7.2.1/.info/version /opt/rocm/.info/version
elif [[ -f /opt/rocm/.info/version-rocm ]]; then
  cp /opt/rocm/.info/version-rocm /opt/rocm/.info/version
else
  echo "7.2.1" >/opt/rocm/.info/version
fi

echo "[rocm-wsl] Verifying packages..."
dpkg -l | grep -E 'rocm|rocr4wsl|rocdxg' || true

echo "[rocm-wsl] Running rocminfo..."
set +e
source /etc/profile.d/rocm-wsl.sh
rocminfo 2>&1 | head -40
rc=$?
set -e

if [[ $rc -ne 0 ]]; then
  echo "[rocm-wsl][warn] rocminfo did not succeed yet. Check driver and reboot WSL if needed." >&2
  exit 1
fi

echo "[rocm-wsl] Done."
