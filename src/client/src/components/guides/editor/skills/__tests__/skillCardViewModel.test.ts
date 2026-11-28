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
      catalogBadge: null,
    });
  });

  it('shows a catalog badge only for pinned store installs', () => {
    const local = buildSkillCardViewModel('Imported', satisfiedGating, []);
    expect(local.catalogBadge).toBeNull();

    const pinned = buildSkillCardViewModel(
      'Imported',
      satisfiedGating,
      [],
      { catalogSkillId: 'catalog-1', catalogVersion: 2, catalogCurrentVersion: 2 },
    );
    expect(pinned.catalogBadge).toEqual({ label: 'Store v2', className: expect.stringContaining('emerald') });

    const stale = buildSkillCardViewModel(
      'Imported',
      satisfiedGating,
      [],
      { catalogSkillId: 'catalog-1', catalogVersion: 1, catalogCurrentVersion: 3 },
    );
    expect(stale.catalogBadge?.label).toBe('Store v1 (update available)');
  });
});
