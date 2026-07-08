#!/usr/bin/env python3
"""Rewrite commit messages: drop Claude co-authors; fix PR #61 squash message."""
import re
import sys

PR_61_MESSAGE = """Bug/assistants from skill (#61)

Convert guide skills to crew assistants with notebook payload

Create-from-skill now produces a standalone assistant (instructions + materialized
Skill scripts/assets) instead of attaching a skill package. SKILL.md stays out of
assistant files; payload paths are copied as FolderKind=Skill rows for notebook
materialization.

Auto-seed the files -> [@files] context option when notebook payload files are
added (create, create-from-skill, or new uploads), matching bootstrap assistants.
Users can still remove it; updates do not re-add unless new payload files arrive.

Server: surface Skill payload files on assistant GET; retain them on assistant
update via fileIdsToKeep; hide skills DTO for assistants. Client: direct create
flow, Skills tab guides-only, show Skill-origin files in Files tab, assistant
file download API.

Also tighten ModelsTab table layout and add LocalServiceModelRefRules for local
model ref validation tests.

Co-authored-by: Jackson Falgoust <jackson.falgoust06@gmail.com>
"""

msg = sys.stdin.read()

first_line = msg.split("\n", 1)[0].strip()
if first_line == "Bug/assistants from skill (#61)":
    sys.stdout.write(PR_61_MESSAGE)
    sys.exit(0)

cleaned = re.sub(
    r"(?im)^Co-[Aa]uthored-[Bb]y:\s*Claude\b.*(?:\r?\n)?",
    "",
    msg,
)
cleaned = re.sub(r"\n{3,}", "\n\n", cleaned).rstrip() + "\n"
sys.stdout.write(cleaned)
