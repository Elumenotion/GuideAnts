/// <reference types="../../../types/electron" />
import { useState, useEffect } from 'react';
import { ProjectLink } from '../../../types/project';
import { ConfirmationDialog } from '../../common/ConfirmationDialog';

interface LinkContentProps {
    link: ProjectLink;
    onUpdate?: (linkId: string, updates: Partial<ProjectLink>) => Promise<void>;
    onDelete?: (linkId: string) => Promise<void>;
    isEditing?: boolean;
    onStartEdit?: () => void;
    onCancelEdit?: () => void;
    canEdit?: boolean;
}

function isValidUrl(urlString: string): boolean {
    try {
        const url = new URL(urlString);
        return url.protocol === 'http:' || url.protocol === 'https:';
    } catch {
        return false;
    }
}

export function LinkContent({ 
    link, 
    onUpdate, 
    onDelete,
    isEditing = false,
    onStartEdit,
    onCancelEdit,
    canEdit = false
}: LinkContentProps) {
    const [editedUrl, setEditedUrl] = useState(link.url);
    const [isSaving, setIsSaving] = useState(false);
    const [isDeleting, setIsDeleting] = useState(false);
    const [error, setError] = useState<string | null>(null);

    // Confirmation dialog state
    const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);

    // Reset edited URL when link changes
    useEffect(() => {
        setEditedUrl(link.url);
    }, [link.url]);

    const handleUrlChange = (newUrl: string) => {
        setEditedUrl(newUrl);
        setError(null);
    };

    const handleSave = async () => {
        if (!onUpdate) return;
        
        if (!isValidUrl(editedUrl)) {
            setError('Please enter a valid URL starting with http:// or https://');
            return;
        }

        setError(null);
        setIsSaving(true);
        try {
            await onUpdate(link.id, { url: editedUrl });
        } catch (error) {
            console.error('Failed to update link:', error);
            setError('Failed to update link. Please try again.');
        } finally {
            setIsSaving(false);
        }
    };

    const handleOpenLink = async (url: string) => {
        if (!isValidUrl(url)) {
            setError('Invalid URL. Cannot open link.');
            return;
        }

        try {
            // @ts-ignore - Electron API is injected at runtime
            if (window.electron?.openExternal) {
                // @ts-ignore - Electron API is injected at runtime
                await window.electron.openExternal(url);
            } else {
                window.open(url, '_blank', 'noopener,noreferrer');
            }
        } catch (error) {
            console.error('Failed to open link:', error);
            setError('Failed to open link. Please try again.');
        }
    };

    const handleDelete = async () => {
        if (!onDelete) return;
        setShowDeleteConfirm(true);
    };

    const handleDeleteConfirm = async () => {
        if (!onDelete) return;

        setIsDeleting(true);
        try {
            await onDelete(link.id);
        } catch (error) {
            console.error('Failed to delete link:', error);
            setError('Failed to delete link. Please try again.');
        } finally {
            setIsDeleting(false);
            setShowDeleteConfirm(false);
        }
    };

    const handleDeleteCancel = () => {
        setShowDeleteConfirm(false);
    };

    return (
        <div className="h-full flex flex-col">
            <div className="border-b pb-4 mb-4">
                <div className="flex items-center justify-between mb-4">
                    <h2 className="text-xl font-semibold">Link Details</h2>
                    <div className="flex gap-2">
                        {isEditing ? (
                            <>
                                <button
                                    onClick={handleSave}
                                    disabled={isSaving}
                                    className="px-4 py-2 bg-blue-600 text-white rounded hover:bg-blue-700 disabled:opacity-50"
                                >
                                    {isSaving ? 'Saving...' : 'Save'}
                                </button>
                                <button
                                    onClick={() => {
                                        onCancelEdit?.();
                                        setEditedUrl(link.url);
                                        setError(null);
                                    }}
                                    disabled={isSaving}
                                    className="px-4 py-2 border rounded hover:bg-gray-50 disabled:opacity-50"
                                >
                                    Cancel
                                </button>
                            </>
                        ) : canEdit ? (
                            <>
                                <button
                                    onClick={onStartEdit}
                                    className="px-4 py-2 border rounded hover:bg-gray-50"
                                >
                                    Edit Link
                                </button>
                                <button
                                    onClick={handleDelete}
                                    disabled={isDeleting}
                                    className="px-4 py-2 border rounded bg-red-50 text-red-600 hover:bg-red-100 disabled:opacity-50"
                                >
                                    {isDeleting ? 'Deleting...' : 'Delete'}
                                </button>
                            </>
                        ) : null}
                    </div>
                </div>

                <div className="space-y-4">
                    <div>
                        <label className="block text-sm font-medium text-gray-700 mb-1">
                            URL
                        </label>
                        {isEditing ? (
                            <div className="space-y-2">
                                <input
                                    type="url"
                                    value={editedUrl}
                                    onChange={(e) => handleUrlChange(e.target.value)}
                                    className="w-full px-3 py-2 border rounded focus:outline-none focus:ring-2 focus:ring-blue-500"
                                    placeholder="Enter URL (e.g., https://example.com)"
                                />
                                {error && (
                                    <p className="text-sm text-red-600">{error}</p>
                                )}
                            </div>
                        ) : (
                            <div className="flex items-center gap-2">
                                <button
                                    onClick={() => handleOpenLink(link.url)}
                                    className="text-blue-600 hover:underline"
                                >
                                    {link.url}
                                </button>
                                <button
                                    onClick={() => navigator.clipboard.writeText(link.url)}
                                    className="px-2 py-1 text-sm text-gray-600 hover:bg-gray-100 rounded"
                                    title="Copy URL"
                                >
                                    Copy URL
                                </button>
                            </div>
                        )}
                    </div>
                </div>
            </div>

            <div className="flex-1 border rounded-lg overflow-hidden">
                <div className="w-full h-full flex flex-col items-center justify-center p-6 text-center text-gray-500 space-y-3">
                    {(isEditing ? isValidUrl(editedUrl) : isValidUrl(link.url)) ? (
                        <>
                            <p>Website previews are unavailable. Open the link in a browser to inspect it.</p>
                            <button
                                onClick={() => handleOpenLink(isEditing ? editedUrl : link.url)}
                                className="text-blue-600 hover:underline text-sm"
                            >
                                Open in new tab
                            </button>
                        </>
                    ) : (
                        <p>{editedUrl ? 'Enter a valid URL starting with http:// or https://.' : 'Enter a URL to open it in a browser.'}</p>
                    )}
                </div>
            </div>

            <ConfirmationDialog
                isOpen={showDeleteConfirm}
                onClose={handleDeleteCancel}
                onConfirm={handleDeleteConfirm}
                title="Confirm Deletion"
                message="Are you sure you want to delete this link? This action cannot be undone."
                confirmText="Delete"
                cancelText="Cancel"
            />
        </div>
    );
} 
