import React, { useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { FileUsageInNotebookDto } from '../../types/api';

interface FileInUseDialogProps {
    isOpen: boolean;
    onClose: () => void;
    fileName: string;
    notebooksUsingFile: FileUsageInNotebookDto[];
    projectId: string;
}

export const FileInUseDialog: React.FC<FileInUseDialogProps> = ({
    isOpen,
    onClose,
    fileName,
    notebooksUsingFile,
    projectId
}) => {
    const navigate = useNavigate();
    
    useEffect(() => {
        if (!isOpen) return;
        const handleKeyDown = (e: KeyboardEvent) => {
            if (e.key === 'Escape') onClose();
            if (e.key === 'Enter') {
                const target = e.target as HTMLElement;
                if (target.tagName === 'BUTTON') return;
                
                e.preventDefault();
                onClose();
            }
        };
        window.addEventListener('keydown', handleKeyDown);
        return () => window.removeEventListener('keydown', handleKeyDown);
    }, [isOpen, onClose]);
    
    if (!isOpen) return null;

    const handleNotebookClick = (notebookId: string) => {
        // Close the dialog first
        onClose();
        // Navigate to the specific notebook using React Router
        navigate(`/projects/${projectId}/notebooks/${notebookId}`);
    };

    return (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50" onClick={onClose}>
            <div 
                className="bg-white rounded-lg shadow-xl max-w-md w-full mx-4 max-h-[80vh] overflow-hidden"
                onClick={(e) => e.stopPropagation()}
            >
                <div className="px-6 py-4 border-b border-gray-200">
                    <h3 className="text-lg font-semibold text-gray-900">Cannot Delete File</h3>
                </div>
                
                <div className="px-6 py-4 max-h-96 overflow-y-auto">
                    <div className="mb-4">
                        <p className="text-gray-700 mb-2">
                            The file <strong>"{fileName}"</strong> cannot be deleted because it is currently being used in the following notebooks:
                        </p>
                        <p className="text-sm text-blue-600 mb-4">
                            <span className="inline-flex items-center">
                                <svg className="w-4 h-4 mr-1" fill="currentColor" viewBox="0 0 20 20">
                                    <path fillRule="evenodd" d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zm-7-4a1 1 0 11-2 0 1 1 0 012 0zM9 9a1 1 0 000 2v3a1 1 0 001 1h1a1 1 0 100-2v-3a1 1 0 00-1-1H9z" clipRule="evenodd" />
                                </svg>
                                This feature preserves data traceability across notebooks
                            </span>
                        </p>
                    </div>
                    
                    <div className="space-y-3">
                        <h4 className="font-medium text-gray-900">Notebooks using this file:</h4>
                        {notebooksUsingFile.map((usage) => (
                            <div 
                                key={`${usage.notebookId}-${usage.notebookFileId}`}
                                className="border border-gray-200 rounded-lg p-3 hover:bg-gray-50 cursor-pointer transition-colors"
                                onClick={() => handleNotebookClick(usage.notebookId)}
                            >
                                <div className="flex items-start justify-between">
                                    <div className="flex-1">
                                        <p className="font-medium text-blue-600 hover:text-blue-800">
                                            {usage.notebookTitle}
                                        </p>
                                        <p className="text-sm text-gray-600 mt-1">
                                            File location: <code className="bg-gray-100 px-1 rounded text-xs">{usage.relativePath}</code>
                                        </p>
                                    </div>
                                    <svg className="w-4 h-4 text-gray-400 mt-1 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-2M14 4h6m0 0v6m0-6L10 14" />
                                    </svg>
                                </div>
                            </div>
                        ))}
                    </div>
                    
                    <div className="mt-6 p-4 bg-amber-50 rounded-lg border border-amber-200">
                        <h5 className="font-medium text-amber-800 mb-2">To delete this file:</h5>
                        <ol className="text-sm text-amber-700 space-y-1 list-decimal list-outside ml-6 pl-2">
                            <li>Remove the file from all notebooks listed above</li>
                            <li>Return to this page and try deleting again</li>
                        </ol>
                    </div>
                </div>
                
                <div className="px-6 py-4 border-t border-gray-200 flex justify-end">
                    <button
                        onClick={onClose}
                        className="px-4 py-2 text-sm font-medium text-gray-700 bg-gray-100 hover:bg-gray-200 rounded-md transition-colors focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2"
                    >
                        Close
                    </button>
                </div>
            </div>
        </div>
    );
}; 