import React from 'react';
import { useFileLineage } from '../../../hooks/useFileLineage';
import { LineageTimeline } from './LineageTimeline';
import { useProject } from '../../../contexts/ProjectContext';

interface HistoryDrawerProps {
    projectId: string;
    fileId: string;
    isOpen: boolean;
    onClose: () => void;
    refreshKey?: string | number; // cause refresh when version changes
}

export const HistoryDrawer: React.FC<HistoryDrawerProps> = ({ projectId, fileId, isOpen, onClose, refreshKey }) => {
    const { events, isLoading, error, refresh } = useFileLineage(projectId, fileId);
    const { canEdit } = useProject();

    // Auto-refresh when refreshKey changes (e.g., latestVersion)
    React.useEffect(() => {
        if (!isOpen) return;
        try { refresh(); } catch {}
    }, [refreshKey, isOpen, refresh]);

    if (!isOpen) return null;

    return (
        <div className="fixed inset-0 z-50">
            {/* Overlay */}
            <div className="absolute inset-0 bg-black bg-opacity-50" onClick={onClose} />
            {/* Drawer */}
            <div className="absolute top-0 right-0 w-full max-w-md h-full bg-white shadow-lg transform transition-transform">
                <div className="flex flex-col h-full">
                    <header className="flex items-center justify-between px-4 py-3 border-b">
                        <h2 className="text-lg font-semibold">File History</h2>
                        <button
                            className="text-gray-500 hover:text-gray-700"
                            onClick={onClose}
                            aria-label="Close"
                        >
                            ×
                        </button>
                    </header>
                    <div className="flex-1 overflow-y-auto p-4">
                        {isLoading && <p className="text-gray-500">Loading...</p>}
                        {error && <p className="text-red-600">{error}</p>}
                        {!isLoading && !error && (
                            <LineageTimeline events={events} canDownload={canEdit()} />
                        )}
                    </div>
                    <footer className="border-t p-3 text-right">
                        <button
                            className="text-sm text-blue-600 hover:underline mr-2"
                            onClick={refresh}
                        >
                            Refresh
                        </button>
                        <button
                            className="px-4 py-2 text-sm bg-blue-600 text-white rounded"
                            onClick={onClose}
                        >
                            Close
                        </button>
                    </footer>
                </div>
            </div>
        </div>
    );
}; 