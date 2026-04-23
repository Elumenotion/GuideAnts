import type { NotebookToolbarServiceDto } from '../../../types/notebookToolbar';

export const WORKSPACE_CONTROLS_COPY =
  'Workspace controls apply to this entire workspace, not one notebook.';

export function statusToneClass(status: string): string {
  const normalized = status.trim().toLowerCase();
  if (normalized === 'ready') return 'text-emerald-700';
  if (normalized === 'blocked') return 'text-red-700';
  if (normalized === 'off') return 'text-slate-500';
  if (normalized === 'inprogress' || normalized === 'in progress') return 'text-blue-700';
  return 'text-amber-700';
}

export function statusDotClass(status: string): string {
  const normalized = status.trim().toLowerCase();
  if (normalized === 'ready') return 'bg-emerald-500';
  if (normalized === 'blocked') return 'bg-red-500';
  if (normalized === 'off') return 'bg-slate-400';
  if (normalized === 'inprogress' || normalized === 'in progress') return 'bg-blue-500';
  return 'bg-amber-500';
}

export function serviceSummaryLine(service: NotebookToolbarServiceDto): string {
  return service.summary;
}
