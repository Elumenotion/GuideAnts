#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../lib/combined-hash.sh
source "$SCRIPT_DIR/../lib/combined-hash.sh"

scratch="$(mktemp -d "${TMPDIR:-/tmp}/ga-hash.XXXXXX")"
cleanup() { rm -rf "$scratch"; }
trap cleanup EXIT

worktree_a="$scratch/GuideAnts"
worktree_b="$scratch/GuideAnts-qwen38-27b-gguf"

for root in "$worktree_a" "$worktree_b"; do
  dir="$root/docker/build/guideants-ai"
  mkdir -p "$dir"
  printf 'numpy==2.0.0\n' > "$dir/asr-requirements.txt"
  printf 'tokenizers==0.21.0\n' > "$dir/emb-requirements.txt"
done

rel_a="$(get_stable_repo_relative_path "$worktree_a/docker/build/guideants-ai/asr-requirements.txt" "$worktree_a")"
rel_b="$(get_stable_repo_relative_path "$worktree_b/docker/build/guideants-ai/asr-requirements.txt" "$worktree_b")"
[[ "$rel_a" == "docker/build/guideants-ai/asr-requirements.txt" ]]
[[ "$rel_a" == "$rel_b" ]]

hash_a="$(get_combined_hash "$worktree_a" \
  "$worktree_a/docker/build/guideants-ai/emb-requirements.txt" \
  "$worktree_a/docker/build/guideants-ai/asr-requirements.txt")"
hash_b="$(get_combined_hash "$worktree_b" \
  "$worktree_b/docker/build/guideants-ai/asr-requirements.txt" \
  "$worktree_b/docker/build/guideants-ai/emb-requirements.txt")"
[[ "$hash_a" == "$hash_b" ]]

printf 'numpy==2.0.1\n' > "$worktree_b/docker/build/guideants-ai/asr-requirements.txt"
hash_b_changed="$(get_combined_hash "$worktree_b" \
  "$worktree_b/docker/build/guideants-ai/asr-requirements.txt" \
  "$worktree_b/docker/build/guideants-ai/emb-requirements.txt")"
[[ "$hash_a" != "$hash_b_changed" ]]

printf 'nope' > "$scratch/outside.txt"
if get_combined_hash "$worktree_a" "$scratch/outside.txt" 2>/dev/null; then
  echo "files outside the repo root must not be hashed" >&2
  exit 1
fi

echo "test_combined_hash.sh: passed"
