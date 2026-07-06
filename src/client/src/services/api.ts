import { ProjectLink, ProjectDetailsDto, ContentFileDto, ProjectFolderDto, CreateFolderDto, UpdateFolderDto, FolderTreeDto, MoveFolderDto, MoveFileDto, ProjectNotebook, NotebookTemplateDto, NotebookTemplateSummaryDto, UpdateNotebookDto } from '../types/project';
import { UsageSummaryDto, UsageDetailsQuery, PagedResultDto, UsageEventDto, UsageBucket, ProjectUsageSummaryDto, UsageBreakdownWithCategoriesDto } from '../types/usage';
import type { UpdateCurrentUserRequest, UserDto } from '../types/user';
import type { AuthMeResponse, AuthResponse, ChangePasswordRequest, LoginRequest, RegisterRequest } from '../types/auth';
import { getCachedFile, cacheFile } from '../utils/fileCache';
import { FileLineageEvent } from '../types/fileLineage';
import type { NotebookChatReadinessDto } from '../types/notebookToolbar';
import {
    AddModelRequest,
    AddModelResponse,
    SettingsModelDto,
    SettingsReadinessDto,
    SettingsRuntimeProfileDto,
    SettingsSchemaDto,
    SettingsSectionDto,
    SettingsSectionSummaryDto,
    CreateRuntimeProfileRequest,
    UpdateSettingsModelRequest,
    UpdateRuntimeProfileRequest,
    UpdateSettingsSectionRequest,
    EmbeddingsRebuildResponse,
    LlamaRuntimeInventoryItemDto,
    LlamaRuntimeAliasStatusDto,
    ModelDownloadOperationDto,
    HuggingFaceRepositoryListingDto,
    ChatTargetReadinessDto,
    SettingsOverviewDto,
    ChatDefaultsDto,
    UpdateChatDefaultsRequest,
    ConnectionUsageDto,
    InfrastructureProbeBatchDto,
    InfrastructureProbeRequestItemDto,
    SettingsRuntimeDependencyDto,
    ServiceEditorStateDto,
    LocalModelsListOutcome,
} from '../types/settings';

import { API_BASE_URL } from '../config/apiConfig';
import { withAuthFetchInit, withAuthHeaders } from './authService';
import { broadcastAuthExpired } from './authEvents';

interface CreateProjectDto {
    title: string;
    description?: string;
}

interface UpdateProjectDto {
    title?: string;
    description?: string;
}

interface ProjectDto {
    id: string;
    title: string;
    description: string;
    created: string;
    canCreateContent: boolean;
}

// DTO for copying notebook
export interface CopyNotebookDto {
    title: string;
    sourceNotebookId: string;
    sourceConversationMessageId?: string;
}

export type AdminUserStatusFilter = 'all' | 'pending' | 'active' | 'inactive';

export interface AdminUserSummaryDto {
    userId: string;
    name: string;
    email: string;
    role: import('../types/user').AppRole;
    isActive: boolean;
    mustChangePassword: boolean;
    approvedByUserId?: string | null;
    approvedAt?: string | null;
    lastLoginAt?: string | null;
    created: string;
}

async function callApi<T>(endpoint: string, options: RequestInit = {}): Promise<T> {
    try {
        const headers = withAuthHeaders(options.headers);
        const isFormDataBody = typeof FormData !== 'undefined' && options.body instanceof FormData;
        if (!isFormDataBody && !headers.has('Content-Type')) {
            headers.set('Content-Type', 'application/json');
        }

        const response = await fetch(`${API_BASE_URL}${endpoint}`, withAuthFetchInit({
            ...options,
            headers,
        }));

        if (!response.ok) {
            let parsed: any = null;
            let rawText: string | null = null;
            try {
                const contentType = response.headers.get('content-type') || '';
                if (contentType.includes('application/json')) {
                    parsed = await response.json();
                } else {
                    rawText = await response.text();
                }
            } catch (e) {
                // ignore parse errors
            }

            const message = (parsed && (parsed.message || parsed.error || parsed.title))
                || rawText
                || `API call failed: ${response.statusText}`;

            const error: any = new Error(message);
            error.status = response.status;
            if (parsed) error.body = parsed; else error.body = rawText;
            error.code = parsed?.code;
            if (response.status === 401) {
                broadcastAuthExpired(message);
            }
            throw error;
        }

        if (response.status === 204) {
            return undefined as T;
        }

        if (typeof response.text === 'function') {
            const rawBody = await response.text();
            if (!rawBody.trim()) {
                return undefined as T;
            }

            return JSON.parse(rawBody) as T;
        }

        return response.json();
    } catch (error) {
        console.error('API call error:', error);
        throw error;
    }
}

async function fetchWithAuth(input: RequestInfo | URL, options: RequestInit = {}): Promise<Response> {
    const headers = withAuthHeaders(options.headers);
    const response = await fetch(input, withAuthFetchInit({
        ...options,
        headers,
    }));
    if (response.status === 401) {
        broadcastAuthExpired('Authentication expired.');
    }
    return response;
}

/**
 * Shape the .NET settings endpoints return for any upstream proxy failure
 * (see {@code LocalServiceAdminRouting.UpstreamProxyEnvelope} on the server).
 * The server always emits HTTP 502 Bad Gateway with this body when the
 * upstream local service returns anything outside the allowed pass-through
 * status codes, or fails at the transport layer.
 */
interface ServerUpstreamEnvelope {
    error?: string;
    upstreamTarget?: string;
    upstreamStatus?: number;
    upstreamStatusText?: string;
    upstreamContentType?: string;
    upstreamBody?: string;
}

function buildLocalModelsFailureFromResponse(
    response: Response,
    rawBody: string,
    contentType: string
): LocalModelsListOutcome {
    // Prefer the structured server envelope so the UI can surface the full
    // proxy chain. The server emits it as application/json on 502; defensively
    // accept any JSON shape that carries an "upstreamStatus" field so small
    // serialization drift (casing, extra fields) does not silently downgrade
    // the error to a generic message.
    if (contentType.includes('application/json') && rawBody) {
        try {
            const parsed = JSON.parse(rawBody) as ServerUpstreamEnvelope;
            if (typeof parsed?.upstreamStatus === 'number' || typeof parsed?.upstreamTarget === 'string') {
                const upstream = {
                    upstreamTarget: parsed.upstreamTarget ?? '',
                    upstreamStatus: typeof parsed.upstreamStatus === 'number' ? parsed.upstreamStatus : 0,
                    upstreamStatusText: parsed.upstreamStatusText ?? '',
                    upstreamContentType: parsed.upstreamContentType ?? '',
                    upstreamBody: parsed.upstreamBody ?? '',
                };
                const message =
                    parsed.error ||
                    `Upstream ${upstream.upstreamTarget || '(unknown target)'} returned ${upstream.upstreamStatus} ${upstream.upstreamStatusText}.`;
                return { kind: 'error', message, upstream };
            }
            if (typeof parsed?.error === 'string' && parsed.error.trim().length > 0) {
                return { kind: 'error', message: parsed.error.trim() };
            }
        } catch {
            // Fall through to the generic failure below — we will preserve
            // whatever raw body we received so the operator can see it.
        }
    }
    // No structured envelope — still tell the truth: include the status and
    // whatever body text we received.
    const statusText = response.statusText || String(response.status);
    const bodyExcerpt = rawBody ? ` | body: ${rawBody.slice(0, 500)}` : '';
    return {
        kind: 'error',
        message: `Request to ${response.url} failed: ${response.status} ${statusText}${bodyExcerpt}`,
    };
}

/**
 * Probe the local-model list endpoint. A 200 response becomes
 * {@code { kind: 'available', payload }}; anything else — including an
 * upstream proxy failure surfaced as a 502 envelope — becomes an
 * {@code { kind: 'error' }} outcome carrying the upstream details verbatim.
 * No response status is silently reinterpreted.
 */
async function fetchLocalModelsListOutcome(serviceId: string): Promise<LocalModelsListOutcome> {
    let response: Response;
    try {
        response = await fetchWithAuth(
            `${API_BASE_URL}/settings/services/${encodeURIComponent(serviceId)}/local-models`,
            {
                headers: { 'Content-Type': 'application/json' },
            }
        );
    } catch (e) {
        const message = e instanceof Error ? e.message : 'Network error';
        return { kind: 'error', message };
    }
    const contentType = response.headers.get('content-type') || '';
    const rawBody = await response.text();
    if (response.status === 200) {
        try {
            return { kind: 'available', payload: rawBody ? JSON.parse(rawBody) : {} };
        } catch (e) {
            const message = e instanceof Error ? e.message : 'Invalid JSON from local-models endpoint.';
            return { kind: 'error', message };
        }
    }
    return buildLocalModelsFailureFromResponse(response, rawBody, contentType);
}

async function fetchLocalModelCatalogOutcome(serviceId: string): Promise<LocalModelsListOutcome> {
    let response: Response;
    try {
        response = await fetchWithAuth(
            `${API_BASE_URL}/settings/services/${encodeURIComponent(serviceId)}/local-models/catalog`,
            {
                headers: { 'Content-Type': 'application/json' },
            }
        );
    } catch (e) {
        const message = e instanceof Error ? e.message : 'Network error';
        return { kind: 'error', message };
    }
    const contentType = response.headers.get('content-type') || '';
    const rawBody = await response.text();
    if (response.status === 200) {
        try {
            return { kind: 'available', payload: rawBody ? JSON.parse(rawBody) : {} };
        } catch (e) {
            const message = e instanceof Error ? e.message : 'Invalid JSON from local-models catalog endpoint.';
            return { kind: 'error', message };
        }
    }
    return buildLocalModelsFailureFromResponse(response, rawBody, contentType);
}

