#!/bin/sh
set -eu

log() {
  printf '[move] %s\n' "$*"
}

die() {
  printf '[move] ERROR: %s\n' "$*" >&2
  exit 1
}

byte_total() {
  root="$1"
  find "$root" -type f -print0 2>/dev/null | xargs -0 stat -c '%s' 2>/dev/null | awk '{s+=$1} END {print s+0}'
}

file_count() {
  root="$1"
  find "$root" -type f 2>/dev/null | wc -l | tr -d ' '
}

require_file_size() {
  path="$1"
  expected="$2"
  [ -f "$path" ] || die "missing file: $path"
  actual="$(stat -c '%s' "$path")"
  [ "$actual" = "$expected" ] || die "size mismatch for $path (expected $expected, got $actual)"
}

copy_tree() {
  src="$1"
  dst="$2"
  label="$3"
  [ -d "$src" ] || die "source directory missing: $src"
  mkdir -p "$dst"
  src_files="$(file_count "$src")"
  src_bytes="$(byte_total "$src")"
  log "$label: copying $src_files files / $src_bytes bytes -> $dst"
  cp -a "$src"/. "$dst"/
  dst_bytes="$(byte_total "$dst")"
  log "$label: destination now has $(file_count "$dst") files / $dst_bytes bytes"
  [ "$dst_bytes" -ge "$src_bytes" ] || die "$label: destination smaller than source after copy"
}

remove_tree_contents() {
  src="$1"
  label="$2"
  [ -d "$src" ] || die "cannot remove missing source: $src"
  find "$src" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
  remaining="$(file_count "$src")"
  [ "$remaining" = "0" ] || die "$label: source still has $remaining files after delete"
  log "$label: source cleared at $src"
}

log "starting comfyui host -> models volume move"

copy_tree /src/comfyui /models "comfyui"
require_file_size /models/diffusion_models/qwen_image_edit_2511_fp8mixed.safetensors 20533762817
require_file_size /models/text_encoders/qwen_2.5_vl_7b_fp8_scaled.safetensors 9384670680
require_file_size /models/vae/qwen_image_vae.safetensors 253806246
require_file_size /models/loras/Qwen-Image-Edit-2511-Lightning-4steps-V1.0-bf16.safetensors 849608296
remove_tree_contents /src/comfyui "comfyui"

log "move complete"
