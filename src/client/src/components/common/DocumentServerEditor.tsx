import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { FiAlertTriangle } from 'react-icons/fi';
import LoadingSpinner from '../LoadingSpinner';
import {
    createDocumentServerEditorConfig,
    DocumentServerEditorConfigRequest,
    DocumentServerScope,
} from '../../services/documentServer';
import { ConfirmationDialog } from './ConfirmationDialog';

declare global {
    interface Window {
        DocsAPI?: {
            DocEditor: new (elementId: string, config: Record<string, unknown>) => {
                destroyEditor?: () => void;
            };
        };
    }
}

interface DocumentServerEditorProps {
    scope: DocumentServerScope;
    projectId: string;
    fileId?: string;
    notebookId?: string;
    relativePath?: string;
    canEdit: boolean;
    className?: string;
    showErrorDialogOnError?: boolean;
    onError?: (message: string) => void;
}

export default function DocumentServerEditor({
    scope,
    projectId,
    fileId,
    notebookId,
    relativePath,
    canEdit,
    className,
    showErrorDialogOnError = true,
    onError,
}: DocumentServerEditorProps) {
    const instanceIdRef = useRef(Math.random().toString(36).slice(2));
    const resourceIdentity = fileId || relativePath || 'unknown';
    const containerId = useMemo(
        () => `documentserver-${scope}-${projectId}-${notebookId ?? 'project'}-${resourceIdentity}-${instanceIdRef.current}`.replace(/[^a-zA-Z0-9-_]/g, '-'),
        [scope, projectId, notebookId, resourceIdentity]
    );
    const editorRef = useRef<{ destroyEditor?: () => void } | null>(null);
    const readyTimeoutRef = useRef<number | null>(null);
    const lastReportedErrorRef = useRef<string | null>(null);
    const [isLoading, setIsLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);
    const [isErrorDialogOpen, setIsErrorDialogOpen] = useState(false);
    const [reloadKey, setReloadKey] = useState(0);

    const reportEditorError = useCallback((message: string) => {
        setError(message);
        onError?.(message);

        if (showErrorDialogOnError && lastReportedErrorRef.current !== message) {
            lastReportedErrorRef.current = message;
            setIsErrorDialogOpen(true);
        }
    }, [onError, showErrorDialogOnError]);

    useEffect(() => {
        let isDisposed = false;
        setIsLoading(true);
        setError(null);
        setIsErrorDialogOpen(false);
        lastReportedErrorRef.current = null;

        const clearContainer = () => {
            const container = document.getElementById(containerId);
            if (container) {
                container.replaceChildren();
            }
        };

        const destroyEditor = () => {
            try {
                editorRef.current?.destroyEditor?.();
            } catch (err) {
                console.warn('[DocumentServer] destroyEditor failed', { scope, fileId, err });
            } finally {
                editorRef.current = null;
                clearContainer();
            }
        };

        const setupEditor = async () => {
            const request: DocumentServerEditorConfigRequest = {
                scope,
                projectId,
                fileId,
                notebookId,
                relativePath,
                canEdit,
            };
            console.info('[DocumentServer] editor mount start', request);

            const response = await createDocumentServerEditorConfig(request);
            const scriptUrl = `${response.documentServerUrl.replace(/\/$/, '')}/web-apps/apps/api/documents/api.js`;
            console.info('[DocumentServer] script load start', { scriptUrl, fileId, scope });
            await ensureScriptLoaded(scriptUrl);
            console.info('[DocumentServer] script load success', { scriptUrl, fileId, scope });

            if (isDisposed || !window.DocsAPI?.DocEditor) {
                console.warn('[DocumentServer] DocsAPI not available after script load', { fileId, scope, isDisposed });
                return;
            }

            const config = response.config as Record<string, unknown>;
            const documentConfig = (config.document as Record<string, unknown> | undefined) ?? {};
            const editorConfig = (config.editorConfig as Record<string, unknown> | undefined) ?? {};
            const existingEvents = (config.events as Record<string, unknown> | undefined) ?? {};

            console.info('[DocumentServer] editor URLs', {
                scope,
                fileId,
                documentUrl: documentConfig.url,
                callbackUrl: editorConfig.callbackUrl,
            });

            readyTimeoutRef.current = window.setTimeout(() => {
                if (isDisposed) {
                    return;
                }
                console.error('[DocumentServer] document ready timeout', { scope, fileId, timeoutMs: 20000 });
                const message = 'DocumentServer editor did not become ready within 20 seconds.';
                reportEditorError(message);
                setIsLoading(false);
            }, 20000);

            const runtimeConfig: Record<string, unknown> = {
                ...config,
                events: {
                    ...existingEvents,
                    onAppReady: (event: unknown) => {
                        console.info('[DocumentServer] onAppReady', { scope, fileId, event });
                    },
                    onDocumentReady: (event: unknown) => {
                        console.info('[DocumentServer] onDocumentReady', { scope, fileId, event });
                        if (readyTimeoutRef.current !== null) {
                            window.clearTimeout(readyTimeoutRef.current);
                            readyTimeoutRef.current = null;
                        }
                        setError(null);
                        setIsLoading(false);
                    },
                    onError: (event: unknown) => {
                        const payload = event as { data?: { errorCode?: number; errorDescription?: string } };
                        const errorCode = payload?.data?.errorCode;
                        const errorDescription = payload?.data?.errorDescription;
                        console.error('[DocumentServer] onError', { scope, fileId, errorCode, errorDescription, event });
                        if (readyTimeoutRef.current !== null) {
                            window.clearTimeout(readyTimeoutRef.current);
                            readyTimeoutRef.current = null;
                        }
                        const message = `DocumentServer runtime error${errorCode ? ` (${errorCode})` : ''}${errorDescription ? `: ${errorDescription}` : '.'}`;
                        reportEditorError(message);
                        setIsLoading(false);
                    },
                    onWarning: (event: unknown) => {
                        console.warn('[DocumentServer] onWarning', { scope, fileId, event });
                    },
                },
            };

            destroyEditor();
            editorRef.current = new window.DocsAPI.DocEditor(containerId, runtimeConfig);
            console.info('[DocumentServer] DocEditor created', { containerId, fileId, scope });
            // Keep the container mounted and visible immediately; some documents do not
            // emit onDocumentReady reliably, which previously left the UI stuck on loader.
            setIsLoading(false);
        };

        setupEditor().catch((err) => {
            if (isDisposed) {
                return;
            }

            const message = err instanceof Error ? err.message : 'Failed to load DocumentServer editor.';
            console.error('[DocumentServer] editor mount failed', {
                scope,
                projectId,
                notebookId,
                fileId,
                message,
            });
            reportEditorError(message);
            setIsLoading(false);
        });

        return () => {
            isDisposed = true;
            if (readyTimeoutRef.current !== null) {
                window.clearTimeout(readyTimeoutRef.current);
                readyTimeoutRef.current = null;
            }
            destroyEditor();
        };
    }, [scope, projectId, fileId, notebookId, relativePath, canEdit, containerId, reloadKey, reportEditorError]);

    return (
        <div className={className ? `${className} relative` : 'h-full w-full relative'} style={{ position: 'relative', overflow: 'hidden', isolation: 'isolate' }}>
            <div id={containerId} className="h-full w-full" />
            {isLoading && (
                <div className="absolute inset-0 flex items-center justify-center bg-white/70">
                    <LoadingSpinner message="Loading DocumentServer editor..." />
                </div>
            )}
            {error && (
                <div className="absolute inset-0 z-10 flex items-center justify-center bg-gray-50 p-4" data-testid="documentserver-inline-error">
                    <div className="w-full max-w-md rounded-lg border border-gray-200 bg-white p-5 text-center shadow-sm">
                        <FiAlertTriangle className="mx-auto mb-3 h-7 w-7 text-amber-600" />
                        <h3 className="text-base font-semibold text-gray-900">Document preview unavailable</h3>
                        <p className="mt-2 text-sm leading-relaxed text-gray-600">{error}</p>
                        <div className="mt-5 flex justify-center gap-3">
                            <button
                                type="button"
                                onClick={() => {
                                    setError(null);
                                    setIsErrorDialogOpen(false);
                                    lastReportedErrorRef.current = null;
                                    setReloadKey((current) => current + 1);
                                }}
                                className="px-4 py-2 text-sm rounded-md bg-blue-600 hover:bg-blue-700 text-white focus:outline-none focus:ring-2 focus:ring-blue-600 focus:ring-offset-2"
                            >
                                Retry
                            </button>
                        </div>
                    </div>
                </div>
            )}
            <ConfirmationDialog
                isOpen={Boolean(error && isErrorDialogOpen)}
                onClose={() => setIsErrorDialogOpen(false)}
                onConfirm={() => setIsErrorDialogOpen(false)}
                title="Document preview unavailable"
                message={error ?? 'Failed to load document preview.'}
                confirmText="OK"
                confirmButtonClass="bg-blue-600 hover:bg-blue-700 text-white"
                showCancelButton={false}
                icon={FiAlertTriangle}
            />
        </div>
    );
}

async function ensureScriptLoaded(scriptUrl: string): Promise<void> {
    const existing = document.querySelector<HTMLScriptElement>(`script[data-documentserver="${scriptUrl}"]`);
    if (existing) {
        if (existing.dataset.loaded === 'true') {
            return;
        }

        await waitForScriptLoad(existing);
        return;
    }

    const script = document.createElement('script');
    script.src = scriptUrl;
    script.async = true;
    script.dataset.documentserver = scriptUrl;

    const loadPromise = waitForScriptLoad(script);
    document.head.appendChild(script);
    await loadPromise;
}

function waitForScriptLoad(script: HTMLScriptElement): Promise<void> {
    return new Promise<void>((resolve, reject) => {
        const onLoad = () => {
            script.dataset.loaded = 'true';
            cleanup();
            resolve();
        };

        const onError = () => {
            cleanup();
            reject(new Error('Failed to load DocumentServer client script.'));
        };

        const cleanup = () => {
            script.removeEventListener('load', onLoad);
            script.removeEventListener('error', onError);
        };

        script.addEventListener('load', onLoad);
        script.addEventListener('error', onError);
    });
}