async function fetchLocalModelVoicePackOutcome(serviceId: string): Promise<LocalModelsListOutcome> {
    let response: Response;
    try {
        response = await fetchWithAuth(
            `${API_BASE_URL}/settings/services/${encodeURIComponent(serviceId)}/local-models/voice-pack`,
            {
                headers: { 'Content-Type': 'application/json' },
            }
        );
    } catch (e) {
        const message = e instanceof Error ? e.message : 'Network error';
        return { kind: 'error', message };
    }
    const contentType = response.headers.get('content-type') || '';
    const rawBody = await response.text();
    if (response.status === 200) {
        try {
            return { kind: 'available', payload: rawBody ? JSON.parse(rawBody) : {} };
        } catch (e) {
            const message = e instanceof Error ? e.message : 'Invalid JSON from voice-pack endpoint.';
            return { kind: 'error', message };
        }
    }
    return buildLocalModelsFailureFromResponse(response, rawBody, contentType);
}

async function fetchLocalModelVoicesOutcome(serviceId: string): Promise<LocalModelsListOutcome> {
    let response: Response;
    try {
        response = await fetchWithAuth(
            `${API_BASE_URL}/settings/services/${encodeURIComponent(serviceId)}/local-models/voices`,
            {
                headers: { 'Content-Type': 'application/json' },
            }
        );
    } catch (e) {
        const message = e instanceof Error ? e.message : 'Network error';
        return { kind: 'error', message };
    }
    const contentType = response.headers.get('content-type') || '';
    const rawBody = await response.text();
    if (response.status === 200) {
        try {
            return { kind: 'available', payload: rawBody ? JSON.parse(rawBody) : {} };
        } catch (e) {
            const message = e instanceof Error ? e.message : 'Invalid JSON from voices endpoint.';
            return { kind: 'error', message };
        }
    }
    return buildLocalModelsFailureFromResponse(response, rawBody, contentType);
}

/**
 * Probe the runtime-readiness endpoint (ASR/TTS/Embeddings). The upstream /ready
 * returns 200 when the service is loaded and 503 with the same JSON shape
 * when it is not; the server treats both as pass-through so the client sees
 * the upstream payload directly. Any other status code is a real failure
 * and is surfaced with the full upstream details — no status is silently
 * reinterpreted as "unavailable".
 */
async function fetchRuntimeReadinessOutcome(serviceId: string): Promise<LocalModelsListOutcome> {
    let response: Response;
    try {
        response = await fetchWithAuth(
            `${API_BASE_URL}/settings/services/${encodeURIComponent(serviceId)}/runtime-readiness`,
            {
                headers: { 'Content-Type': 'application/json' },
            }
        );
    } catch (e) {
        const message = e instanceof Error ? e.message : 'Network error';
        return { kind: 'error', message };
    }
    const contentType = response.headers.get('content-type') || '';
    const rawBody = await response.text();
    if (response.status === 200 || response.status === 503) {
        try {
            return { kind: 'available', payload: rawBody ? JSON.parse(rawBody) : {} };
        } catch (e) {
            const message = e instanceof Error ? e.message : 'Invalid JSON from runtime-readiness endpoint.';
            return { kind: 'error', message };
        }
    }
    return buildLocalModelsFailureFromResponse(response, rawBody, contentType);
}

/**
 * Normalize user input for the Hugging Face repository browse endpoint.
 * Accepts either the canonical <c>owner/repo</c> form or a full
 * <c>https://huggingface.co/owner/repo[/...]</c> URL (including optional
 * trailing path segments like <c>/tree/main</c>). Returns
 * <c>[owner, repo]</c> on success and <c>null</c> when the input cannot be
 * parsed into an owner/repo pair — callers should surface
 * <c>REPO_INVALID</c> in that case.
 */
function parseHuggingFaceRepository(input: string): [string, string] | null {
    const raw = (input ?? '').trim();
    if (!raw) {
        return null;
    }
    let slug = raw;
    const urlMatch = raw.match(/^https?:\/\/(?:www\.)?huggingface\.co\/(.+)$/i);
    if (urlMatch) {
        slug = urlMatch[1];
    }
    slug = slug.replace(/^\/+|\/+$/g, '');
    const parts = slug.split('/').filter((part) => part.length > 0);
    if (parts.length < 2) {
        return null;
    }
    const owner = parts[0];
    const repo = parts[1];
    if (!owner || !repo) {
        return null;
    }
    return [owner, repo];
}

