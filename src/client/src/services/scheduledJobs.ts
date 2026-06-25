import { API_BASE_URL } from '../config/apiConfig';
import { withAuthFetchInit, withAuthHeaders } from './authService';
import { broadcastAuthExpired } from './authEvents';
import type {
  CreateProjectScheduledJobRequest,
  PagedProjectScheduledJobRunsDto,
  ProjectScheduledJobDetailDto,
  ProjectScheduledJobRunDetailDto,
  ProjectScheduledJobSummaryDto,
  UpdateProjectScheduledJobRequest,
} from '../types/scheduledJob';

async function callScheduledJobsApi<T>(endpoint: string, options: RequestInit = {}): Promise<T> {
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

  if (response.status === 204 || response.status === 202) {
    return undefined as T;
  }

  return response.json() as Promise<T>;
}

export const scheduledJobsApi = {
  list: (projectId: string) =>
    callScheduledJobsApi<ProjectScheduledJobSummaryDto[]>(`/projects/${projectId}/scheduled-jobs`),

  get: (projectId: string, jobId: string) =>
    callScheduledJobsApi<ProjectScheduledJobDetailDto>(`/projects/${projectId}/scheduled-jobs/${jobId}`),

  create: (projectId: string, request: CreateProjectScheduledJobRequest) =>
    callScheduledJobsApi<ProjectScheduledJobDetailDto>(`/projects/${projectId}/scheduled-jobs`, {
      method: 'POST',
      body: JSON.stringify(request),
    }),

  update: (projectId: string, jobId: string, request: UpdateProjectScheduledJobRequest) =>
    callScheduledJobsApi<ProjectScheduledJobDetailDto>(`/projects/${projectId}/scheduled-jobs/${jobId}`, {
      method: 'PUT',
      body: JSON.stringify(request),
    }),

  delete: (projectId: string, jobId: string) =>
    callScheduledJobsApi<void>(`/projects/${projectId}/scheduled-jobs/${jobId}`, {
      method: 'DELETE',
    }),

  runNow: (projectId: string, jobId: string) =>
    callScheduledJobsApi<void>(`/projects/${projectId}/scheduled-jobs/${jobId}/run`, {
      method: 'POST',
    }),

  listRuns: (projectId: string, jobId: string, page = 1, pageSize = 20) =>
    callScheduledJobsApi<PagedProjectScheduledJobRunsDto>(
      `/projects/${projectId}/scheduled-jobs/${jobId}/runs?page=${page}&pageSize=${pageSize}`,
    ),

  getRun: (projectId: string, jobId: string, runId: string) =>
    callScheduledJobsApi<ProjectScheduledJobRunDetailDto>(
      `/projects/${projectId}/scheduled-jobs/${jobId}/runs/${runId}`,
    ),
};
