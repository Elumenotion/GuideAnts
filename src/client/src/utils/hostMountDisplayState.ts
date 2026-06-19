import type {
  HostFolderMountDisplayState,
  HostFolderMountLinkStatus,
  HostFolderMountStatus,
  HostFolderMountDetailDto,
  HostFolderMountSummaryDto,
  NotebookHostMountEntry,
} from '../types/hostFolderMount';

export function deriveHostMountDisplayState(
  mountStatus: HostFolderMountStatus,
  linkStatus: HostFolderMountLinkStatus | null,
  linkErrorMessage: string | null | undefined,
  mountErrorMessage: string | null | undefined,
): HostFolderMountDisplayState {
  if (mountStatus === 'PendingRemoval') {
    return 'PendingRemoval';
  }

  if (linkStatus === 'LinkError' || linkStatus === 'UnlinkError' || mountStatus === 'Error') {
    return 'LinkError';
  }

  if (linkErrorMessage === 'Missing source' || mountErrorMessage === 'Missing source') {
    return 'MissingSource';
  }

  if (linkStatus === 'Linked' && mountStatus === 'Active') {
    return 'Linked';
  }

  return 'PendingRestart';
}

export function buildNotebookHostMountEntry(
  summary: HostFolderMountSummaryDto,
  detail: HostFolderMountDetailDto,
  notebookId: string,
): NotebookHostMountEntry | null {
  const link = detail.links.find((entry) => entry.notebookId === notebookId) ?? null;
  if (summary.scope === 'Notebook' && summary.notebookId !== notebookId) {
    return null;
  }
  if (summary.scope === 'Project' && !link) {
    return null;
  }

  const relativePath = link?.linkRelativePath || summary.leafName;

  return {
    mountId: summary.mountId,
    leafName: summary.leafName,
    relativePath,
    displayName: summary.displayName,
    displayState: deriveHostMountDisplayState(
      summary.status,
      link?.status ?? null,
      link?.errorMessage,
      summary.errorMessage,
    ),
    mountStatus: summary.status,
    scope: summary.scope,
    linkStatus: link?.status ?? null,
  };
}

export function isPathInsideHostMount(relativePath: string, mountRelativePath: string): boolean {
  if (!relativePath || !mountRelativePath) {
    return false;
  }
  return relativePath === mountRelativePath || relativePath.startsWith(`${mountRelativePath}/`);
}

export const HOST_MOUNT_DISPLAY_STATE_LABELS: Record<HostFolderMountDisplayState, string> = {
  PendingRestart: 'Pending restart',
  Linked: 'Linked',
  MissingSource: 'Missing source',
  LinkError: 'Link error',
  PendingRemoval: 'Pending removal',
};

export const HOST_MOUNT_DISPLAY_STATE_CLASSES: Record<HostFolderMountDisplayState, string> = {
  PendingRestart: 'bg-amber-100 text-amber-800 border-amber-200',
  Linked: 'bg-green-100 text-green-800 border-green-200',
  MissingSource: 'bg-orange-100 text-orange-800 border-orange-200',
  LinkError: 'bg-red-100 text-red-800 border-red-200',
  PendingRemoval: 'bg-slate-100 text-slate-700 border-slate-300',
};
