#!/usr/bin/env bash
set -euo pipefail

START_SERVICE=0
VIDEO_HOST="${VIDEO_HOST:-http://127.0.0.1:8189}"
SCRIPT_AGENT_TOKEN="${SCRIPT_AGENT_TOKEN:-local-script-agent-test-token}"
VIDEO_ADMIN_TOKEN="${VIDEO_ADMIN_TOKEN:-local-video-admin-test-token}"
COMPOSE_FILE="${COMPOSE_FILE:-docker/compose/comfyui-video-cuda13.standalone.yml}"
CONTENT_FILES_ROOT="${CONTENT_FILES_ROOT:-tests/runtime/content-files}"
ARTIFACTS_ROOT="${ARTIFACTS_ROOT:-artifacts/infinitetalk}"
READY_TIMEOUT_SECONDS="${READY_TIMEOUT_SECONDS:-1800}"
JOB_TIMEOUT_SECONDS="${JOB_TIMEOUT_SECONDS:-3600}"
POLL_SECONDS="${POLL_SECONDS:-10}"

usage() {
  echo "Usage: $0 [--start-service] [--video-host URL] [--content-files-root PATH]"
}

while (($#)); do
  case "$1" in
    --start-service) START_SERVICE=1; shift ;;
    --video-host) VIDEO_HOST="$2"; shift 2 ;;
    --content-files-root) CONTENT_FILES_ROOT="$2"; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

PROJECT_ID="11111111-1111-1111-1111-111111111111"
NOTEBOOK_ID="22222222-2222-2222-2222-222222222222"
GUIDE_ID="33333333-3333-3333-3333-333333333333"
OUTPUT_NAME="sample-cuda13-rtx5090.mp4"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

