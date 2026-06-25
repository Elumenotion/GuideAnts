export type HostFolderMountScope = 'Notebook' | 'Project';

export type HostFolderMountStatus =
  | 'PendingRestart'
  | 'Active'
  | 'PendingRemoval'
  | 'Removed'
  | 'Error';

export type HostFolderMountLinkStatus =
  | 'PendingRestart'
  | 'PendingLink'
  | 'Linked'
  | 'Unlinked'
  | 'LinkError'
  | 'UnlinkError';

export type HostFolderMountDisplayState =
  | 'PendingRestart'
  | 'Linked'
  | 'MissingSource'
  | 'LinkError'
  | 'PendingRemoval';

export interface HostFolderMountSummaryDto {
  mountId: string;
  projectId: string;
  notebookId: string | null;
  scope: HostFolderMountScope;
  sourceKind: string;
  displayName: string;
  leafName: string;
  mountKey: string;
  containerSourcePath: string;
  status: HostFolderMountStatus;
  createdUtc: string;
  updatedUtc: string;
  errorMessage: string | null;
}

export interface HostFolderMountLinkSummaryDto {
  linkId: string;
  notebookId: string;
  linkRelativePath: string;
  status: HostFolderMountLinkStatus;
  errorMessage: string | null;
}

export interface HostFolderMountDetailDto extends HostFolderMountSummaryDto {
  removedUtc: string | null;
  hostPath: string | null;
  networkDevice: string | null;
  networkOptions: string | null;
  credentialRef: string | null;
  links: HostFolderMountLinkSummaryDto[];
}

export interface HostFolderMountCreateRequest {
  notebookId?: string;
  scope: HostFolderMountScope;
  hostPath: string;
  leafName?: string;
}

export interface HostFolderMountCreateResponse {
  mountId: string;
  status: HostFolderMountStatus;
  leafName: string;
  containerSourcePath: string;
  command: string;
}

export interface HostFolderMountCommandResponse {
  mountId: string;
  status: HostFolderMountStatus;
  command: string;
  leafName?: string;
  containerSourcePath?: string;
}

export interface HostFolderMountReconcileResponse {
  mountId: string;
  status: HostFolderMountStatus;
  message: string;
}

export interface NotebookHostMountEntry {
  mountId: string;
  leafName: string;
  relativePath: string;
  displayName: string;
  displayState: HostFolderMountDisplayState;
  mountStatus: HostFolderMountStatus;
  scope: HostFolderMountScope;
  linkStatus: HostFolderMountLinkStatus | null;
}

export interface ProjectHostMountEntry {
  mountId: string;
  leafName: string;
  displayName: string;
  displayState: HostFolderMountDisplayState;
  mountStatus: HostFolderMountStatus;
}
