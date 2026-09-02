---
name: pptx-author
description: Build export-ready PowerPoint decks with clear outlines and speaker notes.
metadata:
  guideants:
    enabled: true
    display_order: 10
    source: bootstrap
    requires_toolsets: [sandbox]
---
# PowerPoint authoring

Paths — fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. This skill's scripts live under
`Skills/pptx-author/scripts/` relative to it. Write deliverables with
**bare filenames**; never prefix with `Output/`.

Use this skill when the user wants slide decks, speaker notes, or export-ready PPTX content.

1. Confirm audience, slide count, and tone.
2. Draft an outline (see `references/outline.md`).
3. Produce slide titles and bullet content before generating files.
