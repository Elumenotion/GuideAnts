import { API_BASE_URL } from '../config/apiConfig';
import { withAuthFetchInit, withAuthHeaders } from './authService';
import { broadcastAuthExpired } from './authEvents';
import type {
  HostFolderMountCommandResponse,
  HostFolderMountCreateRequest,
  HostFolderMountCreateResponse,
  HostFolderMountDetailDto,
  HostFolderMountReconcileResponse,
  HostFolderMountSummaryDto,
} from '../types/hostFolderMount';

async function callHostFolderMountApi<T>(endpoint: string, options: RequestInit = {}): Promise<T> {
  const headers = withAuthHeaders(options.headers);
  if (!(typeof FormData !== 'undefined' && options.body instanceof FormData) && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json');
  }

  const response = await fetch(`${API_BASE_URL}${endpoint}`, withAuthFetchInit({
    ...options,
    headers,
  }));

  if (!response.ok) {
    if (response.status === 401) {
      broadcastAuthExpired('Authentication expired.');
    }
    let message = response.statusText;
    try {
      const body = await response.json() as { message?: string };
      if (body.message) {
        message = body.message;
      }
    } catch {
      // keep status text
    }
    throw new Error(message || `Request failed (${response.status})`);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return response.json() as Promise<T>;
}

export const hostFolderMountsApi = {
  list: (projectId: string) =>
    callHostFolderMountApi<HostFolderMountSummaryDto[]>(`/projects/${projectId}/host-folder-mounts`),

  getDetail: (projectId: string, mountId: string) =>
    callHostFolderMountApi<HostFolderMountDetailDto>(`/projects/${projectId}/host-folder-mounts/${mountId}`),

  create: (projectId: string, request: HostFolderMountCreateRequest) =>
    callHostFolderMountApi<HostFolderMountCreateResponse>(`/projects/${projectId}/host-folder-mounts`, {
      method: 'POST',
      body: JSON.stringify(request),
    }),

  getApplyCommand: (projectId: string, mountId: string) =>
    callHostFolderMountApi<HostFolderMountCommandResponse>(
      `/projects/${projectId}/host-folder-mounts/${mountId}/commands/apply`,
      { method: 'POST' },
    ),

  getRemoveCommand: (projectId: string, mountId: string) =>
    callHostFolderMountApi<HostFolderMountCommandResponse>(
      `/projects/${projectId}/host-folder-mounts/${mountId}/commands/remove`,
      { method: 'POST' },
    ),

  reconcile: (projectId: string, mountId: string) =>
    callHostFolderMountApi<HostFolderMountReconcileResponse>(
      `/projects/${projectId}/host-folder-mounts/${mountId}/reconcile`,
      { method: 'POST' },
    ),
};
