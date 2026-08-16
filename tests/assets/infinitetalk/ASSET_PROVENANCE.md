# InfiniteTalk acceptance asset provenance

`avatar.png` and `voice.wav` are intentionally not present yet. Their origin
and redistribution rights have not been verified, so the acceptance harness
fails preflight until properly licensed files are committed beside this file.
Do not substitute downloaded, generated, or recorded media without documenting
its actual provenance.

Before committing the assets, replace the templates below with complete,
verifiable facts and record SHA-256 hashes for the exact committed bytes.

## `avatar.png`

- Subject: synthetic adult; must not be presented as a real person
- Creator/operator:
- Generation tool and version:
- Model and immutable version:
- Prompt:
- Generation date:
- Model/output use terms and URL:
- Repository redistribution basis:
- SHA-256:
- Pixel dimensions:

## `voice.wav`

- Speaker or dataset:
- Recorder/creator:
- Source URL and immutable dataset/file identifier:
- Recording date or dataset version:
- License and URL:
- Repository redistribution basis:
- Non-identification or attribution conditions:
- SHA-256:
- Codec/sample format:
- Sample rate/channels/duration:
- Transcript: `voice.txt`

## Validation

After both files are committed, confirm the recorded hashes:

```powershell
Get-FileHash tests/assets/infinitetalk/avatar.png -Algorithm SHA256
Get-FileHash tests/assets/infinitetalk/voice.wav -Algorithm SHA256
```

```bash
sha256sum tests/assets/infinitetalk/avatar.png \
  tests/assets/infinitetalk/voice.wav
```

The acceptance scripts validate the PNG and RIFF/WAVE signatures, require both
files to be non-empty, and treat missing assets as an error. They do not create,
download, or replace media.
