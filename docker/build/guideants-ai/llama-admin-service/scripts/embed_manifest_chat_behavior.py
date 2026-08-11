"""One-time helper: replace runtimeProfileId with embedded chatBehavior from bootstrap profiles."""
from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[5]
MANIFEST = ROOT / "docker/build/guideants-ai/llama-admin-service/catalog/manifest.json"
PROFILES_DIR = ROOT / "src/server/GuideAntsApi/Resources/bootstrap/runtime-profiles"


def profile_to_chat_behavior(profile: dict) -> dict:
    behavior: dict = {
        "combineSystemAndDeveloperMessages": profile["combineSystemAndDeveloperMessages"],
        "samplingParametersJson": profile["samplingParametersJson"],
        "thinkingControlJson": profile["thinkingControlJson"],
    }
    if profile.get("thoughtBlockPattern"):
        behavior["thoughtBlockPattern"] = profile["thoughtBlockPattern"]
    if profile.get("requestFieldsWhenToolsPresent"):
        behavior["requestFieldsWhenToolsPresent"] = profile["requestFieldsWhenToolsPresent"]
    return behavior


def main() -> None:
    profiles = {
        p.stem: json.loads(p.read_text(encoding="utf-8"))
        for p in PROFILES_DIR.glob("*.json")
    }
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    for model in manifest["models"]:
        defaults = model["defaults"]
        profile_id = defaults.pop("runtimeProfileId", None)
        if not profile_id:
            raise SystemExit(f"Model {model['id']} missing runtimeProfileId")
        profile = profiles.get(profile_id)
        if profile is None:
            raise SystemExit(f"Profile {profile_id} not found for model {model['id']}")
        defaults["chatBehavior"] = profile_to_chat_behavior(profile)
    MANIFEST.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Updated {MANIFEST} ({len(manifest['models'])} models)")


if __name__ == "__main__":
    main()
