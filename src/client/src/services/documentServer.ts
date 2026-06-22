import { API_BASE_URL } from '../config/apiConfig';
import { withAuthFetchInit, withAuthHeaders } from './authService';
import { broadcastAuthExpired } from './authEvents';

export type DocumentServerScope = 'project' | 'notebook';

export interface DocumentServerCapabilities {
    enabled: boolean;
    publicUrl: string;
    supportedExtensions: string[];
    supportedContentTypes: string[];
}

export interface DocumentServerEditorConfigRequest {
    scope: DocumentServerScope;
    projectId: string;
    fileId?: string;
    notebookId?: string;
    relativePath?: string;
    canEdit: boolean;
    userId?: string;
    userName?: string;
}

export interface DocumentServerEditorConfigResponse {
    documentServerUrl: string;
    config: Record<string, unknown>;
}

const OFFICE_EXTENSIONS = new Set([
    'csv', 'doc', 'docm', 'docx', 'dot', 'dotm', 'dotx', 'epub', 'fb2',
    'odp', 'ods', 'odt', 'pot', 'potm', 'potx', 'pps', 'ppsm', 'ppsx', 'ppt',
    'pptm', 'pptx', 'rtf', 'txt', 'xls', 'xlsb', 'xlsm', 'xlsx', 'xlt', 'xltm', 'xltx',
]);

const OFFICE_CONTENT_TYPE_MARKERS = [
    'application/vnd.openxmlformats-officedocument',
    'application/vnd.ms-excel',
    'application/vnd.ms-powerpoint',
    'application/msword',
    'application/vnd.oasis.opendocument',
];
const EXCLUDED_DOCUMENT_SERVER_EXTENSIONS = new Set(['pdf', 'htm', 'html']);
const EXCLUDED_DOCUMENT_SERVER_CONTENT_TYPES = ['application/pdf', 'text/html', 'application/xhtml+xml'];

let cachedCapabilities: DocumentServerCapabilities | null = null;
const DOCUMENT_SERVER_REQUEST_TIMEOUT_MS = 10000;

export async function getDocumentServerCapabilities(forceRefresh = false): Promise<DocumentServerCapabilities> {
    if (!forceRefresh && cachedCapabilities) {
        console.info('[DocumentServer] capabilities cache hit', {
            enabled: cachedCapabilities.enabled,
            publicUrl: cachedCapabilities.publicUrl,
        });
        return cachedCapabilities;
    }

    console.info('[DocumentServer] capabilities request start', {
        url: `${API_BASE_URL}/documentserver/capabilities`,
        forceRefresh,
    });
    const response = await fetchWithTimeout(
        `${API_BASE_URL}/documentserver/capabilities`,
        {},
        DOCUMENT_SERVER_REQUEST_TIMEOUT_MS
    );
    if (!response.ok) {
        const message = await readDocumentServerError(response, 'Failed to load DocumentServer capabilities.');
        console.error('[DocumentServer] capabilities request failed', {
            status: response.status,
            message,
        });
        throw new Error(message);
    }

    cachedCapabilities = await response.json() as DocumentServerCapabilities;
    console.info('[DocumentServer] capabilities request success', {
        enabled: cachedCapabilities.enabled,
        publicUrl: cachedCapabilities.publicUrl,
        supportedExtensionsCount: cachedCapabilities.supportedExtensions?.length ?? 0,
        supportedContentTypesCount: cachedCapabilities.supportedContentTypes?.length ?? 0,
    });
    return cachedCapabilities;
}

