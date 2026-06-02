import { useEffect, useMemo, useRef, useState } from 'react';
import LoadingSpinner from '../LoadingSpinner';
import {
    createOnlyOfficeEditorConfig,
    OnlyOfficeEditorConfigRequest,
    OnlyOfficeScope,
} from '../../services/onlyOffice';

declare global {
    interface Window {
        DocsAPI?: {
            DocEditor: new (elementId: string, config: Record<string, unknown>) => {
                destroyEditor?: () => void;
            };
        };
    }
}

interface OnlyOfficeEditorProps {
    scope: OnlyOfficeScope;
    projectId: string;
    fileId: string;
    notebookId?: string;
    canEdit: boolean;
    className?: string;
}

export default function OnlyOfficeEditor({
    scope,
    projectId,
    fileId,
    notebookId,
    canEdit,
    className,
}: OnlyOfficeEditorProps) {
    const instanceIdRef = useRef(Math.random().toString(36).slice(2));
    const containerId = useMemo(
        () => `onlyoffice-${scope}-${projectId}-${notebookId ?? 'project'}-${fileId}-${instanceIdRef.current}`.replace(/[^a-zA-Z0-9-_]/g, '-'),
        [scope, projectId, notebookId, fileId]
    );
    const editorRef = useRef<{ destroyEditor?: () => void } | null>(null);
    const readyTimeoutRef = useRef<number | null>(null);
    const [isLoading, setIsLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);

    useEffect(() => {
        let isDisposed = false;
        setIsLoading(true);
        setError(null);

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
                console.warn('[ONLYOFFICE] destroyEditor failed', { scope, fileId, err });
            } finally {
                editorRef.current = null;
                clearContainer();
            }
        };

        const setupEditor = async () => {
            const request: OnlyOfficeEditorConfigRequest = {
                scope,
                projectId,
                fileId,
                notebookId,
                canEdit,
            };
            console.info('[ONLYOFFICE] editor mount start', request);

            const response = await createOnlyOfficeEditorConfig(request);
            const scriptUrl = `${response.documentServerUrl.replace(/\/$/, '')}/web-apps/apps/api/documents/api.js`;
            console.info('[ONLYOFFICE] script load start', { scriptUrl, fileId, scope });
            await ensureScriptLoaded(scriptUrl);
            console.info('[ONLYOFFICE] script load success', { scriptUrl, fileId, scope });

            if (isDisposed || !window.DocsAPI?.DocEditor) {
                console.warn('[ONLYOFFICE] DocsAPI not available after script load', { fileId, scope, isDisposed });
                return;
            }

            const config = response.config as Record<string, unknown>;
            const documentConfig = (config.document as Record<string, unknown> | undefined) ?? {};
            const editorConfig = (config.editorConfig as Record<string, unknown> | undefined) ?? {};
            const existingEvents = (config.events as Record<string, unknown> | undefined) ?? {};

            console.info('[ONLYOFFICE] editor URLs', {
                scope,
                fileId,
                documentUrl: documentConfig.url,
                callbackUrl: editorConfig.callbackUrl,
            });

            readyTimeoutRef.current = window.setTimeout(() => {
                if (isDisposed) {
                    return;
                }
                console.error('[ONLYOFFICE] document ready timeout', { scope, fileId, timeoutMs: 20000 });
                setError('ONLYOFFICE editor did not become ready within 20 seconds.');
                setIsLoading(false);
            }, 20000);

            const runtimeConfig: Record<string, unknown> = {
                ...config,
                events: {
                    ...existingEvents,
                    onAppReady: (event: unknown) => {
                        console.info('[ONLYOFFICE] onAppReady', { scope, fileId, event });
                    },
                    onDocumentReady: (event: unknown) => {
                        console.info('[ONLYOFFICE] onDocumentReady', { scope, fileId, event });
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
                        console.error('[ONLYOFFICE] onError', { scope, fileId, errorCode, errorDescription, event });
                        if (readyTimeoutRef.current !== null) {
                            window.clearTimeout(readyTimeoutRef.current);
                            readyTimeoutRef.current = null;
                        }
                        setError(`ONLYOFFICE runtime error${errorCode ? ` (${errorCode})` : ''}${errorDescription ? `: ${errorDescription}` : '.'}`);
                        setIsLoading(false);
                    },
                    onWarning: (event: unknown) => {
                        console.warn('[ONLYOFFICE] onWarning', { scope, fileId, event });
                    },
                },
            };

            destroyEditor();
            editorRef.current = new window.DocsAPI.DocEditor(containerId, runtimeConfig);
            console.info('[ONLYOFFICE] DocEditor created', { containerId, fileId, scope });
            // Keep the container mounted and visible immediately; some documents do not
            // emit onDocumentReady reliably, which previously left the UI stuck on loader.
            setIsLoading(false);
        };

        setupEditor().catch((err) => {
            if (isDisposed) {
                return;
            }

            const message = err instanceof Error ? err.message : 'Failed to load ONLYOFFICE editor.';
            console.error('[ONLYOFFICE] editor mount failed', {
                scope,
                projectId,
                notebookId,
                fileId,
                message,
            });
            setError(message);
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
    }, [scope, projectId, fileId, notebookId, canEdit, containerId]);

    return (
        <div className={className ?? 'h-full w-full relative'} style={{ overflow: 'hidden', isolation: 'isolate' }}>
            <div id={containerId} className="h-full w-full" />
            {isLoading && (
                <div className="absolute inset-0 flex items-center justify-center bg-white/70">
                    <LoadingSpinner message="Loading ONLYOFFICE editor..." />
                </div>
            )}
            {error && (
                <div className="absolute inset-0 flex items-center justify-center bg-white/90 text-red-600 text-sm px-4 text-center">
                    {error}
                </div>
            )}
        </div>
    );
}

async function ensureScriptLoaded(scriptUrl: string): Promise<void> {
    const existing = document.querySelector<HTMLScriptElement>(`script[data-onlyoffice="${scriptUrl}"]`);
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
    script.dataset.onlyoffice = scriptUrl;

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
            reject(new Error('Failed to load ONLYOFFICE client script.'));
        };

        const cleanup = () => {
            script.removeEventListener('load', onLoad);
            script.removeEventListener('error', onError);
        };

        script.addEventListener('load', onLoad);
        script.addEventListener('error', onError);
    });
}
