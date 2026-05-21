import type { LocalModelOnboardingStatus } from './contracts';

export type LocalModelOnboardingProgressStepId =
  | 'queued'
  | 'resolvingFiles'
  | 'downloading'
  | 'registeringAlias'
  | 'completed';

export function normalizeLocalModelOnboardingStatus(status: string): LocalModelOnboardingStatus {
  const normalized = (status ?? '').trim();
  if (normalized === 'queued') return 'queued';
  if (normalized === 'resolving' || normalized === 'resolvingFiles') return 'resolvingFiles';
  if (normalized === 'downloading') return 'downloading';
  if (normalized === 'registering' || normalized === 'registeringAlias') return 'registeringAlias';
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
  const normalized = normalizeLocalModelOnboardingStatus(status);
  if (normalized === 'queued') return 'queued';
  if (normalized === 'resolvingFiles') return 'resolvingFiles';
  if (normalized === 'downloading') return 'downloading';
  if (normalized === 'registeringAlias') return 'registeringAlias';
  if (normalized === 'completed') return 'completed';
  return 'downloading';
}
