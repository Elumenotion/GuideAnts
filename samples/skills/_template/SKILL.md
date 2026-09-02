---
name: your-skill-name
description: "One-line description for skill discovery. Use when …"
metadata:
  guideants:
    enabled: true
    display_order: 100
    requires_toolsets: [sandbox]
---

# your-skill-name

Paths — fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. This skill's scripts live under
`Skills/your-skill-name/scripts/` relative to it, so run the commands in this file
exactly as written. Write every deliverable to the CWD with a **bare filename**
(e.g. `-o clip`, `-o scene.wav`): never prefix an output path with `Output/` —
the CWD *is* the output directory, so `Output/…` would create a nested
`Output/` folder.

See `docs/sandbox-path-contract.md` for the full path contract.

<!-- Skill-specific instructions below this line. -->
