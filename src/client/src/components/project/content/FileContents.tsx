import React, { useState, useEffect, useMemo, useRef } from 'react';
import { api } from '../../../services/api';
import LoadingSpinner from '../../LoadingSpinner';
import MarkdownViewer from '@/components/common/MarkdownViewer';
import PreviewContainer from '@/components/common/PreviewContainer';
import { FaRegEdit } from 'react-icons/fa';
import TextViewer from '@/components/common/TextViewer';
import { ImageViewer } from '@/components/common/ImageViewer';
import PdfViewer from '@/components/common/PdfViewer';
import AudioPlayer from '@/components/common/AudioPlayer';
import VideoPlayer from '@/components/common/VideoPlayer';
import { FileLoadingErrorBoundary } from '@/components/common/FileLoadingErrorBoundary';
import { resolveHtmlResources, cleanupBlobUrls } from '@/utils/htmlResourceResolver';

interface FileContentsProps {
    inlineMode?: boolean;
    projectId: string;
    fileId: string;
    contentType: string;
    version?: number;
    resolveProjectFilePath?: (relativePath: string) => string | undefined;
    onEditMarkdown?: () => void; // delegate to parent for identical behavior
}

function FileContentsComponent({ projectId, fileId, contentType, version, inlineMode = false, resolveProjectFilePath, onEditMarkdown }: FileContentsProps) {
    const [content, setContent] = useState<{ blob: Blob; contentType: string; fileName: string } | null>(null);
    const [textContent, setTextContent] = useState<string | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [isLoading, setIsLoading] = useState(true);
    
    // HTML resource resolution state
    const [htmlContent, setHtmlContent] = useState<string | null>(null);
    const [isLoadingHtmlResources, setIsLoadingHtmlResources] = useState(false);
    const htmlResourceBlobUrlsRef = useRef<Set<string>>(new Set());

    // Track state transitions
    React.useEffect(() => {
        const fileIdKey = `${projectId}:${fileId}:${version || 'latest'}`;
        console.log('[TELEMETRY] FileContents state transition', {
            fileIdKey,
            isLoading,
            hasContent: !!content,
            hasTextContent: !!textContent,
            hasError: !!error,
            timestamp: Date.now()
        });
    }, [isLoading, content, textContent, error, projectId, fileId, version]);

    // Log Electron environment details on component mount
    React.useEffect(() => {
        console.log('[TELEMETRY] FileContents environment check', {
            isElectron: typeof window !== 'undefined' && (window as any).electron !== undefined,
            userAgent: typeof navigator !== 'undefined' ? navigator.userAgent : 'unknown',
            contextIsolation: typeof process !== 'undefined' ? (process as any).contextIsolated : 'unknown',
            nodeIntegration: typeof process !== 'undefined' && process.versions?.electron ? 'enabled' : 'disabled',
            timestamp: Date.now()
        });
    }, []);

    useEffect(() => {
        const fetchContent = async () => {
            const startTime = Date.now();
            const fileIdKey = `${projectId}:${fileId}:${version || 'latest'}`;
            
            console.log('[TELEMETRY] ContentFile load started', { 
                fileIdKey, 
                projectId, 
                fileId,
                version,
                contentType,
                timestamp: startTime 
            });

            try {
                setIsLoading(true);
                setError(null);
                setTextContent(null);
                
                console.log('[TELEMETRY] ContentFile API call initiated', { 
                    fileIdKey,
                    timestamp: Date.now() 
                });
                
                const fileContent = await api.projects.getContentFileContent(projectId, fileId, version);
                
                console.log('[TELEMETRY] ContentFile API call completed', { 
                    fileIdKey,
                    success: true, 
                    blobSize: fileContent?.blob?.size,
                    blobType: fileContent?.blob?.type,
                    fileName: fileContent?.fileName,
                    contentType: fileContent?.contentType,
                    loadTime: Date.now() - startTime,
                    timestamp: Date.now() 
                });
                
                setContent(fileContent);
                
                console.log('[TELEMETRY] ContentFile load completed successfully', { 
                    fileIdKey,
                    totalTime: Date.now() - startTime,
                    timestamp: Date.now() 
                });
                
            } catch (error) {
                console.log('[TELEMETRY] ContentFile load failed', { 
                    fileIdKey,
                    error: error instanceof Error ? error.message : 'Unknown error',
                    errorType: error instanceof Error ? error.constructor.name : 'Unknown',
                    loadTime: Date.now() - startTime,
                    timestamp: Date.now() 
                });
                
                console.error('Failed to fetch file content:', error);
                setError('Failed to load file content. Please try again.');
            } finally {
                console.log('[TELEMETRY] ContentFile loading state cleared', { 
                    fileIdKey,
                    timestamp: Date.now() 
                });
                
                setIsLoading(false);
            }
        };

        fetchContent();
    }, [projectId, fileId, version]);

    useEffect(() => {
        const loadTextContent = async () => {
            if (content?.blob && (contentType.startsWith('text/') || contentType === 'application/json')) {
                const startTime = Date.now();
                const fileIdKey = `${projectId}:${fileId}:${version || 'latest'}`;
                
                console.log('[TELEMETRY] Text processing started', { 
                    fileIdKey,
                    contentType,
                    blobSize: content.blob.size,
                    timestamp: startTime 
                });
                
                try {
                    const text = await content.blob.text();
                    
                    console.log('[TELEMETRY] Text processing completed', { 
                        fileIdKey,
                        textLength: text?.length,
                        processingTime: Date.now() - startTime,
                        timestamp: Date.now() 
                    });
                    
                    setTextContent(text);
                } catch (error) {
                    console.log('[TELEMETRY] Text processing failed', { 
                        fileIdKey,
                        error: error instanceof Error ? error.message : 'Unknown error',
                        errorType: error instanceof Error ? error.constructor.name : 'Unknown',
                        processingTime: Date.now() - startTime,
                        timestamp: Date.now() 
                    });
                    
                    console.error('Failed to read text content:', error);
                    setError('Failed to read text content. Please try again.');
                }
            }
        };

        loadTextContent();
    }, [content, contentType, projectId, fileId, version]);

    // Resolve HTML resources (images, stylesheets, scripts, etc.)
    useEffect(() => {
        if (contentType !== 'text/html' && contentType !== 'application/xhtml+xml') {
            setHtmlContent(null);
            return;
        }
        
        if (!textContent) {
            setHtmlContent(null);
            return;
        }
        
        // For project files, we need resolveProjectFilePath to resolve relative paths
        // Extract basePath from file path if available (fileName may contain path)
        const basePath = content?.fileName?.includes('/') 
            ? content.fileName.substring(0, content.fileName.lastIndexOf('/'))
            : undefined;
        
        setIsLoadingHtmlResources(true);
        
        resolveHtmlResources({
            html: textContent,
            projectId,
            basePath,
            resolveProjectFilePath,
        }).then((result) => {
            // Clean up old blob URLs before storing new ones
            cleanupBlobUrls(htmlResourceBlobUrlsRef.current);
            htmlResourceBlobUrlsRef.current = result.blobUrls;
            setHtmlContent(result.html);
            setIsLoadingHtmlResources(false);
        }).catch((error) => {
            console.error('Failed to resolve HTML resources:', error);
            // Fall back to original content
            setHtmlContent(textContent);
            setIsLoadingHtmlResources(false);
        });
    }, [textContent, contentType, projectId, content?.fileName, resolveProjectFilePath]);

    // Cleanup HTML resource blob URLs on unmount
    useEffect(() => {
        return () => {
            cleanupBlobUrls(htmlResourceBlobUrlsRef.current);
        };
    }, []);

    // Always call hooks at the top level - make the logic conditional, not the hook calls
    const objectUrl = useMemo(() => {
        if (content?.blob && contentType.startsWith('image/')) {
            return URL.createObjectURL(content.blob);
        }
        return null;
    }, [content?.blob, contentType]);

    useEffect(() => {
        // Cleanup object URL when component unmounts or URL changes
        return () => {
            if (objectUrl) {
                URL.revokeObjectURL(objectUrl);
            }
        };
    }, [objectUrl]);

    const isMarkdown =
        contentType === 'text/markdown' ||
        contentType === 'text/x-markdown' ||
        (content?.fileName && content.fileName.toLowerCase().endsWith('.md'));

    if (isLoading) {
        return (
            <div className="h-full w-full flex items-center justify-center">
                <LoadingSpinner message="Loading file content..." />
            </div>
        );
    }

    if (error || !content) {
        return (
            <div className="h-full w-full flex items-center justify-center text-red-600">
                {error || 'Failed to load file content'}
            </div>
        );
    }

    // Handle Markdown with Mermaid support
    if (isMarkdown) {
        if (textContent === null) {
            return (
                <div className="h-full w-full flex items-center justify-center">
                    <LoadingSpinner message="Processing markdown..." />
                </div>
            );
        }

        if (inlineMode) {
            return (
                <MarkdownViewer text={textContent} className="h-full p-4" inlineMode={true} projectId={projectId} notebookId={undefined} resolveProjectFilePath={resolveProjectFilePath} />
            );
        }
        return (
            <>
                <PreviewContainer
                    contentClassName="h-full p-4"
                    hideHeader={false}
                    showHeaderActionsOnlyWhenFull
                    headerActions={
                        onEditMarkdown ? (
                            <button
                                onClick={onEditMarkdown}
                                className="p-2 rounded hover:bg-gray-200 text-gray-600 hover:text-gray-800"
                                aria-label="Edit markdown"
                                title="Edit"
                            >
                                <FaRegEdit className="w-4 h-4" />
                            </button>
                        ) : null
                    }
                >
                    <MarkdownViewer text={textContent} className="" inlineMode={true} projectId={projectId} notebookId={undefined} resolveProjectFilePath={resolveProjectFilePath} />
                </PreviewContainer>
                {/* Edit functionality is owned by ContentFileContent; viewer is presentational only */}
            </>
        );
    }

    // Handle HTML files - render in iframe with resolved resources
    if (contentType === 'text/html' || contentType === 'application/xhtml+xml') {
        if (textContent === null) {
            return (
                <div className="h-full w-full flex items-center justify-center">
                    <LoadingSpinner message="Processing HTML content..." />
                </div>
            );
        }
        if (isLoadingHtmlResources || !htmlContent) {
            return (
                <div className="h-full w-full flex items-center justify-center">
                    <LoadingSpinner message="Loading resources..." />
                </div>
            );
        }
        return (
            <div className="h-full w-full overflow-hidden bg-gray-100 flex items-center justify-center">
                <iframe
                    srcDoc={htmlContent}
                    className="w-full h-full border-0"
                    title="HTML Preview"
                    sandbox="allow-same-origin allow-scripts allow-popups allow-modals"
                />
            </div>
        );
    }

    // Handle text files (including code and JSON)
    if (contentType.startsWith('text/') || contentType === 'application/json') {
        if (textContent === null) {
            return (
                <div className="h-full w-full flex items-center justify-center">
                    <LoadingSpinner message="Processing text content..." />
                </div>
            );
        }
        return <TextViewer text={textContent} inlineMode={inlineMode} />;
    }

    // Handle images
    if (contentType.startsWith('image/') && objectUrl) {
        return <ImageViewer src={objectUrl} alt={content.fileName} inlineMode={inlineMode} />;
    }

    // Handle PDFs
    if (contentType.startsWith('application/pdf')) {
        return <PdfViewer blob={content.blob} inlineMode={inlineMode} />;
    }

    // Handle audio files
    if (contentType.startsWith('audio/')) {
        return <AudioPlayer blob={content.blob} inlineMode={inlineMode} />;
    }

    // Handle video files
    if (contentType.startsWith('video/')) {
        return <VideoPlayer blob={content.blob} inlineMode={inlineMode} />;
    }

    // Fallback for unsupported file types
    return (
        <div className="h-full w-full flex items-center justify-center">
            <div className="text-center p-4">
                <p className="text-gray-600">
                    Preview not available for this file type ({contentType})
                </p>
                <p className="text-sm text-gray-500 mt-2">
                    You can download the file to view its contents
                </p>
            </div>
        </div>
    );
}

// Wrapped component with error boundary
const FileContentsWithErrorBoundary = React.memo((props: FileContentsProps) => {
    const fileIdKey = `${props.projectId}:${props.fileId}:${props.version || 'latest'}`;
    
    return (
        <FileLoadingErrorBoundary 
            fileId={fileIdKey}
            componentName="FileContents"
        >
            <FileContentsComponent {...props} />
        </FileLoadingErrorBoundary>
    );
});

export const FileContents = FileContentsWithErrorBoundary;
