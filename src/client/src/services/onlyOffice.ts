import { API_BASE_URL } from '../config/apiConfig';

export type OnlyOfficeScope = 'project' | 'notebook';

export interface OnlyOfficeCapabilities {
    enabled: boolean;
    publicUrl: string;
    supportedExtensions: string[];
    supportedContentTypes: string[];
}

export interface OnlyOfficeEditorConfigRequest {
    scope: OnlyOfficeScope;
    projectId: string;
    fileId: string;
    notebookId?: string;
    canEdit: boolean;
    userId?: string;
    userName?: string;
}

export interface OnlyOfficeEditorConfigResponse {
    documentServerUrl: string;
    config: Record<string, unknown>;
}

const OFFICE_EXTENSIONS = new Set([
    'csv', 'doc', 'docm', 'docx', 'dot', 'dotm', 'dotx', 'epub', 'fb2', 'htm', 'html',
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
const EXCLUDED_ONLYOFFICE_EXTENSIONS = new Set(['pdf']);
const EXCLUDED_ONLYOFFICE_CONTENT_TYPES = ['application/pdf'];

let cachedCapabilities: OnlyOfficeCapabilities | null = null;
const ONLYOFFICE_REQUEST_TIMEOUT_MS = 10000;

export async function getOnlyOfficeCapabilities(forceRefresh = false): Promise<OnlyOfficeCapabilities> {
    if (!forceRefresh && cachedCapabilities) {
        console.info('[ONLYOFFICE] capabilities cache hit', {
            enabled: cachedCapabilities.enabled,
            publicUrl: cachedCapabilities.publicUrl,
        });
        return cachedCapabilities;
    }

    console.info('[ONLYOFFICE] capabilities request start', {
        url: `${API_BASE_URL}/onlyoffice/capabilities`,
        forceRefresh,
    });
    const response = await fetchWithTimeout(
        `${API_BASE_URL}/onlyoffice/capabilities`,
        {},
        ONLYOFFICE_REQUEST_TIMEOUT_MS
    );
    if (!response.ok) {
        const message = await readOnlyOfficeError(response, 'Failed to load ONLYOFFICE capabilities.');
        console.error('[ONLYOFFICE] capabilities request failed', {
            status: response.status,
            message,
        });
        throw new Error(message);
    }

    cachedCapabilities = await response.json() as OnlyOfficeCapabilities;
    console.info('[ONLYOFFICE] capabilities request success', {
        enabled: cachedCapabilities.enabled,
        publicUrl: cachedCapabilities.publicUrl,
        supportedExtensionsCount: cachedCapabilities.supportedExtensions?.length ?? 0,
        supportedContentTypesCount: cachedCapabilities.supportedContentTypes?.length ?? 0,
    });
    return cachedCapabilities;
}

export async function createOnlyOfficeEditorConfig(
    request: OnlyOfficeEditorConfigRequest
): Promise<OnlyOfficeEditorConfigResponse> {
    console.info('[ONLYOFFICE] editor-config request start', {
        scope: request.scope,
        projectId: request.projectId,
        notebookId: request.notebookId,
        fileId: request.fileId,
        canEdit: request.canEdit,
    });
    const response = await fetchWithTimeout(
        `${API_BASE_URL}/onlyoffice/editor-config`,
        {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
            },
            body: JSON.stringify(request),
        },
        ONLYOFFICE_REQUEST_TIMEOUT_MS
    );

    if (!response.ok) {
        const message = await readOnlyOfficeError(response, 'Failed to create ONLYOFFICE editor config.');
        console.error('[ONLYOFFICE] editor-config request failed', {
            status: response.status,
            message,
            scope: request.scope,
            fileId: request.fileId,
        });
        throw new Error(message);
    }

    const payload = await response.json() as OnlyOfficeEditorConfigResponse;
    console.info('[ONLYOFFICE] editor-config request success', {
        scope: request.scope,
        fileId: request.fileId,
        documentServerUrl: payload.documentServerUrl,
    });
    return payload;
}

export function isOnlyOfficeSupportedByExtension(fileName: string, capabilities: OnlyOfficeCapabilities | null): boolean {
    if (!capabilities?.enabled) {
        return false;
    }

    const extension = fileName.split('.').pop()?.toLowerCase();
    if (!extension) {
        return false;
    }
    if (EXCLUDED_ONLYOFFICE_EXTENSIONS.has(extension)) {
        return false;
    }

    return capabilities.supportedExtensions.some((value) => value.toLowerCase() === extension);
}

export function isOnlyOfficeSupportedByContentType(contentType: string, capabilities: OnlyOfficeCapabilities | null): boolean {
    if (!capabilities?.enabled) {
        return false;
    }

    if (!contentType) {
        return false;
    }

    const normalizedContentType = contentType.toLowerCase();
    if (EXCLUDED_ONLYOFFICE_CONTENT_TYPES.some((value) => normalizedContentType.startsWith(value))) {
        return false;
    }

    return capabilities.supportedContentTypes.some((value) => value.toLowerCase() === normalizedContentType);
}

export function looksLikeOnlyOfficeFile(fileName: string, contentType?: string | null): boolean {
    const extension = fileName.split('.').pop()?.toLowerCase();
    if (extension && EXCLUDED_ONLYOFFICE_EXTENSIONS.has(extension)) {
        return false;
    }
    if (extension && OFFICE_EXTENSIONS.has(extension)) {
        return true;
    }

    if (!contentType) {
        return false;
    }

    const lowerContentType = contentType.toLowerCase();
    if (EXCLUDED_ONLYOFFICE_CONTENT_TYPES.some((value) => lowerContentType.startsWith(value))) {
        return false;
    }
    return OFFICE_CONTENT_TYPE_MARKERS.some((marker) => lowerContentType.includes(marker));
}

async function readOnlyOfficeError(response: Response, defaultMessage: string): Promise<string> {
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
        return await fetch(input, { ...init, signal: controller.signal });
    } catch (error) {
        if (error instanceof DOMException && error.name === 'AbortError') {
            throw new Error(`ONLYOFFICE request timed out after ${timeoutMs}ms.`);
        }
        throw error;
    } finally {
        window.clearTimeout(timeoutHandle);
    }
}