export async function createDocumentServerEditorConfig(
    request: DocumentServerEditorConfigRequest
): Promise<DocumentServerEditorConfigResponse> {
    console.info('[DocumentServer] editor-config request start', {
        scope: request.scope,
        projectId: request.projectId,
        notebookId: request.notebookId,
        fileId: request.fileId,
        relativePath: request.relativePath,
        canEdit: request.canEdit,
    });
    const response = await fetchWithTimeout(
        `${API_BASE_URL}/documentserver/editor-config`,
        {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
            },
            body: JSON.stringify(request),
        },
        DOCUMENT_SERVER_REQUEST_TIMEOUT_MS
    );

    if (!response.ok) {
        const message = await readDocumentServerError(response, 'Failed to create DocumentServer editor config.');
        console.error('[DocumentServer] editor-config request failed', {
            status: response.status,
            message,
            scope: request.scope,
            fileId: request.fileId,
            relativePath: request.relativePath,
        });
        throw new Error(message);
    }

    const payload = await response.json() as DocumentServerEditorConfigResponse;
    console.info('[DocumentServer] editor-config request success', {
        scope: request.scope,
        fileId: request.fileId,
        relativePath: request.relativePath,
        documentServerUrl: payload.documentServerUrl,
    });
    return payload;
}

export function isDocumentServerSupportedByExtension(fileName: string, capabilities: DocumentServerCapabilities | null): boolean {
    if (!capabilities?.enabled) {
        return false;
    }

    const extension = fileName.split('.').pop()?.toLowerCase();
    if (!extension) {
        return false;
    }
    if (EXCLUDED_DOCUMENT_SERVER_EXTENSIONS.has(extension)) {
        return false;
    }

    return capabilities.supportedExtensions.some((value) => value.toLowerCase() === extension);
}

export function isDocumentServerSupportedByContentType(contentType: string, capabilities: DocumentServerCapabilities | null): boolean {
    if (!capabilities?.enabled) {
        return false;
    }

    if (!contentType) {
        return false;
    }

    const normalizedContentType = contentType.toLowerCase();
    if (EXCLUDED_DOCUMENT_SERVER_CONTENT_TYPES.some((value) => normalizedContentType.startsWith(value))) {
        return false;
    }

    return capabilities.supportedContentTypes.some((value) => value.toLowerCase() === normalizedContentType);
}

export function looksLikeDocumentServerFile(fileName: string, contentType?: string | null): boolean {
    const extension = fileName.split('.').pop()?.toLowerCase();
    if (extension && EXCLUDED_DOCUMENT_SERVER_EXTENSIONS.has(extension)) {
        return false;
    }
    if (extension && OFFICE_EXTENSIONS.has(extension)) {
        return true;
    }

    if (!contentType) {
        return false;
    }

    const lowerContentType = contentType.toLowerCase();
    if (EXCLUDED_DOCUMENT_SERVER_CONTENT_TYPES.some((value) => lowerContentType.startsWith(value))) {
        return false;
    }
    return OFFICE_CONTENT_TYPE_MARKERS.some((marker) => lowerContentType.includes(marker));
}

async function readDocumentServerError(response: Response, defaultMessage: string): Promise<string> {
    const statusPrefix = `HTTP ${response.status}`;
    const raw = await response.text();
    if (!raw) {
        return `${defaultMessage} (${statusPrefix})`;
    }

    try {
        const parsed = JSON.parse(raw) as { message?: string };
        if (parsed?.message) {
            return `${parsed.message} (${statusPrefix})`;
        }
    } catch {
        // Keep raw response text when the body is not JSON.
    }

    return `${raw} (${statusPrefix})`;
}

async function fetchWithTimeout(
    input: RequestInfo | URL,
    init: RequestInit,
    timeoutMs: number
): Promise<Response> {
    const controller = new AbortController();
    const timeoutHandle = window.setTimeout(() => controller.abort(), timeoutMs);
    try {
        const response = await fetch(input, withAuthFetchInit({
            ...init,
            headers: withAuthHeaders(init.headers),
            signal: controller.signal,
        }));
        if (response.status === 401) {
            broadcastAuthExpired('Authentication expired.');
        }
        return response;
    } catch (error) {
        if (error instanceof DOMException && error.name === 'AbortError') {
            throw new Error(`DocumentServer request timed out after ${timeoutMs}ms.`);
        }
        throw error;
    } finally {
        window.clearTimeout(timeoutHandle);
    }
}
