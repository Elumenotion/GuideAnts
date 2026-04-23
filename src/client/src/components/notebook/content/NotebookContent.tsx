import React, { useState } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';

import { ProjectNotebook } from '../../../types/project';
import { NotebookCell } from '../../../types/notebook';
import { useNotebook } from '../../../contexts/NotebookContext';
import { encodeMarkdownLinkSpaces } from '../../../utils/markdownLinkEncoding';

interface NotebookContentProps {
    notebook: ProjectNotebook;
    canEdit?: boolean;
}

interface NotebookCellComponentProps {
    cell: NotebookCell;
    isSelected: boolean;
    onSelect: () => void;
    onUpdate: (content: string) => void;
    onExecute?: () => void;
    canEdit: boolean;
    isExecuting?: boolean;
}

// Custom link component that handles external links properly in Electron
const ExternalLink: React.FC<{ href?: string; children: React.ReactNode }> = ({ href, children }) => {
    const handleClick = (e: React.MouseEvent) => {
        e.preventDefault();
        if (href && (window as any).electron?.openExternal) {
            (window as any).electron.openExternal(href);
        }
    };

    return (
        <a 
            href={href} 
            onClick={handleClick}
            className="text-blue-600 hover:text-blue-800 underline cursor-pointer"
            target="_blank"
            rel="noopener noreferrer"
        >
            {children}
        </a>
    );
};

function NotebookCellComponent({
    cell,
    isSelected,
    onSelect,
    onUpdate,
    onExecute,
    canEdit,
    isExecuting = false
}: NotebookCellComponentProps) {
    const [isEditing, setIsEditing] = useState(false);
    const [editContent, setEditContent] = useState(cell.content);

    const handleSave = () => {
        onUpdate(editContent);
        setIsEditing(false);
    };

    const handleCancel = () => {
        setEditContent(cell.content);
        setIsEditing(false);
    };

    const renderCellContent = () => {
        if (isEditing) {
            return (
                <div className="space-y-2">
                    <textarea
                        value={editContent}
                        onChange={(e) => setEditContent(e.target.value)}
                        className="w-full p-3 border rounded font-mono text-sm resize-vertical min-h-[100px]"
                        placeholder={`Enter ${cell.type} content...`}
                    />
                    <div className="flex space-x-2">
                        <button
                            onClick={handleSave}
                            className="px-3 py-1 bg-blue-600 text-white rounded text-sm hover:bg-blue-700"
                        >
                            Save
                        </button>
                        <button
                            onClick={handleCancel}
                            className="px-3 py-1 border rounded text-sm hover:bg-gray-50"
                        >
                            Cancel
                        </button>
                    </div>
                </div>
            );
        }

        switch (cell.type) {
            case 'code':
                return (
                    <div className="space-y-2">
                        <pre className="bg-gray-100 p-3 rounded font-mono text-sm overflow-x-auto">
                            <code>{cell.content}</code>
                        </pre>
                        {cell.output && (
                            <div className="bg-gray-50 p-3 rounded border-l-4 border-green-500">
                                <div className="text-xs text-gray-600 mb-1">Output:</div>
                                <pre className="font-mono text-sm">{cell.output}</pre>
                            </div>
                        )}
                        {canEdit && onExecute && (
                            <button
                                onClick={onExecute}
                                disabled={isExecuting}
                                className="px-3 py-1 bg-green-600 text-white rounded text-sm hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed flex items-center space-x-1"
                            >
                                {isExecuting && (
                                    <div className="animate-spin w-3 h-3 border border-white border-t-transparent rounded-full"></div>
                                )}
                                <span>{isExecuting ? 'Executing...' : 'Run'}</span>
                            </button>
                        )}
                    </div>
                );
            
            case 'markdown': {
                const processedMarkdown = encodeMarkdownLinkSpaces(cell.content);
                return (
                    <div className="prose max-w-none">
                        <ReactMarkdown
                            children={processedMarkdown}
                            remarkPlugins={[remarkGfm]}
                            components={{
                                ul({ children }: { children: React.ReactNode }) {
                                    return <ul className="list-disc list-outside mb-2 ml-6 pl-2">{children}</ul>;
                                },
                                ol({ children }: { children: React.ReactNode }) {
                                    return <ol className="list-decimal list-outside mb-2 ml-6 pl-2">{children}</ol>;
                                },
                                li({ children }: { children: React.ReactNode }) {
                                    return <li className="mb-1 pl-2">{children}</li>;
                                },
                                a: ExternalLink
                            }}
                        />
                    </div>
                );
            }
            
            case 'text':
                return (
                    <div className="prose max-w-none">
                        <p className="whitespace-pre-wrap">{cell.content}</p>
                    </div>
                );
            
            default:
                return <div className="text-gray-500">Unknown cell type</div>;
        }
    };

    const getCellIcon = () => {
        switch (cell.type) {
            case 'code': return '⚡';
            case 'markdown': return '📝';
            case 'text': return '📄';
            default: return '📄';
        }
    };

    return (
        <div
            className={`border rounded-lg p-4 cursor-pointer transition-all ${
                isSelected ? 'border-blue-500 bg-blue-50' : 'border-gray-200 hover:border-gray-300'
            }`}
            onClick={onSelect}
        >
            <div className="flex items-center justify-between mb-3">
                <div className="flex items-center space-x-2">
                    <span className="text-lg">{getCellIcon()}</span>
                    <span className="text-sm font-medium text-gray-600 capitalize">
                        {cell.type} Cell
                    </span>
                    {cell.language && (
                        <span className="text-xs bg-gray-100 px-2 py-1 rounded">
                            {cell.language}
                        </span>
                    )}
                </div>
                {canEdit && !isEditing && (
                    <button
                        onClick={(e) => {
                            e.stopPropagation();
                            setIsEditing(true);
                        }}
                        className="text-sm text-blue-600 hover:text-blue-800"
                    >
                        Edit
                    </button>
                )}
            </div>
            {renderCellContent()}
        </div>
    );
}

