#!/bin/sh
set -eu
mkdir -p /opt/ComfyUI/user/default/workflows/guideants /opt/ComfyUI/user/default/workflows/guideants-jobs
python /opt/guideants/comfyui-video/scripts/publish-comfy-workflow-templates.py
chown -R guideants:guideants /opt/ComfyUI/user/default/workflows
ls -la /opt/ComfyUI/user/default/workflows/guideants/
