import type { LocalModelOnboardingStatus } from './contracts';

export type LocalModelOnboardingProgressStepId =
  | 'queued'
  | 'resolvingFiles'
  | 'downloading'
  | 'registeringAlias'
  | 'catalogFinalization'
  | 'completed';

function normalizeStatusToken(status: string): string {
  return (status ?? '').trim();
}

export function normalizeLocalModelOnboardingStatus(status: string): LocalModelOnboardingStatus {
  const normalized = normalizeStatusToken(status);
  if (normalized === 'queued') return 'queued';
  if (normalized === 'resolving' || normalized === 'resolvingFiles') return 'resolvingFiles';
  if (normalized === 'downloading') return 'downloading';
  if (normalized === 'registering' || normalized === 'registeringAlias') return 'registeringAlias';
  if (normalized === 'catalogFinalization' || normalized === 'provenanceFinalization') return 'registeringAlias';
  if (normalized === 'completed') return 'completed';
  if (normalized === 'failed' || normalized === 'error') return 'error';
  return 'downloading';
}

export function isLocalModelOnboardingInFlight(status: LocalModelOnboardingStatus): boolean {
  return status === 'submitted'
    || status === 'queued'
    || status === 'resolvingFiles'
    || status === 'downloading'
    || status === 'registeringAlias';
}

export function isLocalModelOnboardingTerminal(status: string): boolean {
  const normalized = (status ?? '').trim().toLowerCase();
  return normalized === 'completed' || normalized === 'failed' || normalized === 'error';
}

export function localModelOnboardingProgressStep(status: string): LocalModelOnboardingProgressStepId {
  const normalized = normalizeStatusToken(status);
  if (normalized === 'catalogFinalization' || normalized === 'provenanceFinalization') {
    return 'catalogFinalization';
  }
  const mapped = normalizeLocalModelOnboardingStatus(status);
  if (mapped === 'queued') return 'queued';
  if (mapped === 'resolvingFiles') return 'resolvingFiles';
  if (mapped === 'downloading') return 'downloading';
  if (mapped === 'registeringAlias') return 'registeringAlias';
  if (mapped === 'completed') return 'completed';
  return 'downloading';
}
