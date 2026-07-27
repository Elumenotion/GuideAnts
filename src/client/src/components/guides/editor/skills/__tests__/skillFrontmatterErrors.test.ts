import { describe, expect, it } from 'vitest';
import { parseSkillFrontmatter } from '../skillFrontmatter';
import {
  formatSkillFrontmatterParseError,
  repairUnquotedDescriptionMarkdown,
  SkillFrontmatterParseError,
} from '../skillFrontmatterErrors';

const brokenDescriptionMarkdown = `---
name: audit-effective-sampler
description: Human-in-the-loop sample selection: consults audit methodology for population-data [--control-frequency FREQ] [--risk-level RISK]
metadata:
  guideants:
    enabled: true
---
# Body
`;

describe('skillFrontmatterErrors', () => {
  it('detects unquoted description values with colons', () => {
    expect(() => parseSkillFrontmatter(brokenDescriptionMarkdown)).toThrow(SkillFrontmatterParseError);

    try {
      parseSkillFrontmatter(brokenDescriptionMarkdown);
    } catch (error) {
      const details = (error as SkillFrontmatterParseError).details;
      expect(details.title).toMatch(/description/i);
      expect(details.problem).toMatch(/colon/i);
      expect(details.fix).toMatch(/quote/i);
      expect(details.location?.line).toBe(2);
      expect(details.canRepair).toBe(true);
      expect(details.repairedMarkdown).toContain('description: "Human-in-the-loop sample selection:');
      expect(details.snippetLines.some((line) => line.text.startsWith('description:'))).toBe(true);
    }
  });

  it('repairs unquoted description markdown', () => {
    const repaired = repairUnquotedDescriptionMarkdown(brokenDescriptionMarkdown);
    expect(repaired).toBeTruthy();

    const parsed = parseSkillFrontmatter(repaired!);
    expect(parsed.frontmatter.name).toBe('audit-effective-sampler');
    expect(parsed.frontmatter.description).toContain('Human-in-the-loop sample selection:');
  });

  it('formats generic yaml errors with location and snippet', () => {
    const yamlText = 'name: broken\nmetadata:\n  enabled: true\n bad: value';
    const details = formatSkillFrontmatterParseError(
      new Error('bad indentation of a mapping entry (4:2)'),
      `---\n${yamlText}\n---\n`,
      yamlText,
    );

    expect(details.title).toMatch(/Invalid YAML/i);
    expect(details.location).toEqual({ line: 4, column: 2 });
    expect(details.snippetLines.length).toBeGreaterThan(0);
  });

  it('leaves already quoted descriptions unchanged', () => {
    const markdown = `---
name: quoted
description: "Already quoted: value"
---
body
`;

    expect(repairUnquotedDescriptionMarkdown(markdown)).toBeNull();
    expect(() => parseSkillFrontmatter(markdown)).not.toThrow();
  });
});
