import React, { useState, useEffect } from 'react';
import { ProjectContentFile, ContentFileDto } from '../../../types/project';
import { api } from '../../../services/api';
import LoadingSpinner from '../../LoadingSpinner';
import { FileContents } from './FileContents';
import { HistoryDrawer } from '../history/HistoryDrawer';
import { FileInUseDialog } from '../../common/FileInUseDialog';
import { FileUsageInNotebookDto, ContentFileMarkdownShadowDto, MarkdownExtractionStatus } from '../../../types/api';
import { ConfirmationDialog } from '../../common/ConfirmationDialog';
import MarkdownViewer from '../../common/MarkdownViewer';
import { isMarkdownExtractionSupported } from '../../../utils/markdownUtils';
import FullScreenEditor from '../../notebook/conversations/FullScreenEditor';
import { FaRegEdit, FaDownload } from 'react-icons/fa';

interface ContentFileContentProps {
    hideHeader?: boolean;
    fileId: string;
    projectId: string;
    canEdit?: boolean;
    onDelete?: () => Promise<void>;
    resolveProjectFilePath?: (relativePath: string) => string | undefined;
}

const convertToProjectContentFile = (dto: ContentFileDto): ProjectContentFile => ({
    ...dto,
    path: dto.relativePath, // Use relative path instead of server path for security
    // index flag removed from client concerns
    documentId: dto.documentId ?? '',
    created: dto.created ?? new Date().toISOString(),
    fileSize: dto.fileSize ?? 0,
    latestVersion: (dto as any).latestVersion ?? undefined,
});

