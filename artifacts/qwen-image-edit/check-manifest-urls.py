import json
from pathlib import Path

m = json.loads(Path("docker/build/comfyui-video/catalog/manifest.json").read_text(encoding="utf-8"))
bad = 0
for bundle in m["bundles"].values():
    for a in bundle["artifacts"]:
        expected = (
            f"https://huggingface.co/{a['repository']}/resolve/{a['revision']}/{a['file']}"
        )
        ok = a["url"] == expected
        print(("OK" if ok else "BAD"), a["id"])
        if not ok:
            bad += 1
            print("  expected", expected)
            print("  got     ", a["url"])
raise SystemExit(bad)
