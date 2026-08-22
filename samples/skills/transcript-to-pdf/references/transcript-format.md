# Transcript structural contract

The parser (`md_transcript_to_pdf.py`) recognises these line shapes. Everything
else is treated as markdown prose.

## Turn boundaries

| Line | Regex | Meaning |
|------|-------|---------|
| `**User**` / `**Assistant**` | `^\*\*(User\|Assistant)\*\*(?:\s*\([^)]*\))?\s*$` | start of a role turn; optional `(meta)` in parens (e.g. `(Code Executor)`) |
| `**Tool** (name)` | `^\*\*Tool\*\*\s*\(([^)]*)\)\s*$` | a tool OUTPUT turn; the name is the tool that produced it |
| `**Tool Call:** `tool`` | `^\*\*Tool Call:\*\*\s*\`([^\`]+)\`\s*$` | marks a tool-call INPUT; the next two fence lines must hold a JSON object whose body starts with `{` |
| `---` | `^-{3,}$` | separator between turns (also used to place an auto-closing fence) |
| `**Key:** value` | `^\*(.+?):\*\*\s*(.*)$` | metadata line in the header (before the first turn) |

## Two independent code tracks

Tool-call inputs and ordinary code blocks are parsed on **separate tracks** so
they can't corrupt each other:

1. **Track 1 — tool-call inputs.** Anchored on the `**Tool Call:**` marker:
   the next two fence lines are the input block; it is consumed and the body
   (JSON) is unescaped. If the JSON has a `script` string containing a
   heredoc, `split_heredoc` separates header / command / file body / footer and
   the file body is highlighted by the target extension
   (`.js`/`.ts` → javascript, `.py` → python, `.html` → html, `.sh` → bash,
   `.json` → text).
2. **Track 2 — markdown fences.** Standard rule: a fence opens a block only if
   preceded by a blank line; the next fence closes it. Indented/inline fences
   in prose stay prose. Tool-call fence lines are never seen here.

## Quirks auto-repaired

- **Truncated code fence.** If a region between two role markers has an *odd*
  count of fence lines (log cut mid-block), a closing ` ``` ` is inserted before
  the region's last `---`. The report's `Fences fixed: N` counts these.
- **Unclosed fence at EOF.** If the file ends inside an open fence, the block
  runs to the last line and is rendered as code.
- **Tool JSON in prose.** A prose paragraph that is a single-line
  `{"StandardOutput":...}` or `{"exitCode":...}` JSON blob is rendered as an
  amber TOOL OUTPUT panel rather than plain text.

## Cover page layout (why it can't overflow)

The cover is a fixed-height (`247mm`) flex column:

```
.cover            height:247mm; display:flex; column; overflow:hidden
  .cover-accent   fixed height bar
  .cover-body     flex:1; min-height:0; column
    .cover-kicker / h1 / .cover-sub / .cover-meta / .stat-row(s)   (fixed-size)
    .cover-contents  flex:1 1 0; min-height:0; overflow:hidden   <- the list
    .cover-foot     footer (fixed at bottom)
```

The linchpin is `.cover-contents { min-height:0; overflow:hidden }`:
`min-height:0` lets the list shrink below its natural content height, and
`overflow:hidden` clips *inside the list's own box*, so the footer (the last
child) is always pinned at the bottom and can never collide with list items.
The list is also capped at 4 visible snippets (~140 chars each) with a
"+ N more requests…" row, so the common case fits with nothing clipped.

## CLI

```
python3 md_transcript_to_pdf.py INPUT.md [-o OUTPUT.pdf]
                               [--no-cover] [--html PATH] [--quiet]
```

Exit 0 on success; the report (unless `--quiet`) prints title, deliverable,
turn/role counts, tool-call / heredoc / inline-code counts, fences fixed,
source-line count, and the output path.