function ContentFileContentComponent({ fileId, projectId, hideHeader = false, canEdit = false, onDelete, resolveProjectFilePath }: ContentFileContentProps) {
    const [file, setFile] = useState<ProjectContentFile | null>(null);
    const [isLoading, setIsLoading] = useState(true);
    const [isDeleting, setIsDeleting] = useState(false);
    const [isDownloading, setIsDownloading] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [showHistory, setShowHistory] = useState(false);
    const [showFileInUseDialog, setShowFileInUseDialog] = useState(false);
    const [notebooksUsingFile, setNotebooksUsingFile] = useState<FileUsageInNotebookDto[]>([]);

    // Confirmation dialog state
    const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);

    // Tab and markdown state
    const [activeTab, setActiveTab] = useState<'original' | 'markdown'>('original');
    const [markdownShadow, setMarkdownShadow] = useState<ContentFileMarkdownShadowDto | null>(null);
    const [markdownContent, setMarkdownContent] = useState<string | null>(null);
    const [isLoadingMarkdown, setIsLoadingMarkdown] = useState(false);
    const [markdownError, setMarkdownError] = useState<string | null>(null);
    const [hasAutoSwitched, setHasAutoSwitched] = useState(false);

    // Markdown edit state (full-screen editor)
    const [isEditingMd, setIsEditingMd] = useState(false);
    const [mdEditorContent, setMdEditorContent] = useState<string>('');
    const [mdEditorLoading, setMdEditorLoading] = useState<boolean>(false);
    const [mdEditorError, setMdEditorError] = useState<string | undefined>(undefined);

    const fetchFile = async () => {
        try {
            setIsLoading(true);
            setError(null);
            const fileDetails = await api.projects.getContentFile(projectId, fileId);
            setFile(convertToProjectContentFile(fileDetails));
        } catch (error) {
            console.error('Failed to fetch file details:', error);
            setError('Failed to load file details. Please try again.');
        } finally {
            setIsLoading(false);
        }
    };

    useEffect(() => {
        fetchFile();
        
        // Reset markdown state when switching files
        setActiveTab('original');
        setMarkdownShadow(null);
        setMarkdownContent(null);
        setIsLoadingMarkdown(false);
        setMarkdownError(null);
        setHasAutoSwitched(false);
    }, [projectId, fileId]);

    // Listen for version updates from sidebar saves
    useEffect(() => {
        const handler = (e: Event) => {
            const ce = e as CustomEvent<{ fileId: string; newVersion: number }>;
            if (ce.detail?.fileId === fileId) {
                // Update file state with new version immediately
                setFile(prev => prev ? { ...prev, latestVersion: ce.detail.newVersion } : null);
            }
        };
        window.addEventListener('file-version-updated', handler as EventListener);
        return () => window.removeEventListener('file-version-updated', handler as EventListener);
    }, [fileId]);

    // Check if file supports markdown extraction and fetch shadow info
    const supportsMarkdownExtraction = file ? isMarkdownExtractionSupported(file.fileName, file.contentType) : false;

    useEffect(() => {
        const fetchMarkdownShadow = async () => {
            if (!file || !supportsMarkdownExtraction) {
                setMarkdownShadow(null);
                return;
            }

            try {
                const shadow = await api.projects.getContentFileMarkdownShadow(projectId, fileId, file.latestVersion);
                setMarkdownShadow(shadow);
            } catch (error) {
                console.error('Failed to fetch markdown shadow:', error);
                setMarkdownShadow(null);
            }
        };

        fetchMarkdownShadow();
    }, [file, projectId, fileId, supportsMarkdownExtraction]);

    // Poll for status updates when extraction is in progress
    useEffect(() => {
        if (!markdownShadow || !file || !supportsMarkdownExtraction) {
            return;
        }

        // Only poll if status is Pending or Processing
        if (markdownShadow.status !== MarkdownExtractionStatus.Pending && 
            markdownShadow.status !== MarkdownExtractionStatus.Processing) {
            return;
        }

        const pollInterval = setInterval(async () => {
            try {
                const shadow = await api.projects.getContentFileMarkdownShadow(projectId, fileId, file.latestVersion);
                setMarkdownShadow(shadow);

                // Stop polling if extraction is complete or failed
                if (shadow.status === MarkdownExtractionStatus.Completed || 
                    shadow.status === MarkdownExtractionStatus.Failed || 
                    shadow.status === MarkdownExtractionStatus.Skipped) {
                    clearInterval(pollInterval);
                }
            } catch (error) {
                console.error('Failed to poll markdown shadow status:', error);
                // Don't clear the interval on error, just log it
            }
        }, 3000); // Poll every 3 seconds

        // Cleanup function
        return () => {
            clearInterval(pollInterval);
        };
    }, [markdownShadow?.status, file, projectId, fileId, supportsMarkdownExtraction]);

    const fetchMarkdownContent = async () => {
        if (!file || !markdownShadow || markdownShadow.status !== MarkdownExtractionStatus.Completed) {
            return;
        }

        const currentFileId = fileId; // Capture current fileId to check if it changed during async operation

        try {
            setIsLoadingMarkdown(true);
            setMarkdownError(null);
            
            const result = await api.projects.getContentFileMarkdownContent(projectId, fileId, file.latestVersion);
            const text = await result.blob.text();
            
            // Only set content if we're still viewing the same file
            if (currentFileId === fileId) {
                setMarkdownContent(text);
            }
        } catch (error) {
            console.error('Failed to fetch markdown content:', error);
            // Only set error if we're still viewing the same file
            if (currentFileId === fileId) {
                setMarkdownError('Failed to load markdown content. Please try again.');
            }
        } finally {
            // Only update loading state if we're still viewing the same file
            if (currentFileId === fileId) {
                setIsLoadingMarkdown(false);
            }
        }
    };

    // Auto-switch to markdown tab for non-previewable files with completed extraction
    useEffect(() => {
        if (!file || !markdownShadow || markdownShadow.status !== MarkdownExtractionStatus.Completed || hasAutoSwitched) {
            return;
        }

        // Check if the original file type can't be previewed
        const isPreviewable = 
            file.contentType.startsWith('text/') ||
            file.contentType === 'application/json' ||
            file.contentType.startsWith('image/') ||
            file.contentType.startsWith('application/pdf') ||
            file.contentType.startsWith('audio/') ||
            file.contentType.startsWith('video/') ||
            file.contentType === 'text/markdown' ||
            file.contentType === 'text/x-markdown' ||
            (file.fileName && file.fileName.toLowerCase().endsWith('.md'));

        // If original content isn't previewable but markdown is ready, switch to markdown tab (only once)
        if (!isPreviewable && activeTab === 'original') {
            setActiveTab('markdown');
            setHasAutoSwitched(true);
        }
    }, [file, markdownShadow, activeTab, hasAutoSwitched]);

    // Fetch markdown content when switching to markdown tab
    useEffect(() => {
        if (activeTab === 'markdown' && markdownShadow && markdownShadow.status === MarkdownExtractionStatus.Completed && !markdownContent) {
            fetchMarkdownContent();
        }
    }, [activeTab, markdownShadow, markdownContent]);

    const handleDelete = async () => {
        if (!canEdit || isDeleting || !onDelete) return;
        setShowDeleteConfirm(true);
    };

    const handleDeleteConfirm = async () => {
        if (!canEdit || isDeleting || !onDelete) return;

        try {
            setIsDeleting(true);
            setError(null);
            await api.projects.deleteContentFile(projectId, fileId);
            await onDelete();
        } catch (error: any) {
            // Check if this is a file-in-use error
            if (error.isFileInUse && error.notebooksUsingFile) {
                setNotebooksUsingFile(error.notebooksUsingFile);
                setShowFileInUseDialog(true);
                setError(null); // Clear the generic error since we're showing the dialog
            } else {
                console.error('Failed to delete file:', error);
                setError('Failed to delete file. Please try again.');
            }
        } finally {
            setIsDeleting(false);
            setShowDeleteConfirm(false);
        }
    };

    const handleDeleteCancel = () => {
        setShowDeleteConfirm(false);
    };

    const handleDownload = async () => {
        if (isDownloading) return;
        try {
            setIsDownloading(true);
            setError(null);

            // Fetch the file content (blob) and filename from the API
            const result = await api.projects.getContentFileContent(projectId, fileId, file?.latestVersion);

            const url = URL.createObjectURL(result.blob);
            const anchor = document.createElement('a');
            anchor.href = url;
            const bestName = result.fileName && result.fileName.toLowerCase() !== 'unknown'
                ? result.fileName
                : file?.fileName || 'download';
            anchor.download = bestName;
            document.body.appendChild(anchor);
            anchor.click();
            anchor.remove();

            // Revoke object URL after a short delay to ensure the download starts
            setTimeout(() => URL.revokeObjectURL(url), 1000);
        } catch (error) {
            console.error('Failed to download file:', error);
            setError('Failed to download file. Please try again.');
        } finally {
            setIsDownloading(false);
        }
    };

    const isMarkdown = file?.contentType === 'text/markdown' || file?.contentType === 'text/x-markdown' || (file?.fileName?.toLowerCase()?.endsWith('.md'));

    const openMarkdownEditor = async () => {
        if (!file || !isMarkdown) return;
        setMdEditorError(undefined);
        setMdEditorLoading(true);
        try {
            // Prefer markdown content endpoint if available
            try {
                const result = await api.projects.getContentFileMarkdownContent(projectId, fileId, file.latestVersion);
                const text = await result.blob.text();
                setMdEditorContent(text);
            } catch {
                const result = await api.projects.getContentFileContent(projectId, fileId, file.latestVersion);
                const text = await result.blob.text();
                setMdEditorContent(text);
            }
            setIsEditingMd(true);
        } catch (err) {
            setMdEditorError('Failed to load markdown content.');
        } finally {
            setMdEditorLoading(false);
        }
    };

    const saveMarkdownEditor = async (newContent: string) => {
        if (!file) return;
        setMdEditorLoading(true);
        setMdEditorError(undefined);
        try {
            const folderId = file.folderId || undefined;
            const mdFile = new File([newContent], file.fileName, { type: 'text/markdown' });
            const uploadResults = await api.projects.uploadFiles(projectId, [mdFile], folderId);
            // Update local view immediately
            setMarkdownContent(newContent);
            // Update latestVersion immediately from upload response to avoid stale cache reads
            const newVersion = uploadResults[0]?.latestVersion;
            if (newVersion !== undefined) {
                setFile(prev => prev ? { ...prev, latestVersion: newVersion } : null);
            }
            setIsEditingMd(false);
            // Refresh file metadata to pick up new latestVersion and details
            await fetchFile();
        } catch (err) {
            setMdEditorError('Failed to save markdown file.');
        } finally {
            setMdEditorLoading(false);
        }
    };

    if (isLoading) {
        return (
            <div className="h-full flex items-center justify-center">
                <LoadingSpinner message="Loading file details..." />
            </div>
        );
    }

    if (!file) {
        return (
            <div className="h-full flex items-center justify-center text-red-600">
                {error || 'File not found'}
            </div>
        );
    }

    // Minimal home-page view without header
    if (hideHeader) {
        const isHtml = file.contentType === 'text/html' || file.contentType === 'application/xhtml+xml';
        const isPreviewable = file.contentType.startsWith('text/') ||
            file.contentType === 'application/json' ||
            file.contentType.startsWith('image/') ||
            file.contentType.startsWith('application/pdf') ||
            file.contentType.startsWith('audio/') ||
            file.contentType.startsWith('video/');

        if (isPreviewable) {
            // HTML files get full-bleed rendering without padding
            if (isHtml) {
                return (
                    <div className="h-full w-full overflow-hidden flex flex-col">
                        <FileContents
                            inlineMode
                            projectId={projectId}
                            fileId={fileId}
                            contentType={file.contentType}
                            version={file.latestVersion}
                            resolveProjectFilePath={resolveProjectFilePath}
                        />
                    </div>
                );
            }
            return (
                <div className="h-full w-full overflow-auto flex flex-col p-8">
                    <FileContents
                        inlineMode
                        projectId={projectId}
                        fileId={fileId}
                        contentType={file.contentType}
                        version={file.latestVersion}
                                resolveProjectFilePath={resolveProjectFilePath}
                                onEditMarkdown={isMarkdown ? openMarkdownEditor : undefined}
                    />
                </div>
            );
        }

        if (supportsMarkdownExtraction && markdownShadow?.status === MarkdownExtractionStatus.Completed && markdownContent) {
            return (
                <div className="h-full w-full overflow-auto flex flex-col">
                    <MarkdownViewer text={markdownContent} className="h-full p-4" projectId={projectId} notebookId={undefined} resolveProjectFilePath={resolveProjectFilePath} />
                </div>
            );
        }

        return (
            <div className="h-full w-full flex items-center justify-center">
                <div className="text-center p-4">
                    <p className="text-gray-600">
                        Preview not available for this file type ({file.contentType})
                    </p>
                    <p className="text-sm text-gray-500 mt-2">
                        You can download the file to view its contents
                    </p>
                </div>
            </div>
        );
    }

    return (
        <>
        <div className="h-full w-full overflow-auto flex flex-col">
            {!hideHeader && (
            <div className="flex-none flex items-center justify-between mb-4">
                <h2 className="text-xl font-bold">{file.fileName}</h2>
                <div className="flex gap-2">
                    {isMarkdown && (
                        <button
                            className="px-4 py-2 text-sm border rounded hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-blue-600 focus:ring-offset-2 flex items-center gap-2"
                            onClick={openMarkdownEditor}
                            aria-label="Edit markdown"
                            title="Edit"
                        >
                            <FaRegEdit className="w-4 h-4" />
                            <span>Edit</span>
                        </button>
                    )}
                    <button
                        className={
                            `px-4 py-2 text-sm border rounded hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-blue-600 focus:ring-offset-2 flex items-center gap-2 ` +
                            `${isDownloading ? 'disabled:opacity-50 disabled:cursor-not-allowed' : ''}`
                        }
                        onClick={handleDownload}
                        disabled={isDownloading}
                    >
                        {isDownloading ? 'Downloading...' : (<><FaDownload className="w-4 h-4" /><span>Download</span></>)}
                    </button>
                    {canEdit && (
                        <button
                            className="px-4 py-2 text-sm text-red-600 border border-red-600 rounded hover:bg-red-50 focus:outline-none focus:ring-2 focus:ring-red-600 focus:ring-offset-2"
                            onClick={handleDelete}
                            disabled={isDeleting}
                        >
                            {isDeleting ? 'Deleting...' : 'Delete'}
                        </button>
                    )}
                    <button
                        className="px-4 py-2 text-sm border rounded hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-blue-600 focus:ring-offset-2"
                        onClick={() => setShowHistory(true)}
                    >
                        History
                    </button>
                </div>
            </div>
            )}
            {error && <p className="text-red-600 text-sm mb-4">{error}</p>}
            {!hideHeader && (
            <div className="flex-none mb-4">
                <div className="space-y-2">
                    <p className="text-gray-600">Path: {file.relativePath}</p>
                    <p className="text-gray-600">Type: {file.contentType}</p>
                    <p className="text-gray-600">Size: {Math.round(file.fileSize / 1024)} KB</p>
                    <p className="text-gray-600">Created: {new Date(file.created).toLocaleString()}</p>
                </div>
            </div>
            )}
            <div className="flex-1 min-h-0 border rounded p-4 flex flex-col">
                {supportsMarkdownExtraction ? (
                    <>
                        {/* Tab Header */}
                        <div className="flex space-x-1 mb-4 border-b">
                            <button
                                className={`px-4 py-2 text-sm font-medium rounded-t-md transition-colors ${
                                    activeTab === 'original'
                                        ? 'bg-blue-600 text-white border-b-2 border-blue-600'
                                        : 'text-gray-600 hover:text-gray-800 hover:bg-gray-50'
                                }`}
                                onClick={() => setActiveTab('original')}
                            >
                                Original Content
                            </button>
                            <button
                                className={`px-4 py-2 text-sm font-medium rounded-t-md transition-colors relative ${
                                    activeTab === 'markdown'
                                        ? 'bg-blue-600 text-white border-b-2 border-blue-600'
                                        : 'text-gray-600 hover:text-gray-800 hover:bg-gray-50'
                                }`}
                                onClick={() => setActiveTab('markdown')}
                                disabled={!markdownShadow || markdownShadow.status !== MarkdownExtractionStatus.Completed}
                            >
                                Extracted Text
                                {markdownShadow && (
                                    <span className={`ml-2 inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${
                                        markdownShadow.status === MarkdownExtractionStatus.Completed
                                            ? 'bg-green-100 text-green-800'
                                            : markdownShadow.status === MarkdownExtractionStatus.Processing
                                            ? 'bg-yellow-100 text-yellow-800'
                                            : markdownShadow.status === MarkdownExtractionStatus.Failed
                                            ? 'bg-red-100 text-red-800'
                                            : 'bg-gray-100 text-gray-800'
                                    }`}>
                                        {markdownShadow.status}
                                    </span>
                                )}
                            </button>
                        </div>

                        {/* Tab Content */}
                        <div className="flex-1 min-h-0 bg-gray-50 rounded flex">
                            {activeTab === 'original' ? (
                                <FileContents
                                    projectId={projectId}
                                    fileId={fileId}
                                    contentType={file.contentType}
                                    version={file.latestVersion}
                                    resolveProjectFilePath={resolveProjectFilePath}
                                    onEditMarkdown={isMarkdown ? openMarkdownEditor : undefined}
                                />
                            ) : (
                                <div className="w-full h-full flex flex-col p-4">
                                    {!markdownShadow ? (
                                        <div className="flex items-center justify-center h-full text-gray-500">
                                            No extracted text available
                                        </div>
                                    ) : markdownShadow.status === MarkdownExtractionStatus.Pending ? (
                                        <div className="flex items-center justify-center h-full text-gray-500">
                                            <LoadingSpinner message="Text extraction is pending..." />
                                        </div>
                                    ) : markdownShadow.status === MarkdownExtractionStatus.Processing ? (
                                        <div className="flex items-center justify-center h-full text-gray-500">
                                            <LoadingSpinner message="Extracting text content..." />
                                        </div>
                                    ) : markdownShadow.status === MarkdownExtractionStatus.Failed ? (
                                        <div className="flex items-center justify-center h-full text-red-600">
                                            <div className="text-center">
                                                <p>Text extraction failed</p>
                                                {markdownShadow.errorMessage && (
                                                    <p className="text-sm mt-2">{markdownShadow.errorMessage}</p>
                                                )}
                                            </div>
                                        </div>
                                    ) : markdownShadow.status === MarkdownExtractionStatus.Skipped ? (
                                        <div className="flex items-center justify-center h-full text-gray-500">
                                            Text extraction was skipped for this file type
                                        </div>
                                    ) : isLoadingMarkdown ? (
                                        <div className="flex items-center justify-center h-full">
                                            <LoadingSpinner message="Loading text content..." />
                                        </div>
                                    ) : markdownError ? (
                                        <div className="flex items-center justify-center h-full text-red-600">
                                            <div className="text-center">
                                                <p>{markdownError}</p>
                                                <button
                                                    className="mt-2 px-4 py-2 text-sm bg-blue-600 text-white rounded hover:bg-blue-700"
                                                    onClick={fetchMarkdownContent}
                                                >
                                                    Retry
                                                </button>
                                            </div>
                                        </div>
                                                                                                              ) : markdownContent ? (
                                         <div className="flex-1 min-h-0 overflow-auto">
                                             <MarkdownViewer text={markdownContent} className="w-full p-4" projectId={projectId} notebookId={undefined} resolveProjectFilePath={resolveProjectFilePath} />
                                         </div>
                                     ) : (
                                        <div className="flex items-center justify-center h-full text-gray-500">
                                            No text content available
                                        </div>
                                    )}
                                </div>
                            )}
                        </div>
                    </>
                ) : (
                    <>
                        <h3 className="font-medium mb-2 flex-none">File Contents</h3>
                        <div className="flex-1 min-h-0 bg-gray-50 rounded flex">
                            <FileContents
                                projectId={projectId}
                                fileId={fileId}
                                contentType={file.contentType}
                                version={file.latestVersion}
                                resolveProjectFilePath={resolveProjectFilePath}
                                onEditMarkdown={isMarkdown ? openMarkdownEditor : undefined}
                            />
                        </div>
                    </>
                )}
            </div>
            <HistoryDrawer
                projectId={projectId}
                fileId={fileId}
                isOpen={showHistory}
                refreshKey={file.latestVersion}
                onClose={() => setShowHistory(false)}
            />
            <FileInUseDialog
                isOpen={showFileInUseDialog}
                onClose={() => setShowFileInUseDialog(false)}
                fileName={file?.fileName || 'Unknown'}
                notebooksUsingFile={notebooksUsingFile}
                projectId={projectId}
            />
            <ConfirmationDialog
                isOpen={showDeleteConfirm}
                onClose={handleDeleteCancel}
                onConfirm={handleDeleteConfirm}
                title="Confirm Deletion"
                message={`Are you sure you want to delete the file "${file?.fileName}"? This action cannot be undone.`}
                confirmText="Delete"
                cancelText="Cancel"
            />
        </div>
        {isEditingMd && (
            <FullScreenEditor
                content={mdEditorContent}
                onSave={saveMarkdownEditor}
                onCancel={() => setIsEditingMd(false)}
                mode={'edit'}
                title={`Edit File: ${file.fileName}`}
                placeholder={file.fileName}
                submitLabel={'Save'}
                isLoading={mdEditorLoading}
                error={mdEditorError}
                projectId={projectId}
                notebookId={undefined}
                resolveProjectFilePath={resolveProjectFilePath}
            />
        )}
        </>
    );
}

// Memoize to prevent unnecessary re-renders during sidebar polling
export const ContentFileContent = React.memo(ContentFileContentComponent, (prevProps, nextProps) => {
    return (
        prevProps.fileId === nextProps.fileId &&
        prevProps.projectId === nextProps.projectId &&
        prevProps.hideHeader === nextProps.hideHeader &&
        prevProps.canEdit === nextProps.canEdit
        // onDelete callback identity is ignored since it's stable now
    );
}); 