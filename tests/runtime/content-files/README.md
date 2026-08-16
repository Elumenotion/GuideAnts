# InfiniteTalk acceptance ContentFiles

This directory is the default host bind root used by the InfiniteTalk
acceptance harness. Generated runtime content is ignored; the harness recreates
this deterministic authorization tree on every run:

```text
acceptance-project/authorized-notebook/
  .guideants/notebook.json
  Input/avatar.png
  Input/voice.wav
  Output/
```

The manifest always uses project ID
`11111111-1111-1111-1111-111111111111` and notebook ID
`22222222-2222-2222-2222-222222222222`. The source assets must be committed
under `tests/assets/infinitetalk`; the harness never manufactures or downloads
substitute media.

Runtime transcripts and preserved MP4 files are written under
`artifacts/infinitetalk/`, outside this fixture directory.
