import { describe, expect, it } from 'vitest';
import { computeSkillGating } from '../skillGating';
import {
  buildSkillCardViewModel,
  countSkillPayloadFiles,
  gatingBadgeClassName,
  sourceBadgeClassName,
} from '../skillCardViewModel';

describe('skillCardViewModel', () => {
  const satisfiedGating = computeSkillGating(
    {
      requiresToolsets: [],
      requiresTools: [],
      fallbackForToolsets: [],
      fallbackForTools: [],
    },
    new Set<string>(),
    false,
  );

  it('maps source and gating states to badge classes', () => {
    expect(sourceBadgeClassName('Authored')).toContain('blue');
    expect(sourceBadgeClassName('Bootstrap')).toContain('sky');
    expect(sourceBadgeClassName('Imported')).toContain('gray');
    expect(gatingBadgeClassName(satisfiedGating)).toContain('green');
    expect(
      gatingBadgeClassName({
        ...satisfiedGating,
        satisfied: false,
      }),
    ).toContain('amber');
  });

  it('counts payload files and builds the card view model', () => {
    const files = [
      { relativePath: 'skill/references/guide.md' },
      { relativePath: 'skill/scripts/run.py' },
      { relativePath: 'skill/assets/logo.png' },
      { relativePath: 'SKILL.md' },
    ];

    expect(countSkillPayloadFiles(files)).toBe(3);

    const viewModel = buildSkillCardViewModel('Authored', satisfiedGating, files);
    expect(viewModel).toEqual({
      sourceClassName: sourceBadgeClassName('Authored'),
      gatingClassName: gatingBadgeClassName(satisfiedGating),
      payloadFileCount: 3,
      gatingSummary: satisfiedGating.summary,
    });
  });
});
