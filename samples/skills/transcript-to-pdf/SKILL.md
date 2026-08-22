---
name: transcript-to-pdf
description: "Convert a GuideAnts / Code-Executor markdown conversation transcript into a styled, print-ready PDF with a cover page, role-badged turns, full tool-call inputs (heredoc-aware) and highlighted code. Use when the user wants to export, archive, or share a conversation log as a polished PDF document."
metadata:
  guideants:
    enabled: true
    display_order: 40
    requires_toolsets: [sandbox]
---

# transcript-to-pdf

Turns a **markdown conversation transcript** (the kind produced by saving a
GuideAnts / Code-Executor session) into a well-formatted PDF.

The renderer understands the transcript system, not generic markdown: it
recognises `**User**` / `**Assistant**` / `**Tool**` turns, `**Tool Call:**`
input blocks, and tool OUTPUT JSON, and lays each out as a distinct card.
Heredoc script inputs (e.g. `cat > file <<EOF`) are split so the bash header
and the written-file body are highlighted with the correct language lexer.

**Stdlib + weasyprint only.** No network. No model download. Runs in seconds.

## Dependencies

Required (already present in the sandbox image):

```text
weasyprint   # HTML -> PDF
pygments     # code highlighting
pypdf        # (optional) page inspection
```

Check with `python3 -c "import weasyprint, pygments"`. If missing:
`pip install weasyprint pygments`.

## What to run

Single command, one input, one output:

```bash
python3 Output/Skills/transcript-to-pdf/scripts/md_transcript_to_pdf.py \
  <transcript>.md -o <out>.pdf
```

- Output defaults to `<input>.pdf` when `-o` is omitted.
- The script prints a short report (title, turn counts, tool-call counts,
  fence fixes, output path) and exits 0 on success.

### Useful flags

```bash
python3 .../md_transcript_to_pdf.py in.md --no-cover   # drop the cover page
python3 .../md_transcript_to_pdf.py in.md --html out.html   # also dump the intermediate HTML (debug)
python3 .../md_transcript_to_pdf.py in.md --quiet       # suppress the report
```

## Layout guarantees

- **Cover page** — title, subtitle, stat grid (messages / users / assistants /
  tool calls / heredocs / source lines), metadata table, and a list of the
  session's user requests. The list region is a bounded flex box
  (`flex:1 1 0; min-height:0; overflow:hidden`) so it **cannot collide with the
  footer** regardless of how long the session is. Snippets are capped (4 shown,
  "+ N more" otherwise) and shortened to ~140 chars to keep the common case clean.
- **Turns** — every message is a role-badged card (User / Assistant / Tool).
- **Tool-call inputs** — `**Tool Call:**` blocks are anchored to the following
  JSON fence; the `script` field is unescaped and, if it is a heredoc, rendered
  as (bash header) + (file body in the right language) + (bash footer).
- **Tool outputs** — the `**Tool**` result JSON renders in an amber panel.
- **Markdown** — headings, bold/italic, inline code, lists, and pipe tables.
- **Truncated-fence repair** — turns that end mid-code-fence get a closing
  fence inserted so the document balances.

## Inputs it expects

A `.md` file in the saved-conversation shape:

```
# Title
**Created:** ...            <- optional "Key: value" metadata
**Last Activity:** ...
**Assistant:** ...
---
**User**
<message>
---
**Assistant** (Code Executor)
<message / reasoning>
**Tool Call:** `run_bash`
```json
{ "script": "cat > file <<'EOF' ... EOF" }
```
---
**Tool** (run_bash)
{"StandardOutput":"...","ExitCode":0,...}
---
```

See `references/transcript-format.md` for the exact structural contract and
the quirks the parser auto-repairs.

## Failure modes

- **No `# Title`** — the base filename is used as the title. Safe.
- **Unbalanced code fences** (log cut mid-block) — auto-closed; the report
  shows `Fences fixed: N`.
- **Malformed tool-call JSON** — falls back to rendering the raw fence body as
  text; the rest of the document still renders.
- **weasyprint missing** — clear `ModuleNotFoundError`; install it and re-run.

## Reporting

End by stating the output path, the turn/tool-call counts from the report, and
how many truncated fences were auto-repaired.
