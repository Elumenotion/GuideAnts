import { describe, expect, it } from 'vitest';
import {
  buildCanonicalSkillMarkdown,
  parseSkillFrontmatter,
  updateSkillFrontmatterFlags,
} from '../skillFrontmatter';
import { SkillFrontmatterParseError } from '../skillFrontmatterErrors';

const sampleMarkdown = `---
name: demo-skill
description: Demo skill for tests.
metadata:
  guideants:
    enabled: true
    display_order: 2
    source: Imported
    requires_toolsets:
      - terminal
    requires_tools:
      - WebSearch
---

# Demo Skill

Body content.
`;

describe('skillFrontmatter', () => {
  it('parses guideants metadata and body content', () => {
    const parsed = parseSkillFrontmatter(sampleMarkdown);

    expect(parsed.frontmatter).toMatchObject({
      name: 'demo-skill',
      description: 'Demo skill for tests.',
      enabled: true,
      displayOrder: 2,
      requiresToolsets: ['terminal'],
      requiresTools: ['WebSearch'],
      source: 'Imported',
    });
    expect(parsed.body).toContain('# Demo Skill');
  });

  it('rejects invalid or oversized skill manifests', () => {
    expect(() => parseSkillFrontmatter('')).toThrow(/empty/i);
    expect(() => parseSkillFrontmatter('# No frontmatter')).toThrow(/missing yaml frontmatter/i);
    expect(() => parseSkillFrontmatter('---\nname: only-open')).toThrow(/not closed/i);
    expect(() => parseSkillFrontmatter(`---\nname: x\n---\n`)).toThrow(/missing required field 'description'/i);
  });

  it('truncates long descriptions to the database limit', () => {
    const longDescription = 'x'.repeat(1100);
    const parsed = parseSkillFrontmatter(`---\nname: long-desc\ndescription: ${longDescription}\n---\nbody\n`);

    expect(parsed.frontmatter.description).toHaveLength(1024);
  });

  it('builds canonical markdown with optional prerequisite lists', () => {
    const markdown = buildCanonicalSkillMarkdown({
      name: 'built-skill',
      description: 'Built from tests.',
      enabled: false,
      displayOrder: 4,
      body: '# Built\n',
      source: 'Imported',
      requiresToolsets: ['web'],
      requiresTools: ['ReadWeb'],
    });

    const parsed = parseSkillFrontmatter(markdown);
    expect(parsed.frontmatter.name).toBe('built-skill');
    expect(parsed.frontmatter.enabled).toBe(false);
    expect(parsed.frontmatter.displayOrder).toBe(4);
    expect(parsed.frontmatter.requiresToolsets).toEqual(['web']);
    expect(parsed.frontmatter.requiresTools).toEqual(['ReadWeb']);
  });

  it('updates enabled and display order flags in existing markdown', () => {
    const updated = updateSkillFrontmatterFlags(sampleMarkdown, false, 9);
    const parsed = parseSkillFrontmatter(updated);

    expect(parsed.frontmatter.enabled).toBe(false);
    expect(parsed.frontmatter.displayOrder).toBe(9);
    expect(parsed.body).toContain('# Demo Skill');
  });

  it('falls back to hermes metadata and coalesces prerequisite lists', () => {
    const markdown = `---
name: hermes-skill
description: Hermes metadata path.
metadata:
  hermes:
    enabled: false
    display_order: 5
    source: Hub
    requires_toolsets:
      - web
    fallback_for_tools:
      - browser_navigate
allowed-tools:
  - read_file
---

Body
`;

    const parsed = parseSkillFrontmatter(markdown);
    expect(parsed.frontmatter).toMatchObject({
      enabled: false,
      displayOrder: 5,
      source: 'Hub',
      requiresToolsets: ['web'],
      fallbackForTools: ['browser_navigate'],
      requiresTools: ['read_file'],
    });
  });

  it('rejects oversized skill manifests and empty frontmatter blocks', () => {
    const oversizedBody = 'x'.repeat(100_001);
    expect(() => parseSkillFrontmatter(`---\nname: big\n---\n${oversizedBody}`)).toThrow(
      /maximum length/i,
    );
    expect(() => parseSkillFrontmatter('---\n---\n')).toThrow(SkillFrontmatterParseError);
    expect(() => parseSkillFrontmatter('---\ndescription: missing name\n---\n')).toThrow(
      /missing required field 'name'/i,
    );
  });

  it('reads names from nested metadata and coerces scalar prerequisite lists', () => {
    const markdown = `---
description: Guideants metadata name path.
metadata:
  guideants:
    name: guideants-name
    requires_toolsets: terminal
    requires_tools: read_file
---

# Body without top-level name
`;

    const parsed = parseSkillFrontmatter(markdown);
    expect(parsed.frontmatter.name).toBe('guideants-name');
    expect(parsed.frontmatter.requiresToolsets).toEqual(['terminal']);
    expect(parsed.frontmatter.requiresTools).toEqual(['read_file']);
    expect(parsed.body).toContain('# Body without top-level name');
  });

  it('coerces non-string frontmatter values into strings', () => {
    const markdown = `---
name: 42
description: numeric name skill
---

Body
`;

    const parsed = parseSkillFrontmatter(markdown);
    expect(parsed.frontmatter.name).toBe('42');
  });
});