export const api = {
    public: {
        getPublishedGuideByFriendlyName: async (friendlyName: string) => {
            const response = await fetch(`${API_BASE_URL}/published/guides/by-name/${encodeURIComponent(friendlyName)}`);
            if (!response.ok) {
                const error: any = new Error('Failed to load guide');
                error.status = response.status;
                throw error;
            }
            return response.json() as Promise<{
                id: string;
                friendlyName: string;
                projectId: string;
                notebookId: string;
                guideId: string;
                guideName: string;
                guideDescription?: string;
                avatarUrl?: string;
                maxUserMessageLength?: number;
                maxTurns?: number;
                requiresAuth: boolean;
            }>;
        }
    },
    auth: {
        login: (request: LoginRequest) =>
            callApi<AuthResponse>('/auth/login', {
                method: 'POST',
                body: JSON.stringify({
                    email: request.email.trim(),
                    password: request.password,
                }),
            }),
        register: (request: RegisterRequest) =>
            callApi<AuthResponse>('/auth/register', {
                method: 'POST',
                body: JSON.stringify({
                    name: request.name.trim(),
                    email: request.email.trim(),
                    password: request.password,
                }),
            }),
        me: () => callApi<AuthMeResponse>('/auth/me'),
        logout: () => callApi<void>('/auth/logout', { method: 'POST' }),
        changePassword: (request: ChangePasswordRequest) =>
            callApi<void>('/auth/change-password', {
                method: 'POST',
                body: JSON.stringify(request),
            }),
    },
    test: {
        secure: () => callApi<any>('/test/secure')
    },
    cli: {
        approveSession: (sessionId: string) =>
            callApi<void>(`/cli/sessions/${encodeURIComponent(sessionId)}/approve`, { method: 'POST' }),
        denySession: (sessionId: string) =>
            callApi<void>(`/cli/sessions/${encodeURIComponent(sessionId)}/deny`, { method: 'POST' }),
    },
    quickStart: {
        create: () => callApi<{
            projectId: string;
            projectTitle: string;
            notebookId: string;
            notebookTitle: string;
            conversationId: string;
            conversationTitle: string;
        }>('/quick-start', {
            method: 'POST',
        }),
    },
    users: {
        getCurrent: () => callApi<UserDto>('/users/current'),
        updatePersonalization: (request: UpdateCurrentUserRequest) =>
            callApi<UserDto>('/users/current/personalization', {
                method: 'PUT',
                body: JSON.stringify(request),
            }),
        getUserById: (userId: string) => callApi<UserDto>(`/users/${userId}`),
    },
    adminUsers: {
        list: (options?: { role?: string; status?: AdminUserStatusFilter }) => {
            const query = new URLSearchParams();
            if (options?.role) {
                query.set('role', options.role);
            }
            if (options?.status) {
                query.set('status', options.status);
            }
            const suffix = query.toString();
            return callApi<AdminUserSummaryDto[]>(`/admin/users/${suffix ? `?${suffix}` : ''}`);
        },
        approve: (userId: string, role: string) =>
            callApi<AdminUserSummaryDto>(`/admin/users/${encodeURIComponent(userId)}/approve`, {
                method: 'POST',
                body: JSON.stringify({ role }),
            }),
        changeRole: (userId: string, role: string) =>
            callApi<AdminUserSummaryDto>(`/admin/users/${encodeURIComponent(userId)}/role`, {
                method: 'PUT',
                body: JSON.stringify({ role }),
            }),
        deactivate: (userId: string) =>
            callApi<AdminUserSummaryDto>(`/admin/users/${encodeURIComponent(userId)}/deactivate`, {
                method: 'POST',
            }),
        reactivate: (userId: string) =>
            callApi<AdminUserSummaryDto>(`/admin/users/${encodeURIComponent(userId)}/reactivate`, {
                method: 'POST',
            }),
        setPassword: (userId: string, password: string) =>
            callApi<AdminUserSummaryDto>(`/admin/users/${encodeURIComponent(userId)}/set-password`, {
                method: 'POST',
                body: JSON.stringify({ password }),
            }),
    },
    utils: {
        getAuthenticatedUrl: async (url: string) => {
            const response = await fetchWithAuth(url);

            if (!response.ok) {
                const fetchError: any = new Error(`Failed to fetch: ${response.statusText}`);
                fetchError.status = response.status;
                throw fetchError;
            }

            const blob = await response.blob();
            const contentDisposition = response.headers.get('content-disposition');
            let fileName = contentDisposition?.split('filename=')[1]?.replace(/["']/g, '') || '';

            if (!fileName || fileName.toLowerCase() === 'file' || fileName.toLowerCase() === 'download') {
                try {
                    const u = new URL(url, window.location.origin);
                    const fromPath = u.searchParams.get('path');
                    const candidate = (fromPath ? decodeURIComponent(fromPath) : u.pathname).split('/').pop() || '';
                    if (candidate) fileName = candidate;
                } catch {
                    // keep default
                }
                if (!fileName) fileName = 'download';
            }

            return {
                objectUrl: URL.createObjectURL(blob),
                contentType: response.headers.get('content-type') || 'application/octet-stream',
                fileName
            };
        }
    },
    projects: {
        create: (project: CreateProjectDto) => 
            callApi<ProjectDto>('/projects', {
                method: 'POST',
                body: JSON.stringify(project),
            }),
        
        getUserProjects: () => 
            callApi<ProjectDto[]>('/projects'),
        
        getProject: (projectId: string) => 
            callApi<ProjectDto>(`/projects/${projectId}`),
        
        getProjectDetails: (projectId: string) => 
            callApi<ProjectDetailsDto>(`/projects/${projectId}/details`),
        
        updateProject: (projectId: string, project: UpdateProjectDto) => 
            callApi<ProjectDto>(`/projects/${projectId}`, {
                method: 'PUT',
                body: JSON.stringify(project),
            }),

        deleteProject: (projectId: string) => 
            callApi<void>(`/projects/${projectId}`, {
                method: 'DELETE',
            }),

        copyProject: (projectId: string) =>
            callApi<ProjectDto>(`/projects/${projectId}/copy`, {
                method: 'POST',
            }),

        uploadFiles: async (projectId: string, files: File[], folderId?: string) => {
            const results = [];
            for (const file of files) {
                const formData = new FormData();
                formData.append('file', file);
                if (folderId) {
                    formData.append('folderId', folderId);
                }

                const response = await fetchWithAuth(`${API_BASE_URL}/projects/${projectId}/files`, {
                    method: 'POST',
                    body: formData
                });

                if (!response.ok) {
                    throw new Error(`Failed to upload file ${file.name}: ${response.statusText}`);
                }

                const result = await response.json();
                results.push(result);
            }
            return results;
        },

        // Content file endpoints
        getContentFile: async (projectId: string, fileId: string) => {
            return callApi<ContentFileDto>(`/projects/${projectId}/files/${fileId}`);
        },

        getContentFileContent: async (projectId: string, fileId: string, version?: number) => {
            const startTime = Date.now();
            const fileIdKey = `${projectId}:${fileId}:${version || 'latest'}`;
            
            console.log('[TELEMETRY] ContentFile API call started', { 
                fileIdKey,
                projectId,
                fileId,
                version,
                timestamp: startTime 
            });

            const cacheKey = version !== undefined ? `${fileId}-v${version}` : fileId;
            let cached: { blob: Blob; contentType: string; fileName: string } | null = null;
            try {
                console.log('[TELEMETRY] Checking IndexedDB cache', { fileIdKey, cacheKey });
                cached = await getCachedFile(projectId, cacheKey);
                
                if (cached) {
                    console.log('[TELEMETRY] Cache hit', { 
                        fileIdKey,
                        blobSize: cached.blob.size,
                        contentType: cached.contentType,
                        fileName: cached.fileName,
                        timestamp: Date.now() 
                    });
                } else {
                    console.log('[TELEMETRY] Cache miss', { fileIdKey });
                }
            } catch (err) {
                console.log('[TELEMETRY] Cache read failed', { 
                    fileIdKey,
                    error: err instanceof Error ? err.message : 'Unknown error',
                    timestamp: Date.now() 
                });
                console.warn('IndexedDB cache read failed – falling back to network', err);
            }
            
            if (cached) {
                console.log('[TELEMETRY] Using cached content file', { 
                    fileIdKey,
                    totalTime: Date.now() - startTime,
                    timestamp: Date.now() 
                });
                
                return {
                    blob: cached.blob,
                    contentType: cached.contentType,
                    fileName: cached.fileName,
                };
            }

            console.log('[TELEMETRY] Starting network fetch', { fileIdKey });
            
            const versionParam = version !== undefined ? `?v=${version}` : '';
            const url = `${API_BASE_URL}/projects/${projectId}/files/${fileId}/content${versionParam}`;
            
            console.log('[TELEMETRY] Fetching URL', { fileIdKey, url });
            
            const response = await fetchWithAuth(url);

            console.log('[TELEMETRY] Network response received', { 
                fileIdKey,
                status: response.status,
                statusText: response.statusText,
                headers: Object.fromEntries(response.headers.entries()),
                timestamp: Date.now() 
            });

            if (!response.ok) {
                console.log('[TELEMETRY] Network request failed', { 
                    fileIdKey,
                    status: response.status,
                    statusText: response.statusText,
                    timestamp: Date.now() 
                });
                throw new Error('Failed to fetch file content');
            }

            const blob = await response.blob();
            const contentType = response.headers.get('Content-Type') || 'application/octet-stream';
            const contentDisposition = response.headers.get('Content-Disposition') || '';
            let fileName = 'unknown';
            if (contentDisposition) {
                const starMatch = contentDisposition.match(/filename\*=(?:UTF-8''|)([^;]+)/i);
                if (starMatch?.[1]) {
                    try {
                        fileName = decodeURIComponent(starMatch[1].replace(/"/g, ''));
                    } catch {
                        fileName = starMatch[1].replace(/"/g, '');
                    }
                } else {
                    const regularMatch = contentDisposition.match(/filename=([^;]+)/i);
                    if (regularMatch?.[1]) {
                        fileName = regularMatch[1].replace(/"/g, '');
                    }
                }
            }

            console.log('[TELEMETRY] Network fetch completed', { 
                fileIdKey,
                blobSize: blob.size,
                blobType: blob.type,
                contentType,
                fileName,
                totalTime: Date.now() - startTime,
                timestamp: Date.now() 
            });

            cacheFile(projectId, cacheKey, { blob, contentType, fileName })
                .then(() => {
                    console.log('[TELEMETRY] Content file cached successfully', { fileIdKey });
                })
                .catch((err) => {
                    console.log('[TELEMETRY] Cache write failed', { 
                        fileIdKey,
                        error: err instanceof Error ? err.message : 'Unknown error',
                        timestamp: Date.now() 
                    });
                });

            return { blob, contentType, fileName };
        },

        getMountedDetailsByPath: async (projectId: string, relativePath: string) => {
            return callApi<ContentFileDto>(`/projects/${projectId}/files/mounted/details?path=${encodeURIComponent(relativePath)}`);
        },

        getMountedContentByPath: async (projectId: string, relativePath: string) => {
            const url = `${API_BASE_URL}/projects/${projectId}/files/mounted/content?path=${encodeURIComponent(relativePath)}`;
            const response = await fetchWithAuth(url);
            if (!response.ok) {
                throw new Error('Failed to fetch mounted file content');
            }
            const blob = await response.blob();
            const contentType = response.headers.get('Content-Type') || 'application/octet-stream';
            const contentDisposition = response.headers.get('Content-Disposition') || '';
            let fileName = 'unknown';
            if (contentDisposition) {
                const starMatch = contentDisposition.match(/filename\*=(?:UTF-8''|)([^;]+)/i);
                if (starMatch?.[1]) {
                    try { fileName = decodeURIComponent(starMatch[1].replace(/"/g, '')); } catch { fileName = starMatch[1].replace(/"/g, ''); }
                } else {
                    const regularMatch = contentDisposition.match(/filename=([^;]+)/i);
                    if (regularMatch?.[1]) { fileName = regularMatch[1].replace(/"/g, ''); }
                }
            }
            return { blob, contentType, fileName };
        },

        saveMountedByPath: async (projectId: string, relativePath: string, content: string | Blob) => {
            const url = `${API_BASE_URL}/projects/${projectId}/files/mounted/content?path=${encodeURIComponent(relativePath)}`;
            const response = await fetchWithAuth(url, { method: 'PUT', body: content });
            if (!response.ok) {
                throw new Error('Failed to save mounted file content');
            }
        },

        renameMountedByPath: async (projectId: string, relativePath: string, newName: string) => {
            const url = `${API_BASE_URL}/projects/${projectId}/files/mounted/rename?path=${encodeURIComponent(relativePath)}`;
            const response = await fetchWithAuth(url, {
                method: 'PATCH',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ newName }),
            });
            if (!response.ok) {
                throw new Error('Failed to rename mounted entry');
            }
        },

        updateContentFile: (projectId: string, fileId: string, updates: { index?: boolean; fileName?: string }) =>
            callApi<import('../types/project').ContentFileDto>(`/projects/${projectId}/files/${fileId}`, {
                method: 'PATCH',
                body: JSON.stringify(updates),
            }),

        renameContentFile: async (projectId: string, fileId: string, newName: string) => {
            return callApi<ContentFileDto>(`/projects/${projectId}/files/${fileId}/rename`, {
                method: 'PATCH',
                body: JSON.stringify({ newName }),
            });
        },

        deleteContentFile: async (projectId: string, fileId: string) => {
            try {
                await callApi(`/projects/${projectId}/files/${fileId}`, {
                    method: 'DELETE',
                });
            } catch (error: any) {
                if (error?.status === 400 && error?.body) {
                    const errorData = error.body;
                    try {
                        const parsed = typeof errorData === 'string' ? JSON.parse(errorData) : errorData;
                        if (parsed?.notebooksUsingFile) {
                            error.isFileInUse = true;
                            error.notebooksUsingFile = parsed.notebooksUsingFile;
                            error.message = parsed.message || 'File is in use by notebooks';
                        }
                    } catch (parseError) {
                        if (process.env.NODE_ENV === 'development') {
                            console.warn('Failed to parse file-in-use error body:', parseError);
                        }
                    }
                }
                throw error;
            }
        },

        addLink: async (projectId: string, url: string) => {
            return callApi<ProjectDto>(`/projects/${projectId}/links`, {
                method: 'POST',
                body: JSON.stringify({ url }),
            });
        },

        updateLink: async (projectId: string, linkId: string, updates: Partial<ProjectLink>) => {
            return callApi<ProjectDto>(`/projects/${projectId}/links/${linkId}`, {
                method: 'PUT',
                body: JSON.stringify(updates),
            });
        },

        deleteLink: async (projectId: string, linkId: string) => {
            return callApi<void>(`/projects/${projectId}/links/${linkId}`, {
                method: 'DELETE',
            });
        },

        moveContentFile: async (projectId: string, fileId: string, moveData: MoveFileDto) => {
            let destinationFolderId = moveData.destinationFolderId;
            
            if (destinationFolderId === undefined || destinationFolderId === '' || destinationFolderId === 'undefined') {
                destinationFolderId = null;
            }
            
            const normalizedMoveData = {
                ...moveData,
                destinationFolderId
            };

            return callApi<ContentFileDto>(`/projects/${projectId}/files/${fileId}/move`, {
                method: 'PATCH',
                body: JSON.stringify(normalizedMoveData),
            });
        },

        // File history
        getFileHistory: (projectId: string, fileId: string) =>
            callApi<FileLineageEvent[]>(`/projects/${projectId}/files/${fileId}/history`),

        // Markdown shadow functions
        getContentFileMarkdownShadow: async (projectId: string, fileId: string, version?: number) => {
            const endpoint = version !== undefined 
                ? `/projects/${projectId}/files/${fileId}/versions/${version}/markdown`
                : `/projects/${projectId}/files/${fileId}/markdown`;
            return callApi<import('../types/api').ContentFileMarkdownShadowDto>(endpoint);
        },

        getContentFileMarkdownContent: async (projectId: string, fileId: string, version?: number) => {
            const endpoint = version !== undefined 
                ? `/projects/${projectId}/files/${fileId}/versions/${version}/markdown/content`
                : `/projects/${projectId}/files/${fileId}/markdown/content`;
                
            const response = await fetchWithAuth(`${API_BASE_URL}${endpoint}`);

            if (!response.ok) {
                throw new Error('Failed to fetch markdown content');
            }

            const blob = await response.blob();
            const contentType = response.headers.get('Content-Type') || 'text/markdown';
            const contentDisposition = response.headers.get('Content-Disposition') || '';
            let fileName = 'markdown-content.md';
            
            if (contentDisposition) {
                const starMatch = contentDisposition.match(/filename\*=(?:UTF-8''|)([^;]+)/i);
                if (starMatch?.[1]) {
                    try {
                        fileName = decodeURIComponent(starMatch[1].replace(/"/g, ''));
                    } catch {
                        fileName = starMatch[1].replace(/"/g, '');
                    }
                } else {
                    const regularMatch = contentDisposition.match(/filename=([^;]+)/i);
                    if (regularMatch?.[1]) {
                        fileName = regularMatch[1].replace(/"/g, '');
                    }
                }
            }

            return { blob, contentType, fileName };
        },

        // Folder management endpoints
        folders: {
            getFolderTree: (projectId: string) => 
                callApi<FolderTreeDto>(`/projects/${projectId}/folders/tree`),
            
            getFolders: (projectId: string) => 
                callApi<ProjectFolderDto[]>(`/projects/${projectId}/folders`),
            
            getFolder: (projectId: string, folderId: string) => 
                callApi<ProjectFolderDto>(`/projects/${projectId}/folders/${folderId}`),
            
            createFolder: (projectId: string, folderData: CreateFolderDto) => 
                callApi<ProjectFolderDto>(`/projects/${projectId}/folders`, {
                    method: 'POST',
                    body: JSON.stringify(folderData),
                }),
            
            updateFolder: (projectId: string, folderId: string, folderData: UpdateFolderDto) => 
                callApi<ProjectFolderDto>(`/projects/${projectId}/folders/${folderId}`, {
                    method: 'PUT',
                    body: JSON.stringify(folderData),
                }),
            
            deleteFolder: (projectId: string, folderId: string) => 
                callApi<void>(`/projects/${projectId}/folders/${folderId}`, {
                    method: 'DELETE',
                }),
            
            moveFolder: (projectId: string, folderId: string, moveData: MoveFolderDto) => 
                callApi<ProjectFolderDto>(`/projects/${projectId}/folders/${folderId}/move`, {
                    method: 'PATCH',
                    body: JSON.stringify(moveData),
                }),
        },

        // Notebook endpoints
        createNotebook: (projectId: string, notebook: { title: string; description?: string; guideId?: string }) =>
            callApi<ProjectNotebook>(`/projects/${projectId}/notebooks`, {
                method: 'POST',
                body: JSON.stringify(notebook),
            }),

        notebookTemplates: {
            getAll: (projectId: string) => callApi<NotebookTemplateSummaryDto[]>(`/notebook-templates?projectId=${projectId}`),
            getById: (templateId: string, projectId: string) => callApi<NotebookTemplateDto>(`/notebook-templates/${templateId}?projectId=${projectId}`),

            getAssistants: (templateId: string, projectId: string) =>
                callApi<import('../types/conversation').AssistantDefinition[]>(
                    `/notebook-templates/${templateId}/assistants?projectId=${projectId}`
                ),
        },

        assistants: {
            getConversationStarters: (assistantName: string, projectId: string) =>
                callApi<string[]>(`/assistants/conversation-starters/${encodeURIComponent(assistantName)}?projectId=${projectId}`),
        },

        deleteNotebook: (projectId: string, notebookId: string) =>
            callApi<void>(`/projects/${projectId}/notebooks/${notebookId}`, { method: 'DELETE' }),

        copyNotebook: (projectId: string, data: CopyNotebookDto) =>
            callApi<ProjectNotebook>(`/projects/${projectId}/notebooks/copy`, {
                method: 'POST',
                body: JSON.stringify(data),
            }),

        updateNotebook: (projectId: string, notebookId: string, data: UpdateNotebookDto) =>
            callApi<ProjectNotebook>(`/projects/${projectId}/notebooks/${notebookId}`, {
                method: 'PUT',
                body: JSON.stringify(data),
            }),

        createNotebookFromFile: (projectId: string, data: {
            title: string;
            contentFileId: string;
            versionNumber?: number;
            guideId?: string;
        }) =>
            callApi<{
                notebookId: string;
                notebookTitle: string;
                copiedFileId: string;
                copiedFileName: string;
            }>(`/projects/${projectId}/notebooks/create-from-file`, {
                method: 'POST',
                body: JSON.stringify(data),
            }),

        setHomePage: (projectId: string, fileId: string) =>
            callApi<void>(`/projects/${projectId}/homepage/${fileId}`, { method: 'POST' }),

        clearHomePage: (projectId: string) =>
            callApi<void>(`/projects/${projectId}/homepage`, { method: 'DELETE' }),

        externalAuth: {
            list: (projectId: string) =>
                callApi<Array<{
                    providerId: string;
                    authType: string;
                    clientId?: string | null;
                    tenant?: string | null;
                    headerName?: string | null;
                    hasHeaderValue: boolean;
                }>>(`/projects/${projectId}/external-auth`),
            
            save: (projectId: string, providerId: string, data: {
                authType: string;
                clientId?: string;
                tenant?: string;
                headerName?: string;
                headerValue?: string;
            }) =>
                callApi<void>(`/projects/${projectId}/external-auth/${encodeURIComponent(providerId)}`, {
                    method: 'PUT',
                    body: JSON.stringify(data)
                }),
            
            delete: (projectId: string, providerId: string) =>
                callApi<void>(`/projects/${projectId}/external-auth/${encodeURIComponent(providerId)}`, { method: 'DELETE' }),

            oauth: {
                authorizeUrl: (
                    projectId: string,
                    providerId: string,
                    data: {
                        clientId: string;
                        tenant: string;
                        scopes: string[];
                        redirectUri: string;
                        returnUrl?: string;
                    }
                ) =>
                    callApi<{
                        authorizeUrl: string;
                        state: string;
                        expiresAt: string;
                    }>(`/projects/${projectId}/external-auth/${encodeURIComponent(providerId)}/oauth/authorize-url`, {
                        method: 'POST',
                        body: JSON.stringify(data),
                    }),
                callback: (projectId: string, providerId: string, data: { code: string; state: string }) =>
                    callApi<{
                        connected: boolean;
                        expiresAt: string | null;
                        scopes: string[];
                    }>(`/projects/${projectId}/external-auth/${encodeURIComponent(providerId)}/oauth/callback`, {
                        method: 'POST',
                        body: JSON.stringify(data),
                    }),
                status: (projectId: string, providerId: string) =>
                    callApi<{
                        connected: boolean;
                        expiresAt: string | null;
                        scopes: string[];
                    }>(`/projects/${projectId}/external-auth/${encodeURIComponent(providerId)}/oauth/status`),
                disconnect: (projectId: string, providerId: string) =>
                    callApi<void>(`/projects/${projectId}/external-auth/${encodeURIComponent(providerId)}/oauth`, {
                        method: 'DELETE',
                    }),
            },
        },

        notebooks: {
            getNotebookFiles: (projectId: string, notebookId: string) =>
                callApi<import('../types/notebook').NotebookFileDto[]>(`/projects/${projectId}/notebooks/${notebookId}/files`),
            
            getNotebookFolderTree: (projectId: string, notebookId: string) =>
                callApi<import('../types/notebook').NotebookFolderTreeDto>(`/projects/${projectId}/notebooks/${notebookId}/files/tree`),
            
            uploadNotebookFiles: async (projectId: string, notebookId: string, files: File[], targetRelativePath: string, index: boolean) => {
                const formData = new FormData();
                files.forEach(file => formData.append('files', file));
                formData.append('targetRelativePath', targetRelativePath);
                formData.append('index', index.toString());

                const response = await fetchWithAuth(`${API_BASE_URL}/projects/${projectId}/notebooks/${notebookId}/files/upload`, {
                    method: 'POST',
                    body: formData
                });

                if (!response.ok) {
                    throw new Error(`Failed to upload files: ${response.statusText}`);
                }

                return response.json();
            },
            
            createNotebookFolder: (projectId: string, notebookId: string, path: string) =>
                callApi<import('../types/notebook').NotebookFolderTreeDto>(`/projects/${projectId}/notebooks/${notebookId}/files/create-folder`, {
                    method: 'POST',
                    body: JSON.stringify({ path }),
                }),
            
            renameNotebookItem: (projectId: string, notebookId: string, sourcePath: string, newName: string) =>
                callApi<void>(`/projects/${projectId}/notebooks/${notebookId}/files/rename`, {
                    method: 'PATCH',
                    body: JSON.stringify({ sourcePath, newName }),
                }),
            
            deleteNotebookItem: (projectId: string, notebookId: string, path: string) =>
                callApi<void>(`/projects/${projectId}/notebooks/${notebookId}/files?path=${encodeURIComponent(path)}`, {
                    method: 'DELETE',
                }),
            
            moveNotebookItem: (projectId: string, notebookId: string, sourcePath: string, destinationPath: string) =>
                callApi<void>(`/projects/${projectId}/notebooks/${notebookId}/files/move`, {
                    method: 'PATCH',
                    body: JSON.stringify({ sourcePath, destinationPath }),
                }),
            
            deleteNotebookFileById: (projectId: string, notebookId: string, fileId: string) =>
                callApi<void>(`/projects/${projectId}/notebooks/${notebookId}/files/${fileId}`, {
                    method: 'DELETE',
                }),
            
            renameNotebookFileById: (projectId: string, notebookId: string, fileId: string, newName: string) =>
                callApi<void>(`/projects/${projectId}/notebooks/${notebookId}/files/${fileId}/rename`, {
                    method: 'PATCH',
                    body: JSON.stringify({ newName }),
                }),
            
            moveNotebookFileById: (projectId: string, notebookId: string, fileId: string, destinationPath: string | undefined) =>
                callApi<void>(`/projects/${projectId}/notebooks/${notebookId}/files/${fileId}/move`, {
                    method: 'PATCH',
                    body: JSON.stringify({ destinationPath }),
                }),
            
            copyFileFromProject: (projectId: string, notebookId: string, contentFileId: string, versionNumber?: number, targetRelativePath?: string) =>
                callApi<import('../types/notebook').NotebookFileDto>(`/projects/${projectId}/notebooks/${notebookId}/files/copy-from-project`, {
                    method: 'POST',
                    body: JSON.stringify({ 
                        contentFileId, 
                        versionNumber, 
                        targetRelativePath 
                    }),
                }),
            
            syncFiles: (projectId: string, notebookId: string) =>
                callApi<void>(`/projects/${projectId}/notebooks/${notebookId}/files/sync`, {
                    method: 'POST',
                }),

            getNotebook: (projectId: string, notebookId: string) =>
                callApi<import('../types/notebook').NotebookDetailsDto>(`/projects/${projectId}/notebooks/${notebookId}`),

            conversations: {
                getAll: (projectId: string, notebookId: string) =>
                    callApi<import('../types/notebook').NotebookConversationDto[]>(`/projects/${projectId}/notebooks/${notebookId}/conversations`),
                get: (projectId: string, notebookId: string, convoId: string) =>
                    callApi<import('../types/notebook').NotebookConversationWithMessagesDto>(`/projects/${projectId}/notebooks/${notebookId}/conversations/${convoId}`),
                create: (projectId: string, notebookId: string, title: string) =>
                    callApi<import('../types/notebook').NotebookConversationDto>(`/projects/${projectId}/notebooks/${notebookId}/conversations`, {
                        method: 'POST',
                        body: JSON.stringify({ title }),
                    }),
                rename: (projectId: string, notebookId: string, convoId: string, title: string) =>
                    callApi<void>(`/projects/${projectId}/notebooks/${notebookId}/conversations/${convoId}`, {
                        method: 'PUT',
                        body: JSON.stringify({ title }),
                    }),
                delete: (projectId: string, notebookId: string, convoId: string) =>
                    callApi<void>(`/projects/${projectId}/notebooks/${notebookId}/conversations/${convoId}`, {
                        method: 'DELETE',
                    }),
                generateTitle: (projectId: string, notebookId: string, convoId: string) =>
                    callApi<{ title: string }>(
                        `/projects/${projectId}/notebooks/${notebookId}/conversations/${convoId}/title/generate`,
                        { method: 'POST' }
                    ),
                checkLlamaRuntime: (_projectId: string, notebookId: string, assistantId?: string) =>
                    callApi<any>(`/notebooks/${notebookId}/llama-runtime${assistantId ? `?assistantId=${assistantId}` : ''}`),
                loadLlamaRuntime: (_projectId: string, notebookId: string, assistantId?: string) =>
                    callApi<any>(`/notebooks/${notebookId}/llama-runtime/load`, {
                        method: 'POST',
                        body: JSON.stringify({ assistantId })
                    }),
                unloadLlamaRuntime: (_projectId: string, notebookId: string, assistantId?: string) =>
                    callApi<import('../types/notebookToolbar').ModelLoadOperationDto>(
                        `/notebooks/${notebookId}/llama-runtime/unload`,
                        {
                            method: 'POST',
                            body: JSON.stringify({ assistantId: assistantId ?? null }),
                        }
                    ),
                pollLlamaRuntimeOperation: (_projectId: string, notebookId: string, operationId: string) =>
                    callApi<any>(`/notebooks/${notebookId}/llama-runtime/operations/${operationId}`),
                restartLlamaRuntime: (_projectId: string, notebookId: string) =>
                    callApi<{ restarted: boolean; termed: boolean; oldPid: number | null; newPid: number | null }>(
                        `/notebooks/${notebookId}/llama-runtime/restart`,
                        { method: 'POST' }
                    ),
                sendMessageStream: async (
                    projectId: string,
                    notebookId: string,
                    convoId: string,
                    data: { instructions: string; assistantName?: string; useAssistantDefinitionModel?: boolean },
                    onEvent: (event: { type: string; data: any }) => void,
                    onError: (error: Error) => void,
                    onComplete: () => void,
                    abortSignal?: AbortSignal
                ) => {
                    const response = await fetchWithAuth(`${API_BASE_URL}/projects/${projectId}/notebooks/${notebookId}/conversations/${convoId}/messages`, {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json',
                            'Accept': 'text/event-stream',
                        },
                        body: JSON.stringify(data),
                        signal: abortSignal,
                    });

                    if (!response.ok) {
                        const errorBody = await response.json().catch(() => null);
                        const err: any = new Error(`HTTP ${response.status}: ${response.statusText}`);
                        err.status = response.status;
                        err.body = errorBody;
                        throw err;
                    }

                    if (!response.body) {
                        throw new Error('No response body for streaming');
                    }

                    const reader = response.body.getReader();
                    const decoder = new TextDecoder();
                    let buffer = '';
                    let currentEventType = '';

                    try {
                        while (true) {
                            if (abortSignal?.aborted) {
                                throw new DOMException('Operation was aborted', 'AbortError');
                            }

                            const { done, value } = await reader.read();
                            if (done) break;

                            buffer += decoder.decode(value, { stream: true });
                            const lines = buffer.split('\n');
                            buffer = lines.pop() || '';

                            for (const line of lines) {
                                if (abortSignal?.aborted) {
                                    throw new DOMException('Operation was aborted', 'AbortError');
                                }

                                if (line.startsWith('event: ')) {
                                    currentEventType = line.slice(7);
                                } else if (line.startsWith('data: ')) {
                                    const eventData = line.slice(6);
                                    if (eventData === '[DONE]') {
                                        onComplete();
                                        return;
                                    }
                                    
                                    try {
                                        const parsed = JSON.parse(eventData);
                                        onEvent({ type: currentEventType || 'data', data: parsed });
                                    } catch (err) {
                                        console.error('Failed to parse SSE data:', err);
                                    }
                                } else if (line === '') {
                                    currentEventType = '';
                                }
                            }
                        }
                        onComplete();
                    } catch (err) {
                        if (err instanceof DOMException && err.name === 'AbortError') {
                            console.log('Stream was cancelled by user');
                            return;
                        }
                        onError(err as Error);
                    } finally {
                        reader.releaseLock();
                    }
                },

                editMessage: (
                    projectId: string,
                    notebookId: string,
                    convoId: string,
                    messageId: string,
                    content: string
                ) =>
                    callApi<void>(
                        `/projects/${projectId}/notebooks/${notebookId}/conversations/${convoId}/messages/${messageId}`,
                        {
                            method: 'PATCH',
                            body: JSON.stringify({ content }),
                        }
                    ),

                undoLast: (projectId: string, notebookId: string, convoId: string) =>
                    callApi<void>(
                        `/projects/${projectId}/notebooks/${notebookId}/conversations/${convoId}/messages/last`,
                        {
                            method: 'DELETE',
                        }
                    ),
                saveAs: (projectId: string, notebookId: string, convoId: string) =>
                    callApi<import('../types/notebook').NotebookFileDto>(
                        `/projects/${projectId}/notebooks/${notebookId}/conversations/${convoId}/save-as`,
                        { method: 'POST' }
                    ),
            },

            setHomePageFile: (projId: string, nbId: string, fileId: string) =>
                callApi<void>(`/projects/${projId}/notebooks/${nbId}/homepage/${fileId}`, { method: 'POST' }),
            
            clearHomePage: (projId: string, nbId: string) =>
                callApi<void>(`/projects/${projId}/notebooks/${nbId}/homepage`, { method: 'DELETE' }),
        },
    },
    links: {
        create: (data: { projectId: string; url: string }) => 
            callApi<ProjectLink>(`/projects/${data.projectId}/links`, {
                method: 'POST',
                body: JSON.stringify({ url: data.url }),
            }),
        
        update: (linkId: string, updates: Partial<ProjectLink>) => 
            callApi<ProjectLink>(`/projects/${updates.projectId}/links/${linkId}`, {
                method: 'PUT',
                body: JSON.stringify({ url: updates.url }),
            }),

        delete: (linkId: string, projectId: string) =>
            callApi<void>(`/projects/${projectId}/links/${linkId}`, {
                method: 'DELETE',
            })
    },
    lineage: {
        getEvent: (eventId: string) => callApi<FileLineageEvent>(`/lineage/${eventId}`),

        download: async (eventId: string) => {
            const response = await fetchWithAuth(`${API_BASE_URL}/lineage/${eventId}/download`);

            if (!response.ok) {
                throw new Error('Failed to download lineage file');
            }

            const blob = await response.blob();
            const contentType = response.headers.get('Content-Type') || 'application/octet-stream';
            const fileName = response.headers.get('Content-Disposition')?.split('filename=')[1] || 'download';

            return { blob, contentType, fileName };
        },
    },
    settings: {
        getSections: () =>
            callApi<SettingsSectionSummaryDto[]>('/settings/sections'),

        getSchema: () =>
            callApi<SettingsSchemaDto>('/settings/schema'),

        getReadiness: () =>
            callApi<SettingsReadinessDto>('/settings/readiness'),

        getSection: (sectionName: string) =>
            callApi<SettingsSectionDto>(`/settings/sections/${encodeURIComponent(sectionName)}`),

        updateSection: (sectionName: string, request: UpdateSettingsSectionRequest) =>
            callApi<SettingsSectionDto>(`/settings/sections/${encodeURIComponent(sectionName)}`, {
                method: 'PUT',
                body: JSON.stringify(request),
            }),

        rebuildEmbeddings: () =>
            callApi<EmbeddingsRebuildResponse>('/settings/embeddings/rebuild', {
                method: 'POST',
            }),

        getModels: () =>
            callApi<SettingsModelDto[]>('/settings/models'),

        addModel: (request: AddModelRequest) =>
            callApi<AddModelResponse>('/settings/models:add', {
                method: 'POST',
                body: JSON.stringify(request),
            }),

        updateModel: (modelId: string, request: UpdateSettingsModelRequest) =>
            callApi<SettingsModelDto>(`/settings/models/${encodeURIComponent(modelId)}`, {
                method: 'PUT',
                body: JSON.stringify(request),
            }),

        deleteModel: (modelId: string) =>
            callApi<void>(`/settings/models/${encodeURIComponent(modelId)}`, {
                method: 'DELETE',
            }),

        getRuntimeProfiles: () =>
            callApi<SettingsRuntimeProfileDto[]>('/settings/runtime-profiles'),

        getRuntimeProfile: (profileId: string) =>
            callApi<SettingsRuntimeProfileDto>(`/settings/runtime-profiles/${encodeURIComponent(profileId)}`),

        createRuntimeProfile: (request: CreateRuntimeProfileRequest) =>
            callApi<SettingsRuntimeProfileDto>('/settings/runtime-profiles', {
                method: 'POST',
                body: JSON.stringify(request),
            }),

        updateRuntimeProfile: (profileId: string, request: UpdateRuntimeProfileRequest) =>
            callApi<SettingsRuntimeProfileDto>(`/settings/runtime-profiles/${encodeURIComponent(profileId)}`, {
                method: 'PUT',
                body: JSON.stringify(request),
            }),

        deleteRuntimeProfile: (profileId: string) =>
            callApi<void>(`/settings/runtime-profiles/${encodeURIComponent(profileId)}`, {
                method: 'DELETE',
            }),

        getLlamaInventory: () => callApi<LlamaRuntimeInventoryItemDto[]>('/settings/llama/runtime/inventory'),

        /**
         * Phase F (R-6.10): per-alias runtime status snapshot for the Local
         * Llama Runtime diagnostics panel. Read-only — does not mutate any
         * coordinator lock or server-side runtime state.
         */
        getLlamaRuntimeStatus: () =>
            callApi<LlamaRuntimeAliasStatusDto[]>('/settings/llama/runtime/status'),

        loadLlamaModel: (routerModelId: string) =>
            callApi<void>('/settings/llama/runtime/load', {
                method: 'POST',
                body: JSON.stringify({ routerModelId }),
            }),

        unloadLlamaModel: (routerModelId: string) =>
            callApi<void>('/settings/llama/runtime/unload', {
                method: 'POST',
                body: JSON.stringify({ routerModelId }),
            }),

        getDownloadStatus: (operationId: string) =>
            callApi<ModelDownloadOperationDto>(`/settings/llama/downloads/${encodeURIComponent(operationId)}`),

        /**
         * List files in a public or gated Hugging Face repo to populate the
         * per-service file pickers (llama-cpp, Image Generation, Speech
         * Transcription, Speech Synthesis, Embeddings). Accepts either
         * "owner/repo" or a full Hugging Face model URL on the client and
         * splits before calling; the server injects the configured HF token
         * (browser never sees it). Structured errors carry a `code`
         * property (REPO_NOT_FOUND, REPO_TOKEN_MISSING, etc.).
         *
         * Optional {@link options.serviceOrigin} is sent as the
         * `X-Service-Origin` request header so server-side telemetry can
         * report which service initiated the browse. The endpoint itself is
         * service-agnostic; all classification happens client-side.
         *
         * Optional {@link options.signal} supports cancellation via
         * {@link AbortController} so the shared picker can abort an
         * in-flight browse on `Esc` or repo-change.
         */
        browseHuggingFaceRepository: (
            repository: string,
            options: { serviceOrigin?: string; signal?: AbortSignal } = {},
        ) => {
            const parsed = parseHuggingFaceRepository(repository);
            if (!parsed) {
                return Promise.reject(
                    Object.assign(new Error('Repository must be in the form "owner/repo".'), { code: 'REPO_INVALID' }),
                );
            }
            const [owner, repo] = parsed;
            const init: RequestInit = {};
            if (options.signal) {
                init.signal = options.signal;
            }
            const trimmedOrigin = (options.serviceOrigin ?? '').trim();
            if (trimmedOrigin) {
                init.headers = { 'X-Service-Origin': trimmedOrigin };
            }
            return callApi<HuggingFaceRepositoryListingDto>(
                `/settings/huggingface/repositories/${encodeURIComponent(owner)}/${encodeURIComponent(repo)}/files`,
                init,
            );
        },

        deleteLlamaRouterEntry: (routerModelId: string) =>
            callApi<void>(`/settings/llama/router/entries/${encodeURIComponent(routerModelId)}`, {
                method: 'DELETE',
            }),

        getOverview: () =>
            callApi<SettingsOverviewDto>('/settings/overview'),

        chatDefaults: {
            get: () => callApi<ChatDefaultsDto>('/settings/chat-defaults'),
            update: (request: UpdateChatDefaultsRequest) =>
                callApi<ChatDefaultsDto>('/settings/chat-defaults', {
                    method: 'PUT',
                    body: JSON.stringify(request),
                }),
        },

        routing: {
            /**
             * Batch chat-target readiness snapshot used by Catalog and Overview.
             * Avoids per-model path-segment issues for ids that include `/`.
             */
            getChatTargetsPreflight: () =>
                callApi<ChatTargetReadinessDto[]>('/settings/routing/chat-targets/preflight'),

            /**
             * Per-model readiness probe used by the Models & Runtime Catalog
             * readiness badge column. Passing `strict: true` flips blocked
             * -> 409 problem+json for callers that need a non-2xx failure
             * path; the default 200 path lets the UI render an inline blocked
             * badge without error handling.
             */
            getChatTargetReadiness: (modelId: string, strict = false) =>
                callApi<ChatTargetReadinessDto>(
                    `/settings/routing/chat-targets/${encodeURIComponent(modelId)}/readiness?strict=${strict ? 'true' : 'false'}`
                ),
        },

        connections: {
            getUsage: (section: string) =>
                callApi<ConnectionUsageDto>(`/settings/connections/${encodeURIComponent(section)}/usage`),
        },

        infrastructure: {
            listDependencies: () =>
                callApi<SettingsRuntimeDependencyDto[]>('/settings/infrastructure/dependencies'),

            updateDependency: (key: string, value: string | null) =>
                callApi<SettingsRuntimeDependencyDto>(`/settings/infrastructure/dependencies/${encodeURIComponent(key)}`, {
                    method: 'PUT',
                    body: JSON.stringify({ value }),
                }),

            probe: (items: InfrastructureProbeRequestItemDto[]) =>
                callApi<InfrastructureProbeBatchDto>('/settings/infrastructure/probes', {
                    method: 'POST',
                    body: JSON.stringify({ items }),
                }),
        },

        services: {
            get: (serviceId: string) =>
                callApi<ServiceEditorStateDto>(`/settings/services/${encodeURIComponent(serviceId)}`),

            updateActiveProvider: (serviceId: string, providerId: string) =>
                callApi<ServiceEditorStateDto>(`/settings/services/${encodeURIComponent(serviceId)}/active-provider`, {
                    method: 'PUT',
                    body: JSON.stringify({ providerId }),
                }),

            updateProviderFields: (
                serviceId: string,
                providerId: string,
                fields: Record<string, string | null | undefined>
            ) =>
                callApi<ServiceEditorStateDto>(
                    `/settings/services/${encodeURIComponent(serviceId)}/providers/${encodeURIComponent(providerId)}`,
                    {
                        method: 'PUT',
                        body: JSON.stringify({ fields }),
                    }
                ),

            getReadiness: (serviceId: string) =>
                callApi<any>(`/settings/services/${encodeURIComponent(serviceId)}/readiness`),
        },

        localModels: {
            /** Use for service editor: avoids console spam on expected 404. */
            listOutcome: (serviceId: string) => fetchLocalModelsListOutcome(serviceId),

            list: (serviceId: string) =>
                callApi<any>(`/settings/services/${encodeURIComponent(serviceId)}/local-models`),

            startDownload: (serviceId: string, request: Record<string, unknown>) =>
                callApi<any>(`/settings/services/${encodeURIComponent(serviceId)}/local-models/downloads`, {
                    method: 'POST',
                    body: JSON.stringify(request),
                }),

            getOperation: (serviceId: string, operationId: string) =>
                callApi<any>(
                    `/settings/services/${encodeURIComponent(serviceId)}/local-models/operations/${encodeURIComponent(operationId)}`
                ),

            cancelOperation: (serviceId: string, operationId: string) =>
                callApi<any>(
                    `/settings/services/${encodeURIComponent(serviceId)}/local-models/operations/${encodeURIComponent(operationId)}/cancel`,
                    {
                        method: 'POST',
                    }
                ),

            get: (serviceId: string, modelRef: string) =>
                callApi<any>(`/settings/services/${encodeURIComponent(serviceId)}/local-models/${encodeURIComponent(modelRef)}`),

            selectActive: (serviceId: string, modelRef: string) =>
                callApi<any>(`/settings/services/${encodeURIComponent(serviceId)}/local-models/${encodeURIComponent(modelRef)}/select-active`, {
                    method: 'POST',
                }),

            remove: (serviceId: string, modelRef: string) =>
                callApi<void>(`/settings/services/${encodeURIComponent(serviceId)}/local-models/${encodeURIComponent(modelRef)}`, {
                    method: 'DELETE',
                }),

            /**
             * Load the local model / engine for the given service.
             *
             * - ASR / TTS: `request` MUST carry `model_id` or `model_path` plus
             *   optional runtime knobs, which the sub-service loads into
             *   memory. Pass `{}` only if the caller intends a no-op probe.
             * - Image Generation: the active bundle is authoritative on disk
             *   (set via `selectActive`), so the request body is ignored.
             *   Pass `{}`.
             */
            load: (serviceId: string, request: Record<string, unknown>) =>
                callApi<any>(`/settings/services/${encodeURIComponent(serviceId)}/local-models/load`, {
                    method: 'POST',
                    body: JSON.stringify(request),
                }),

            unload: (serviceId: string) =>
                callApi<any>(`/settings/services/${encodeURIComponent(serviceId)}/local-models/unload`, {
                    method: 'POST',
                    body: JSON.stringify({}),
                }),

            /** ASR / TTS / Embeddings: curated catalog from engine /admin/catalog. */
            catalogOutcome: (serviceId: string) => fetchLocalModelCatalogOutcome(serviceId),

            /**
             * SpeechSynthesis only: baked voice-pack presets for models whose
             * catalog voiceInput is voice_pack (e.g. chatterbox). Not a Hugging
             * Face download — the pack ships in the image. Used to populate the
             * voice picker instead of any hardcoded list.
             */
            voicePackOutcome: (serviceId: string) => fetchLocalModelVoicePackOutcome(serviceId),

            /**
             * SpeechSynthesis only: runtime speaker ids from the loaded TTS model
             * (audiocpp_server GET /v1/audio/voices). Used for catalog voiceInput
             * builtin entries.
             */
            voicesOutcome: (serviceId: string) => fetchLocalModelVoicesOutcome(serviceId),

            /** ASR / TTS only: /ready passthrough. */
            runtimeReadinessOutcome: (serviceId: string) => fetchRuntimeReadinessOutcome(serviceId),
        },
    },
    usage: {
        getSummary: (from: string, to: string, bucket: UsageBucket) => {
            const params = new URLSearchParams({ from, to, bucket });
            return callApi<UsageSummaryDto>(`/usage/summary?${params.toString()}`);
        },

        getSummaryByProject: (from: string, to: string) => {
            const params = new URLSearchParams({ from, to });
            return callApi<ProjectUsageSummaryDto[]>(`/usage/by-project?${params.toString()}`);
        },

        getProjectSummary: (projectId: string, from: string, to: string, bucket: UsageBucket) => {
            const params = new URLSearchParams({ from, to, bucket });
            return callApi<UsageSummaryDto>(`/usage/projects/${projectId}/summary?${params.toString()}`);
        },

        getDetails: (query: UsageDetailsQuery) => {
            const params = new URLSearchParams({
                from: query.from,
                to: query.to,
                ...(query.projectId ? { projectId: query.projectId } : {}),
                ...(query.category ? { category: query.category } : {}),
                ...(query.service ? { service: query.service } : {}),
                ...(query.operation ? { operation: query.operation } : {}),
                ...(query.page !== undefined ? { page: String(query.page) } : {}),
                ...(query.pageSize !== undefined ? { pageSize: String(query.pageSize) } : {}),
            });
            return callApi<PagedResultDto<UsageEventDto>>(`/usage/details?${params.toString()}`);
        },

        getProjectDetails: (projectId: string, query: Omit<UsageDetailsQuery, 'projectId'>) => {
            const params = new URLSearchParams({
                from: query.from,
                to: query.to,
                ...(query.category ? { category: query.category } : {}),
                ...(query.service ? { service: query.service } : {}),
                ...(query.operation ? { operation: query.operation } : {}),
                ...(query.page !== undefined ? { page: String(query.page) } : {}),
                ...(query.pageSize !== undefined ? { pageSize: String(query.pageSize) } : {}),
            });
            return callApi<PagedResultDto<UsageEventDto>>(`/usage/projects/${projectId}/details?${params.toString()}`);
        },

        getBreakdown: (from: string, to: string) => {
            const params = new URLSearchParams({ from, to });
            return callApi<UsageBreakdownWithCategoriesDto>(`/usage/breakdown?${params.toString()}`);
        },

        getProjectBreakdown: (projectId: string, from: string, to: string) => {
            const params = new URLSearchParams({ from, to });
            return callApi<UsageBreakdownWithCategoriesDto>(`/usage/projects/${projectId}/breakdown?${params.toString()}`);
        },
    },
    guides: {
        guides: {
            list: (projectId?: string) =>
                callApi<any[]>(`/guides${projectId ? `?projectId=${encodeURIComponent(projectId)}` : ''}`),
            get: (guideId: string, projectId?: string) =>
                callApi<any>(`/guides/${guideId}${projectId ? `?projectId=${encodeURIComponent(projectId)}` : ''}`),
            create: (dto: any) => callApi<any>(`/guides`, { method: 'POST', body: JSON.stringify(dto) }),
            update: (guideId: string, dto: any) => callApi<any>(`/guides/${guideId}`, { method: 'PUT', body: JSON.stringify(dto) }),
            delete: (guideId: string) => callApi<void>(`/guides/${guideId}`, { method: 'DELETE' }),
            duplicate: (guideId: string) => callApi<any>(`/guides/${guideId}/duplicate`, { method: 'POST' }),
            validateRuntime: (dto: any) => callApi<any>(`/guides/runtime/validate`, { method: 'POST', body: JSON.stringify(dto) }),
            mcpToolSources: {
                testConnection: (dto: import('../types/guides').McpTestConnectionRequest) =>
                    callApi<import('../types/guides').McpTestConnectionResponse>(
                        `/guides/tool-sources/mcp/test-connection`,
                        { method: 'POST', body: JSON.stringify(dto) }
                    ),
                discover: (dto: import('../types/guides').McpDiscoverToolsRequest) =>
                    callApi<import('../types/guides').McpDiscoverToolsResponse>(
                        `/guides/tool-sources/mcp/discover`,
                        { method: 'POST', body: JSON.stringify(dto) }
                    ),
            },
            export: async (guideId: string): Promise<Blob> => {
                const response = await fetchWithAuth(`${API_BASE_URL}/guides/${guideId}/export`);
                if (!response.ok) {
                    throw new Error('Export failed');
                }
                return response.blob();
            },
            import: async (file: File): Promise<any> => {
                const formData = new FormData();
                formData.append('file', file);
                const response = await fetchWithAuth(`${API_BASE_URL}/guides/import`, {
                    method: 'POST',
                    body: formData,
                });
                if (!response.ok) {
                    const error = await response.json();
                    throw new Error(error.error || 'Import failed');
                }
                return response.json();
            },
            
            publish: (guideId: string, dto: import('../types/guides').PublishGuideDto) =>
                callApi<import('../types/guides').PublishedGuideDto>(
                    `/guides/${guideId}/publish`,
                    { method: 'POST', body: JSON.stringify(dto) }
                ),
            
            getPublishedInProject: (guideId: string, projectId: string) =>
                callApi<import('../types/guides').PublishedGuideDto | null>(
                    `/guides/${guideId}/publish/in-project/${projectId}`
                ),
            
            updatePublished: (guideId: string, pubId: string, dto: import('../types/guides').UpdatePublishedGuideDto) =>
                callApi<import('../types/guides').PublishedGuideDto>(
                    `/guides/${guideId}/publish/${pubId}`,
                    { method: 'PUT', body: JSON.stringify(dto) }
                ),
            
            deactivatePublished: (guideId: string, pubId: string) =>
                callApi<void>(
                    `/guides/${guideId}/publish/${pubId}/deactivate`,
                    { method: 'POST' }
                ),
            
            reactivatePublished: (guideId: string, pubId: string) =>
                callApi<import('../types/guides').PublishedGuideDto>(
                    `/guides/${guideId}/publish/${pubId}/reactivate`,
                    { method: 'POST' }
                ),
            
            validateFriendlyName: (guideId: string, friendlyName: string) =>
                callApi<{ available: boolean; reason?: string }>(
                    `/guides/${guideId}/publish/validate-friendly-name/${encodeURIComponent(friendlyName)}`
                ),
            
            generateApiKey: (guideId: string, pubId: string) =>
                callApi<import('../types/guides').ApiKeyGenerationResultDto>(
                    `/guides/${guideId}/publish/${pubId}/api-key`,
                    { method: 'POST' }
                ),
            
            removeApiKey: (guideId: string, pubId: string) =>
                callApi<void>(
                    `/guides/${guideId}/publish/${pubId}/api-key`,
                    { method: 'DELETE' }
                ),

            downloadClaudeSkill: async (guideId: string, pubId: string): Promise<Blob> => {
                const response = await fetchWithAuth(
                    `${API_BASE_URL}/guides/${guideId}/publish/${pubId}/claude-skill`
                );
                if (!response.ok) {
                    let message = 'Failed to download Claude skill pack';
                    try {
                        const error = await response.json();
                        message = error.message || error.error || message;
                    } catch {
                        // ignore parse errors
                    }
                    throw new Error(message);
                }
                return response.blob();
            },
        },
        assistants: {
            list: () => callApi<any[]>(`/assistants`),
            get: (assistantId: string, projectId?: string) =>
                callApi<any>(`/assistants/${assistantId}${projectId ? `?projectId=${encodeURIComponent(projectId)}` : ''}`),
            create: (dto: any) => callApi<any>(`/assistants`, { method: 'POST', body: JSON.stringify(dto) }),
            update: (assistantId: string, dto: any) => callApi<any>(`/assistants/${assistantId}`, { method: 'PUT', body: JSON.stringify(dto) }),
            delete: (assistantId: string) => callApi<void>(`/assistants/${assistantId}`, { method: 'DELETE' }),
            duplicate: (assistantId: string) => callApi<any>(`/assistants/${assistantId}/duplicate`, { method: 'POST' }),
            export: async (assistantId: string): Promise<Blob> => {
                const response = await fetchWithAuth(`${API_BASE_URL}/assistants/${assistantId}/export`);
                if (!response.ok) {
                    throw new Error('Export failed');
                }
                return response.blob();
            },
            import: async (file: File): Promise<any> => {
                const formData = new FormData();
                formData.append('file', file);
                const response = await fetchWithAuth(`${API_BASE_URL}/assistants/import`, {
                    method: 'POST',
                    body: formData,
                });
                if (!response.ok) {
                    const error = await response.json();
                    throw new Error(error.error || 'Import failed');
                }
                return response.json();
            },
            
            getFileMarkdownShadow: async (assistantId: string, fileId: string) =>
                callApi<import('../types/guides').AssistantFileMarkdownShadowDto>(
                    `/assistants/${assistantId}/files/${fileId}/markdown`
                ),

            getFileMarkdownContent: async (assistantId: string, fileId: string) => {
                const response = await fetchWithAuth(
                    `${API_BASE_URL}/assistants/${assistantId}/files/${fileId}/markdown/content`
                );

                if (!response.ok) {
                    throw new Error('Failed to fetch markdown content');
                }

                const blob = await response.blob();
                const contentType = response.headers.get('Content-Type') || 'text/markdown';
                const contentDisposition = response.headers.get('Content-Disposition') || '';
                let fileName = 'markdown-content.md';
                
                if (contentDisposition) {
                    const matches = /filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/.exec(contentDisposition);
                    if (matches != null && matches[1]) {
                        fileName = matches[1].replace(/['"]/g, '');
                    }
                }

                return { blob, contentType, fileName };
            },

            retryFileMarkdownExtraction: async (assistantId: string, fileId: string) =>
                callApi<import('../types/guides').AssistantFileMarkdownShadowDto>(
                    `/assistants/${assistantId}/files/${fileId}/markdown/retry`,
                    { method: 'POST' }
                ),
        },
        catalogs: {
            models: () => callApi<any[]>('/catalogs/models'),
            tools: () => callApi<any[]>('/catalogs/tools'),
            globalAssistants: () => callApi<any[]>('/catalogs/global-assistants'),
            globalAssistant: (id: string) => callApi<any>(`/catalogs/global-assistants/${id}`),
        },
        operations: {
            get: (operationId: string) => callApi<any>(`/operations/${operationId}`),
            update: (operationId: string, dto: any) => callApi<any>(`/operations/${operationId}`, { method: 'PUT', body: JSON.stringify(dto) }),
            preview: (dto: import('../types/guides').PreviewToolDefinitionDto) =>
                callApi<import('../types/guides').ToolDefinitionPreviewResult>(`/operations/preview`, { method: 'POST', body: JSON.stringify(dto) }),
        },
    },
    notebooks: {
        headerToolbar: (notebookId: string, conversationId?: string) => {
            const q = conversationId ? `?conversationId=${encodeURIComponent(conversationId)}` : '';
            return callApi<import('../types/notebookToolbar').NotebookHeaderToolbarDto>(`/notebooks/${notebookId}/header-toolbar${q}`);
        },
        chatReadiness: (notebookId: string, conversationId?: string) => {
            const query = new URLSearchParams();
            if (conversationId) {
                query.set('conversationId', conversationId);
            }
            const suffix = query.toString();
            return callApi<NotebookChatReadinessDto>(`/notebooks/${notebookId}/header-toolbar/chat-readiness${suffix ? `?${suffix}` : ''}`);
        },
    },
    systemGuide: {
        getSession: () => callApi<import('../types/systemGuide').SystemGuideSessionDto>('/system-guide/session'),
        getWorkspace: () => callApi<import('../types/systemGuide').SystemGuideWorkspaceDto>('/system-guide/workspace'),
    },
    conversations: {
        getUserConversations: (query?: import('../types/conversation').UserConversationsQuery) => {
            const params = new URLSearchParams();
            if (query?.page) params.append('page', query.page.toString());
            if (query?.pageSize) params.append('pageSize', query.pageSize.toString());
            if (query?.search) params.append('search', query.search);
            if (query?.sortBy) params.append('sortBy', query.sortBy);
            if (query?.sortOrder) params.append('sortOrder', query.sortOrder);
            const queryString = params.toString();
            return callApi<import('../types/conversation').PagedUserConversationsDto>(
                `/conversations${queryString ? '?' + queryString : ''}`
            );
        },
    },
};
