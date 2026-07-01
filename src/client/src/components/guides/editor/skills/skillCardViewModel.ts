import type { SkillSourceKind } from '../../../../types/guides';
import type { SkillGatingResult } from './skillGating';

export function sourceBadgeClassName(source: SkillSourceKind): string {
  switch (source) {
    case 'Authored':
      return 'bg-blue-100 text-blue-800';
    case 'Bootstrap':
      return 'bg-sky-100 text-sky-800';
    default:
      return 'bg-gray-100 text-gray-700';
  }
}

export function gatingBadgeClassName(gating: SkillGatingResult): string {
  return gating.satisfied ? 'bg-green-100 text-green-800' : 'bg-amber-100 text-amber-900';
}

export function countSkillPayloadFiles(files: { relativePath: string }[]): number {
  return files.filter((file) => {
    const path = file.relativePath.replace(/\\/g, '/');
    return path.includes('/references/')
      || path.includes('/scripts/')
      || path.includes('/assets/');
  }).length;
}

export interface SkillCardViewModel {
  sourceClassName: string;
  gatingClassName: string;
  payloadFileCount: number;
  gatingSummary: string;
}

export function buildSkillCardViewModel(
  source: SkillSourceKind,
  gating: SkillGatingResult,
  files: { relativePath: string }[],
): SkillCardViewModel {
  return {
    sourceClassName: sourceBadgeClassName(source),
    gatingClassName: gatingBadgeClassName(gating),
    payloadFileCount: countSkillPayloadFiles(files),
    gatingSummary: gating.summary,
  };
}
