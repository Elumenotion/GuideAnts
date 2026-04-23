import React from 'react';
import { useNavigate } from 'react-router-dom';
import { FileLineageEvent } from '../../../types/fileLineage';
import { FaDownload, FaUpload, FaCopy, FaArrowUp, FaArrowsAlt, FaEdit, FaFileAlt } from 'react-icons/fa';
import { useDownloadLineage } from '../../../hooks/useDownloadLineage';

interface LineageTimelineProps {
    events: FileLineageEvent[];
    canDownload: boolean;
}

const actionIcon = (action: string) => {
    switch (action) {
        case 'Uploaded':
            return <FaUpload className="text-blue-600" />;
        case 'Versioned':
            return <FaArrowUp className="text-green-600" />;
        case 'CopiedToNotebook':
            return <FaCopy className="text-purple-600" />;
        case 'PublishedToProject':
            return <FaArrowUp className="text-orange-600" />;
        case 'Moved':
            return <FaArrowsAlt className="text-yellow-600" />;
        case 'Renamed':
            return <FaEdit className="text-teal-600" />;
        default:
            return <FaFileAlt className="text-gray-600" />;
    }
};

export const LineageTimeline: React.FC<LineageTimelineProps> = ({ events, canDownload }) => {
    const { download, isDownloading } = useDownloadLineage();
    const navigate = useNavigate();

    const handleNotebookClick = (projectId: string, notebookId: string) => {
        navigate(`/projects/${projectId}/notebooks/${notebookId}`);
    };

    if (!events.length) {
        return <p className="text-gray-500">No history available.</p>;
    }

    return (
        <ul className="space-y-4">
            {events.map(ev => (
                <li key={ev.id} className="flex items-start gap-3">
                    {/* Icon */}
                    <div className="mt-1">{actionIcon(ev.action)}</div>
                    {/* Details */}
                    <div className="flex-1">
                        <div className="flex items-center gap-2">
                            <span className="font-medium">{ev.action}</span>
                            {ev.versionNumber !== null && ev.versionNumber !== undefined && (
                                <span className="text-xs bg-gray-200 px-1 rounded">v{ev.versionNumber}</span>
                            )}
                        </div>
                        {ev.notebookName && ev.notebookId && (
                            <div className="mt-1">
                                <button
                                    onClick={() => handleNotebookClick(ev.projectId, ev.notebookId!)}
                                    className="text-xs bg-purple-100 text-purple-700 px-2 py-0.5 rounded-full hover:bg-purple-200 hover:text-purple-800 transition-colors cursor-pointer border-none"
                                    title={`Open ${ev.notebookName} notebook`}
                                >
                                    📓 {ev.notebookName}
                                </button>
                            </div>
                        )}
                        <div className="text-sm text-gray-600">
                            {new Date(ev.timestamp).toLocaleString()} – {ev.userDisplayName || ev.userId}
                        </div>
                    </div>
                    {/* Download */}
                    {canDownload && (
                        <button
                            className="p-2 text-gray-500 hover:text-gray-700"
                            disabled={isDownloading}
                            onClick={() => download(ev.id, ev.fileName)}
                            title="Download this version"
                        >
                            <FaDownload />
                        </button>
                    )}
                </li>
            ))}
        </ul>
    );
}; 