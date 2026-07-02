---
description: "Deliberately missing the required 'name' field to test import rejection."
---

# Invalid skill fixture

This SKILL.md is intentionally invalid. `SkillFrontmatter.Parse` requires both
`name` and `description`; this file omits `name` to verify the importer
rejects it explicitly instead of silently accepting a partial skill.
