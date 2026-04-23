import { useState, useRef, useMemo, useEffect, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { FolderTreeDto } from '../../../types/project';

interface UploadFilesDialogProps {
    isOpen: boolean;
    onClose: () => void;
    onUpload: (files: File[], folderId?: string) => Promise<void>;
    disabled?: boolean;
    initialFolderId?: string;
    folderTree?: FolderTreeDto | null; // Real tree for destination picking
}

export const UploadFilesDialog: React.FC<UploadFilesDialogProps> = ({
    isOpen,
    onClose,
    onUpload,
    disabled = false,
    initialFolderId,
    folderTree
}) => {
    const [selectedFiles, setSelectedFiles] = useState<File[]>([]);
    const [isUploading, setIsUploading] = useState(false);
    const fileInputRef = useRef<HTMLInputElement>(null);
    const [selectedFolderId, setSelectedFolderId] = useState<string | undefined>(initialFolderId);

    // Compute a human-readable path for header
    const selectedFolderPath = useMemo(() => {
        const buildPath = (node: FolderTreeDto | null | undefined, id?: string): string | undefined => {
            if (!node) return undefined;
            if (!id) return '';
            if (node.id === id) return node.name || '';
            for (const sub of node.subFolders || []) {
                const p = buildPath(sub, id);
                if (p !== undefined) return node.name ? `${node.name}/${p}` : p;
            }
            return undefined;
        };
        const p = buildPath(folderTree || null, selectedFolderId);
        return p && p.trim().length > 0 ? p : undefined;
    }, [folderTree, selectedFolderId]);

    const renderFolderOptions = (
        node: FolderTreeDto,
        depth: number,
        currentId: string | undefined,
        setId: (id: string | undefined) => void,
        disabled: boolean
    ) => {
        const items: ReactNode[] = [];
        // Skip the synthetic root label row; its children are the real top-level folders
        if (depth > 0) {
            items.push(
                <button
                    key={node.id || node.relativePath}
                    type="button"
                    className={`block w-full text-left px-2 py-1 rounded ${currentId === node.id ? 'bg-blue-50 text-blue-700' : 'hover:bg-gray-50'}`}
                    style={{ paddingLeft: `${depth * 12}px` }}
                    onClick={() => setId(node.id)}
                    disabled={disabled || !node.id}
                    title={node.relativePath}
                >
                    {node.name}
                </button>
            );
        }
        for (const sub of node.subFolders || []) {
            items.push(...renderFolderOptions(sub, depth + 1, currentId, setId, disabled));
        }
        return items;
    };

    const handleFileSelect = (event: React.ChangeEvent<HTMLInputElement>) => {
        const files = Array.from(event.target.files || []);
        setSelectedFiles(files);
    };

    const handleDrop = (event: React.DragEvent<HTMLDivElement>) => {
        event.preventDefault();
        const files = Array.from(event.dataTransfer.files);
        setSelectedFiles(files);
    };

    const handleDragOver = (event: React.DragEvent<HTMLDivElement>) => {
        event.preventDefault();
    };

    const handleUpload = async () => {
        if (selectedFiles.length === 0) return;

        setIsUploading(true);
        try {
            await onUpload(selectedFiles, selectedFolderId);
            setSelectedFiles([]);
            onClose();
        } catch (error) {
            console.error('Upload failed:', error);
        } finally {
            setIsUploading(false);
        }
    };

    const handleClose = () => {
        if (!isUploading) {
            setSelectedFiles([]);
            onClose();
        }
    };

    const formatFileSize = (bytes: number): string => {
        if (bytes === 0) return '0 B';
        const k = 1024;
        const sizes = ['B', 'KB', 'MB', 'GB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
    };

    useEffect(() => {
        if (!isOpen) return;
        const handleKeyDown = (e: KeyboardEvent) => {
            if (e.key === 'Escape' && !isUploading) handleClose();
            if (e.key === 'Enter' && !isUploading) {
                const target = e.target as HTMLElement;
                if (target.tagName === 'BUTTON') return;
                
                e.preventDefault();
                handleUpload();
            }
        };
        window.addEventListener('keydown', handleKeyDown);
        return () => window.removeEventListener('keydown', handleKeyDown);
    }, [isOpen, isUploading, handleClose, handleUpload]);

    if (!isOpen) return null;

    const dialogMarkup = (
        <div className="fixed inset-0 z-[9999] flex items-center justify-center bg-black bg-opacity-50">
            <div className="bg-white rounded-lg shadow-xl max-w-2xl w-full mx-4 max-h-[90vh] flex flex-col">
                {/* Header */}
                <div className="flex items-center justify-between p-6 border-b border-gray-200">
                    <h2 className="text-xl font-semibold text-gray-900 flex items-baseline gap-2">
                        Upload Files
                        <span className="text-sm font-normal text-gray-500">to {selectedFolderPath || 'Root'}</span>
                    </h2>
                    <button
                        onClick={handleClose}
                        disabled={isUploading}
                        className="text-gray-400 hover:text-gray-600 disabled:opacity-50"
                    >
                        <svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                        </svg>
                    </button>
                </div>

                <div className="flex-1 overflow-hidden flex flex-col">
                    {/* File Selection */}
                    <div className="p-6 flex-1 overflow-hidden flex flex-col">
                        <h3 className="text-sm font-medium text-gray-700 mb-3">Select Files</h3>
                        
                        {/* Drop Zone */}
                        <div
                            onDrop={handleDrop}
                            onDragOver={handleDragOver}
                            className="border-2 border-dashed border-gray-300 rounded-lg p-6 text-center cursor-pointer hover:border-blue-400 mb-4"
                            onClick={() => fileInputRef.current?.click()}
                        >
                            <svg className="w-12 h-12 mx-auto text-gray-400 mb-4" fill="none" stroke="currentColor" viewBox="0 0 48 48">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M28 8H12a4 4 0 00-4 4v20m32-12v8m0 0v8a4 4 0 01-4 4H12a4 4 0 01-4-4v-4m32-4l-3.172-3.172a4 4 0 00-5.656 0L28 28M8 32l9.172-9.172a4 4 0 015.656 0L28 28m0 0l4 4m4-24h8m-4-4v8m-12 4h.02" />
                            </svg>
                            <p className="text-gray-600">
                                <span className="font-medium text-blue-600">Click to upload</span> or drag and drop
                            </p>
                            <p className="text-sm text-gray-500 mt-1">Any file type is supported</p>
                        </div>

                        <input
                            ref={fileInputRef}
                            type="file"
                            multiple
                            onChange={handleFileSelect}
                            className="hidden"
                            disabled={isUploading}
                        />

                        {/* Selected Files List */}
                        {selectedFiles.length > 0 && (
                            <div className="flex-1 overflow-hidden flex flex-col">
                                <h4 className="text-sm font-medium text-gray-700 mb-2">
                                    Selected Files ({selectedFiles.length})
                                </h4>
                                <div className="flex-1 border border-gray-200 rounded-md overflow-y-auto">
                                    {selectedFiles.map((file, index) => (
                                        <div key={index} className="flex items-center justify-between p-3 border-b border-gray-100 last:border-b-0">
                                            <div className="flex items-center flex-1 min-w-0">
                                                <svg className="w-4 h-4 mr-2 text-gray-400 flex-shrink-0" fill="currentColor" viewBox="0 0 20 20">
                                                    <path fillRule="evenodd" d="M4 4a2 2 0 012-2h4.586A2 2 0 0112 2.586L15.414 6A2 2 0 0116 7.414V16a2 2 0 01-2 2H6a2 2 0 01-2-2V4zm2 6a1 1 0 011-1h6a1 1 0 110 2H7a1 1 0 01-1-1zm1 3a1 1 0 100 2h6a1 1 0 100-2H7z" clipRule="evenodd" />
                                                </svg>
                                                <span className="text-sm truncate">{file.name}</span>
                                            </div>
                                            <div className="flex items-center space-x-2 flex-shrink-0">
                                                <span className="text-xs text-gray-500">{formatFileSize(file.size)}</span>
                                                <button
                                                    onClick={() => setSelectedFiles(files => files.filter((_, i) => i !== index))}
                                                    disabled={isUploading}
                                                    className="text-red-600 hover:text-red-800 disabled:opacity-50"
                                                >
                                                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                                                    </svg>
                                                </button>
                                            </div>
                                        </div>
                                    ))}
                                </div>
                            </div>
                        )}
                        {/* Destination Folder Picker (real tree) */}
                        <div className="mt-6">
                            <h3 className="text-sm font-medium text-gray-700 mb-2">Destination Folder</h3>
                            <div className="max-h-40 overflow-y-auto border border-gray-200 rounded-md p-2 text-sm">
                                <button
                                    type="button"
                                    className={`block w-full text-left px-2 py-1 rounded ${!selectedFolderId ? 'bg-blue-50 text-blue-700' : 'hover:bg-gray-50'}`}
                                    onClick={() => setSelectedFolderId(undefined)}
                                    disabled={isUploading}
                                >
                                    Root
                                </button>
                                {folderTree && renderFolderOptions(folderTree, 0, selectedFolderId, setSelectedFolderId, isUploading)}
                            </div>
                        </div>
                    </div>
                </div>

                {/* Footer */}
                <div className="p-6 border-t border-gray-200 flex items-center justify-between">
                    <div className="text-sm text-gray-600">
                        {selectedFiles.length > 0 && (
                            `${selectedFiles.length} file${selectedFiles.length > 1 ? 's' : ''} selected`
                        )}
                    </div>
                    <div className="flex space-x-3">
                        <button
                            onClick={handleClose}
                            disabled={isUploading}
                            className="px-4 py-2 text-sm border border-gray-300 rounded-md hover:bg-gray-50 disabled:opacity-50"
                        >
                            Cancel
                        </button>
                        <button
                            onClick={handleUpload}
                            disabled={selectedFiles.length === 0 || isUploading || disabled}
                            className="px-4 py-2 text-sm bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:opacity-50 flex items-center"
                        >
                            {isUploading ? (
                                <>
                                    <svg className="w-4 h-4 mr-2 animate-spin" fill="none" viewBox="0 0 24 24">
                                        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                                        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z" />
                                    </svg>
                                    Uploading...
                                </>
                            ) : (
                                'Upload Files'
                            )}
                        </button>
                    </div>
                </div>
            </div>
        </div>
    );

    return createPortal(dialogMarkup, document.body);
}; 