export function NotebookContent({ notebook: passedNotebook, canEdit = false }: NotebookContentProps) {
    const {
        notebook: contextNotebook,
        selectedCell,
        setSelectedCell,
        updateCell,
        executeCell,
        isExecuting,
        executingCellId
    } = useNotebook();

    // Use the real notebook data passed as props (from NotebookDetails), fallback to context data for cells
    const notebookDetails = contextNotebook;
    const realNotebook = passedNotebook;

    if (!notebookDetails || !realNotebook) {
        return (
            <div className="flex items-center justify-center h-64">
                <div className="text-center">
                    <div className="text-gray-500 mb-2">Loading notebook content...</div>
                </div>
            </div>
        );
    }

    const handleCellUpdate = async (cellId: string, content: string) => {
        await updateCell(cellId, { content });
    };

    const handleCellExecute = async (cellId: string) => {
        await executeCell(cellId);
    };

    const cells = notebookDetails.cells ?? [];
    const sortedCells = [...cells].sort((a, b) => (a.order ?? 0) - (b.order ?? 0));

    return (
        <div className="h-full overflow-y-auto" data-tour-id="notebook.cells.content">
            <div className="w-full max-w-none px-6 py-4 space-y-4">
                {/* Header section with constrained width for readability */}
                <div className="border-b pb-6 mb-6">
                    <div className="mb-4">
                        <h1 className="text-3xl font-bold text-gray-900 mb-2">{realNotebook.title}</h1>
                        {/* Metadata row removed per design update */}
                    </div>
                    {realNotebook.description && (
                        <div className="bg-gray-50 rounded-lg p-4 border-l-4 border-blue-400">
                            <div className="text-gray-700 leading-relaxed whitespace-pre-wrap">
                                {realNotebook.description}
                            </div>
                        </div>
                    )}
                </div>

                {sortedCells.length === 0 ? (
                    <div className="text-center py-12">
                        <div className="text-gray-500 mb-4">
                            <svg className="w-16 h-16 mx-auto mb-4 text-gray-300" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                            </svg>
                            <p className="text-lg font-medium">Welcome to your notebook</p>
                            <p className="text-sm">Select a conversation or file from the sidebar to get started</p>
                        </div>
                    </div>
                ) : (
                    <div className="space-y-4 w-full">
                        {sortedCells.map(cell => (
                            <NotebookCellComponent
                                key={cell.id}
                                cell={cell}
                                isSelected={selectedCell?.id === cell.id}
                                onSelect={() => setSelectedCell(cell)}
                                onUpdate={(content) => handleCellUpdate(cell.id, content)}
                                onExecute={cell.type === 'code' ? () => handleCellExecute(cell.id) : undefined}
                                canEdit={canEdit}
                                isExecuting={isExecuting && executingCellId === cell.id}
                            />
                        ))}
                    </div>
                )}
            </div>
        </div>
    );
} 