repo_path() {
  case "$1" in
    /*) printf '%s\n' "$1" ;;
    *) printf '%s\n' "$REPO_ROOT/$1" ;;
  esac
}

require_file() {
  [[ -f "$1" && -s "$1" ]] || { echo "$2" >&2; echo "Missing or empty: $1" >&2; exit 1; }
}

command -v curl >/dev/null || { echo "curl is required." >&2; exit 1; }
command -v python3 >/dev/null || { echo "python3 is required." >&2; exit 1; }
[[ "$POLL_SECONDS" =~ ^[1-9][0-9]*$ ]] || { echo "POLL_SECONDS must be a positive integer." >&2; exit 1; }

ASSETS="$(repo_path tests/assets/infinitetalk)"
AVATAR="$ASSETS/avatar.png"
VOICE="$ASSETS/voice.wav"
require_file "$ASSETS/ASSET_PROVENANCE.md" "Asset provenance guidance is required."
require_file "$AVATAR" "Licensed avatar.png is not committed. Complete ASSET_PROVENANCE.md before running acceptance."
require_file "$VOICE" "Licensed voice.wav is not committed. Complete ASSET_PROVENANCE.md before running acceptance."

python3 - "$AVATAR" "$VOICE" <<'PY'
import pathlib, sys
avatar, voice = map(pathlib.Path, sys.argv[1:])
if not avatar.read_bytes().startswith(b"\x89PNG\r\n\x1a\n"):
    raise SystemExit("avatar.png does not have a PNG signature.")
wav = voice.read_bytes()[:12]
if len(wav) != 12 or wav[:4] != b"RIFF" or wav[8:] != b"WAVE":
    raise SystemExit("voice.wav does not have a RIFF/WAVE signature.")
PY
command -v ffprobe >/dev/null || { echo "ffprobe is required to verify the generated MP4." >&2; exit 1; }

CONTENT_ROOT="$(repo_path "$CONTENT_FILES_ROOT")"
NOTEBOOK_ROOT="$CONTENT_ROOT/acceptance-project/authorized-notebook"
INPUT_DIR="$NOTEBOOK_ROOT/Input"
OUTPUT_DIR="$NOTEBOOK_ROOT/Output"
METADATA_DIR="$NOTEBOOK_ROOT/.guideants"
ARTIFACT_DIR="$(repo_path "$ARTIFACTS_ROOT")"
mkdir -p "$INPUT_DIR" "$OUTPUT_DIR" "$METADATA_DIR" "$ARTIFACT_DIR"
printf '{"ProjectId":"%s","NotebookId":"%s"}\n' "$PROJECT_ID" "$NOTEBOOK_ID" \
  > "$METADATA_DIR/notebook.json"
cp "$AVATAR" "$INPUT_DIR/avatar.png"
cp "$VOICE" "$INPUT_DIR/voice.wav"
rm -f "$OUTPUT_DIR/$OUTPUT_NAME"

TRANSCRIPT="$ARTIFACT_DIR/acceptance-$(date +%Y%m%d-%H%M%S).log"
: > "$TRANSCRIPT"

curl_text() {
  local label="$1"; shift
  {
    printf '\n=== %s ===\n> curl' "$label"
    printf ' %q' "$@"
    printf '\n'
  } >> "$TRANSCRIPT"
  local body
  if ! body="$(curl --fail --silent --show-error "$@" 2> >(tee -a "$TRANSCRIPT" >&2))"; then
    echo "curl failed during '$label'. See $TRANSCRIPT" >&2
    return 1
  fi
  [[ -n "$body" ]] || { echo "'$label' returned an empty response." >&2; return 1; }
  printf '%s\n' "$body" >> "$TRANSCRIPT"
  printf '%s' "$body"
}

json_property() {
  local name="$1" context="$2"
  python3 -c 'import json,sys
obj=json.load(sys.stdin); value=obj.get(sys.argv[1])
if value is None or str(value).strip()=="":
    raise SystemExit(f"{sys.argv[2]} response is missing required {sys.argv[1]!r}.")
print(value)' "$name" "$context"
}

write_execute_request() {
  local path="$1" script="$2"
  python3 - "$path" "$script" "$PROJECT_ID" "$NOTEBOOK_ID" "$GUIDE_ID" <<'PY'
import json, pathlib, sys
path, script, project, notebook, guide = sys.argv[1:]
payload = {
    "script": script,
    "scriptType": "Python",
    "workingDirectory": "/app/ContentFiles/acceptance-project/authorized-notebook/Output",
    "projectId": project,
    "notebookId": notebook,
    "guideId": guide,
    "timeoutSeconds": 600,
}
pathlib.Path(path).write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
PY
}

sandbox_execute() {
  local label="$1" request="$2" response stdout exit_code
  response="$(curl_text "$label" \
    -H "X-Script-Agent-Token: $SCRIPT_AGENT_TOKEN" \
    -H "Content-Type: application/json" --data-binary "@$request" \
    "$VIDEO_HOST/sandbox/execute")"
  exit_code="$(printf '%s' "$response" | json_property exitCode "$label")"
  [[ "$exit_code" == "0" ]] || {
    printf '%s' "$response" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("standardError",""))' >&2
    return 1
  }
  stdout="$(printf '%s' "$response" | json_property standardOutput "$label")"
  python3 -c 'import json,sys; json.load(sys.stdin)' <<< "$stdout" ||
    { echo "'$label' stdout was not client JSON." >&2; return 1; }
  printf '%s' "$stdout"
}

if ((START_SERVICE)); then
  command -v docker >/dev/null || { echo "docker is required with --start-service." >&2; exit 1; }
  COMPOSE="$(repo_path "$COMPOSE_FILE")"
  require_file "$COMPOSE" "Standalone compose file is required with --start-service."
  export GA_CONTENT_FILES_HOST_PATH="$CONTENT_ROOT"
  export GA_SCRIPT_AGENT_TOKEN="$SCRIPT_AGENT_TOKEN"
  export GA_COMFYUI_VIDEO_ADMIN_TOKEN="$VIDEO_ADMIN_TOKEN"
  docker compose -f "$COMPOSE" up -d --no-deps comfyui-video
fi

curl_text "sandbox health" "$VIDEO_HOST/sandbox/health" >/dev/null
curl_text "video health" "$VIDEO_HOST/video/health" >/dev/null
curl_text "capabilities" "$VIDEO_HOST/video/v1/capabilities" >/dev/null

models="$(curl_text "models" -H "X-Video-Admin-Token: $VIDEO_ADMIN_TOKEN" \
  "$VIDEO_HOST/video/v1/models")"
models_ready="$(printf '%s' "$models" | json_property ready "models")"
ready_deadline=$((SECONDS + READY_TIMEOUT_SECONDS))
if [[ "$models_ready" != "true" ]]; then
install="$(curl_text "model install" \
  -H "X-Video-Admin-Token: $VIDEO_ADMIN_TOKEN" \
  -H "Content-Type: application/json" --data '{"bundle":"infinitetalk-i2v-v1"}' \
  "$VIDEO_HOST/video/v1/admin/models/install")"
install_id="$(printf '%s' "$install" | json_property installId "model install")"
deadline=$ready_deadline
while :; do
  install_status="$(curl_text "model install status" \
    -H "X-Video-Admin-Token: $VIDEO_ADMIN_TOKEN" \
    "$VIDEO_HOST/video/v1/admin/models/install/$install_id")"
  install_state="$(printf '%s' "$install_status" | json_property state "model install status" | tr '[:upper:]' '[:lower:]')"
  [[ "$install_state" == "completed" ]] && break
  [[ "$install_state" != "failed" && "$install_state" != "cancelled" ]] ||
    { echo "Model installation ended in state '$install_state'." >&2; exit 1; }
  ((SECONDS < deadline)) || { echo "Timed out waiting for model installation." >&2; exit 1; }
  sleep "$POLL_SECONDS"
done
else
  printf '\nmodels already ready; skipping install\n' >> "$TRANSCRIPT"
fi

until curl_text "video ready" "$VIDEO_HOST/video/ready" >/dev/null; do
  ((SECONDS < ready_deadline)) || { echo "Timed out waiting for video readiness." >&2; exit 1; }
  sleep "$POLL_SECONDS"
done

submit_request="$(repo_path tests/requests/infinitetalk/execute-sample.json)"
submit="$(sandbox_execute "submit" "$submit_request")"
job_id="$(printf '%s' "$submit" | json_property jobId submit)"

deadline=$((SECONDS + JOB_TIMEOUT_SECONDS))
status_request="$ARTIFACT_DIR/status-request.json"
while :; do
  status_script="from guideants_video_client import get_talking_head_job
import json
print(json.dumps(get_talking_head_job('$job_id'), separators=(',', ':')))"
  write_execute_request "$status_request" "$status_script"
  status="$(sandbox_execute "job status" "$status_request")"
  state="$(printf '%s' "$status" | json_property state "job status" | tr '[:upper:]' '[:lower:]')"
  progress="$(STATUS_JSON="$status" python3 - <<'PY'
import json, os
payload = json.loads(os.environ["STATUS_JSON"])
progress = payload.get("progress") or {}
message = progress.get("message") or payload.get("state") or ""
parts = [message]
node_class = progress.get("node_class")
if node_class:
    parts.append(f"node={node_class}")
step = progress.get("step")
max_steps = progress.get("max_steps")
if step is not None and max_steps is not None:
    parts.append(f"step={step}/{max_steps}")
print(" | ".join(parts))
PY
)"
  if [[ -n "$progress" ]]; then
    echo "[job $job_id] $progress"
  fi
  [[ "$state" == "completed" ]] && break
  [[ "$state" != "failed" && "$state" != "cancelled" ]] ||
    { echo "Video job ended in state '$state'." >&2; exit 1; }
  ((SECONDS < deadline)) || { echo "Timed out waiting for video job $job_id." >&2; exit 1; }
  sleep "$POLL_SECONDS"
done

materialize_request="$ARTIFACT_DIR/materialize-request.json"
materialize_script="from guideants_video_client import materialize_talking_head_result
import json
print(json.dumps(materialize_talking_head_result('$job_id', '$OUTPUT_NAME'), separators=(',', ':')))"
write_execute_request "$materialize_request" "$materialize_script"
sandbox_execute "materialize" "$materialize_request" >/dev/null

files="$(curl_text "sandbox files" \
  -H "X-Script-Agent-Token: $SCRIPT_AGENT_TOKEN" --get \
  --data-urlencode "directory=/app/ContentFiles/acceptance-project/authorized-notebook/Output" \
  --data-urlencode "projectId=$PROJECT_ID" --data-urlencode "notebookId=$NOTEBOOK_ID" \
  "$VIDEO_HOST/sandbox/files")"
python3 -c 'import sys; name=sys.argv[1]; body=sys.stdin.read()
if name not in body: raise SystemExit(f"/sandbox/files did not list {name}.")' \
  "$OUTPUT_NAME" <<< "$files"

HOST_OUTPUT="$OUTPUT_DIR/$OUTPUT_NAME"
require_file "$HOST_OUTPUT" "Materialized MP4 is missing from the host ContentFiles share."
python3 - "$HOST_OUTPUT" <<'PY'
import pathlib, sys
header = pathlib.Path(sys.argv[1]).read_bytes()[:12]
if len(header) < 12 or header[4:8] != b"ftyp":
    raise SystemExit("Host output is not an ISO Base Media/MP4 file.")
PY
ffprobe -v error -select_streams v:0 -show_entries stream=codec_name \
  -of default=noprint_wrappers=1:nokey=1 "$HOST_OUTPUT" >> "$TRANSCRIPT"
audio_duration="$(ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 "$INPUT_DIR/voice.wav")"
video_duration="$(ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 "$HOST_OUTPUT")"
duration_tolerance="0.5"
python3 - "$audio_duration" "$video_duration" "$duration_tolerance" <<'PY'
import sys
audio = float(sys.argv[1])
video = float(sys.argv[2])
tolerance = float(sys.argv[3])
delta = abs(video - audio)
if delta > tolerance:
    raise SystemExit(
        f"Output duration {video}s does not match audio duration {audio}s "
        f"(tolerance {tolerance}s)."
    )
PY
{
  echo "audio_duration_seconds=$audio_duration"
  echo "video_duration_seconds=$video_duration"
} >> "$TRANSCRIPT"
cp "$HOST_OUTPUT" "$ARTIFACT_DIR/$OUTPUT_NAME"
echo "Acceptance passed. Transcript: $TRANSCRIPT"
echo "Preserved MP4: $ARTIFACT_DIR/$OUTPUT_NAME"
