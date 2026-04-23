import { useState, useEffect, useMemo, useRef } from 'react';
import { resolveAgainstApiBase } from '../../../config/apiConfig';
import { createPortal } from 'react-dom';
import { NotebookTemplateSummaryDto } from '../../../types/project';
import { api } from '../../../services/api';

interface CreateNotebookDialogProps {
    projectId: string;
    isOpen: boolean;
    onClose: () => void;
    onCreate: (title: string, guideId?: string, description?: string) => Promise<void> | void;
    disabled?: boolean;
    initialFiles?: {
        id: string;
        fileName: string;
    }[] | null;
}

// HACK: Temporarily hide specific templates by name. Remove when no longer needed.
const HIDDEN_TEMPLATES = new Set<string>(['Code Notebook', 'Diagramming Notebook']);

// Helper function to generate smart notebook titles from file names
const generateNotebookTitle = (fileName: string): string => {
    const nameWithoutExt = fileName.replace(/\.[^/.]+$/, "");
    const formattedName = nameWithoutExt
        .replace(/[_-]/g, ' ')
        .replace(/\b\w/g, l => l.toUpperCase());
    return `Analysis - ${formattedName}`;
};

// We keep the auto-generated title in a ref so we can detect whether the user changed it
// without triggering effect loops when it updates.

export const CreateNotebookDialog: React.FC<CreateNotebookDialogProps> = ({
    projectId,
    isOpen,
    onClose,
    onCreate,
    disabled = false,
    initialFiles = null,
}) => {
    const [title, setTitle] = useState('');
    const [description, setDescription] = useState('');
    const [isSaving, setIsSaving] = useState(false);
    const [templates, setTemplates] = useState<NotebookTemplateSummaryDto[]>([]);
    const [selectedTemplateId, setSelectedTemplateId] = useState<string | undefined>();
    const [guideSearch, setGuideSearch] = useState('');
    type SortOption = 'title-asc' | 'title-desc';
    const [sortOption, setSortOption] = useState<SortOption>('title-asc');
    // Track the last auto-generated title to detect user edits
    const autoTitleRef = useRef<string>('');

    // Fetch templates when dialog opens
    useEffect(() => {
        if (!isOpen) return;
        
        // Reset state when dialog opens
        autoTitleRef.current = '';
        setTitle('');
        setSelectedTemplateId(undefined); // Clear selection when dialog opens
        
        api.projects.notebookTemplates.getAll(projectId)
            .then(setTemplates)
            .catch(err => console.error('Failed to load templates', err));
    }, [isOpen, projectId]);

    // Default select first template from the sorted list when templates loaded
    useEffect(() => {
        if (templates.length > 0 && !selectedTemplateId) {
            // Filter out hidden templates and sort by name (A-Z by default)
            const visibleTemplates = templates
                .filter(t => !HIDDEN_TEMPLATES.has(t.templateName))
                .sort((a, b) => a.templateName.localeCompare(b.templateName));
            
            if (visibleTemplates.length > 0) {
                setSelectedTemplateId(visibleTemplates[0].id);
            }
        }
    }, [templates, selectedTemplateId]);

    // Set smart default title when creating from file (only once per open)
    useEffect(() => {
        if (!isOpen) return;
        if (initialFiles && initialFiles.length > 0 && autoTitleRef.current === '') {
            const t = initialFiles.length === 1
                ? generateNotebookTitle(initialFiles[0].fileName)
                : `Analysis - ${initialFiles.length} Files`;
            autoTitleRef.current = t;
            setTitle(t);
        }
    }, [isOpen, initialFiles]);

    // Update title when template selection changes, but only if user hasn't modified it
    useEffect(() => {
        if (!selectedTemplateId || templates.length === 0) return;
        
        const selectedTemplate = templates.find(t => t.id === selectedTemplateId);
        if (!selectedTemplate) return;
        
        // Only auto-update if the current title exactly matches the last auto-set title
        // Use a callback to read the current title without adding it to dependencies
        setTitle(currentTitle => {
            // If user hasn't changed the title from the auto-generated one, update it
            if (currentTitle === autoTitleRef.current || currentTitle === '') {
                const newTitle = selectedTemplate.templateName;
                autoTitleRef.current = newTitle;
                return newTitle;
            }
            // User has modified the title, don't override it
            return currentTitle;
        });
    }, [selectedTemplateId, templates]);

    const handleSubmit = async () => {
        if (!title.trim()) return;
        setIsSaving(true);
        try {
            await onCreate(title.trim(), selectedTemplateId, description.trim() || undefined);
            setTitle('');
            setDescription('');
            onClose();
        } catch (error) {
            console.error('Failed to create notebook:', error);
        } finally {
            setIsSaving(false);
        }
    };

    const handleClose = () => {
        if (!isSaving) {
            setTitle('');
            setDescription('');
            autoTitleRef.current = '';
            onClose();
        }
    };

    const filteredAndSortedTemplates = useMemo(() => {
        const normalizedQuery = guideSearch.trim().toLowerCase();
        const filtered = (normalizedQuery
            ? templates.filter(t => {
                const name = t.templateName?.toLowerCase() ?? '';
                const desc = t.description?.toLowerCase() ?? '';
                return name.includes(normalizedQuery) || desc.includes(normalizedQuery);
            })
            : templates.slice()
        ).filter(t => !HIDDEN_TEMPLATES.has(t.templateName));

        filtered.sort((a, b) => {
            const aTitle = a.templateName.toLocaleLowerCase();
            const bTitle = b.templateName.toLocaleLowerCase();
            if (sortOption === 'title-asc') return aTitle.localeCompare(bTitle);
            return bTitle.localeCompare(aTitle);
        });

        return filtered;
    }, [templates, guideSearch, sortOption]);

    useEffect(() => {
        if (!isOpen) return;
        const handleKeyDown = (e: KeyboardEvent) => {
            if (e.key === 'Escape') handleClose();
            if (e.key === 'Enter' && !e.shiftKey && !e.altKey && !e.ctrlKey && !e.metaKey) {
                const target = e.target as HTMLElement;
                if (target.tagName === 'TEXTAREA') return;
                // Don't trigger if a button is focused (let native behavior work)
                if (target.tagName === 'BUTTON') return;
                
                e.preventDefault();
                handleSubmit();
            }
        };
        window.addEventListener('keydown', handleKeyDown);
        return () => window.removeEventListener('keydown', handleKeyDown);
    }, [isOpen, handleClose, handleSubmit]);

    if (!isOpen) return null;

    const dialog = (
        <div className="fixed inset-0 z-[9999] flex items-center justify-center bg-black bg-opacity-50">
            <div className="bg-white rounded-lg shadow-xl w-full max-w-3xl mx-4 max-h-[90vh] overflow-y-auto">
                {/* Header */}
                <div className="flex items-center justify-between p-6 border-b border-gray-200">
                    <h2 className="text-xl font-semibold text-gray-900">
                        {initialFiles && initialFiles.length > 0 ? 'Create Notebook from Files' : 'New Notebook'}
                    </h2>
                    <button
                        onClick={handleClose}
                        disabled={isSaving}
                        className="text-gray-400 hover:text-gray-600 disabled:opacity-50"
                    >
                        <svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                        </svg>
                    </button>
                </div>

                {/* Body */}
                <div className="p-6 space-y-6">
                    {/* File inclusion notice */}
                    {initialFiles && initialFiles.length > 0 && (
                        <div className="bg-blue-50 border border-blue-200 rounded-md p-3">
                            <div className="flex items-start">
                                <svg className="w-5 h-5 text-blue-400 mt-0.5 mr-2" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                                </svg>
                                <div className="text-sm">
                                    <p className="text-blue-800 font-medium">Files will be included</p>
                                    <p className="text-blue-600">
                                        {initialFiles.length === 1 
                                            ? `${initialFiles[0].fileName} will be copied to the new notebook`
                                            : `${initialFiles.length} files will be copied to the new notebook`
                                        }
                                    </p>
                                </div>
                            </div>
                        </div>
                    )}

                    <div>
                        <label htmlFor="notebook-title" className="block text-sm font-medium text-gray-700 mb-1">
                            Title
                        </label>
                        <input
                            id="notebook-title"
                            type="text"
                            value={title}
                            onChange={(e) => setTitle(e.target.value)}
                            className="w-full border border-gray-300 rounded-md px-3 py-2 focus:outline-none focus:ring-2 focus:ring-blue-500"
                            placeholder="Enter notebook title"
                            disabled={isSaving || disabled}
                            autoFocus={!initialFiles}
                        />
                    </div>
                    
                    <div>
                        <label htmlFor="notebook-description" className="block text-sm font-medium text-gray-700 mb-1">
                            Description <span className="text-gray-500 text-xs">(optional)</span>
                        </label>
                        <textarea
                            id="notebook-description"
                            value={description}
                            onChange={(e) => setDescription(e.target.value)}
                            className="w-full border border-gray-300 rounded-md px-3 py-2 focus:outline-none focus:ring-2 focus:ring-blue-500 resize-none"
                            placeholder="Describe what this notebook will be used for..."
                            rows={3}
                            maxLength={1000}
                            disabled={isSaving || disabled}
                        />
                        <div className="text-xs text-gray-500 text-right mt-1">
                            {description.length}/1000 characters
                        </div>
                    </div>
                    {/* Template Selection */}
                    <div>
                        <p className="block text-sm font-medium text-gray-700">Pick a Guide</p>
                        <div className="mt-2 flex w-full items-center gap-2">
                            <div className="relative flex-1">
                                <input
                                    type="text"
                                    value={guideSearch}
                                    onChange={(e) => setGuideSearch(e.target.value)}
                                    placeholder="Search guides by title or description"
                                    className="w-full border border-gray-300 rounded-md pl-9 pr-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                                    aria-label="Search guides"
                                    disabled={isSaving || disabled}
                                />
                                <svg className="absolute left-2 top-2.5 h-5 w-5 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-4.35-4.35M10 18a8 8 0 110-16 8 8 0 010 16z" />
                                </svg>
                            </div>
                            <select
                                className="ml-auto border border-gray-300 rounded-md px-2 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                                value={sortOption}
                                onChange={(e) => setSortOption(e.target.value as SortOption)}
                                aria-label="Sort guides"
                                disabled={isSaving || disabled}
                            >
                                <option value="title-asc">Title (A–Z)</option>
                                <option value="title-desc">Title (Z–A)</option>
                            </select>
                        </div>

                        <div className="mt-3 max-h-80 overflow-y-auto pr-1 space-y-2" role="radiogroup" aria-label="Guide templates">
                            {filteredAndSortedTemplates.map(t => {
                                const isSelected = selectedTemplateId === t.id;
                                let src = t.avatarUrl?.startsWith('http')
                                    ? t.avatarUrl
                                    : (t.avatarUrl ? resolveAgainstApiBase(t.avatarUrl).toString() : undefined);
                                
                                // Append projectId to relative avatar URLs
                                if (src && !src.startsWith('http') && src.includes('/api/')) {
                                    src = `${src}?projectId=${projectId}`;
                                } else if (src && src.startsWith('http') && src.includes('/api/') && !src.includes('projectId=')) {
                                    src = `${src}?projectId=${projectId}`;
                                }

                                return (
                                    <button
                                        type="button"
                                        key={t.id}
                                        onClick={() => setSelectedTemplateId(t.id)}
                                        disabled={isSaving || disabled}
                                        role="radio"
                                        aria-checked={isSelected}
                                        className={`relative w-full flex items-center gap-3 rounded-md border p-2 text-left transition ${
                                            isSelected ? 'border-blue-600 ring-2 ring-blue-500 bg-blue-50' : 'border-gray-300 hover:border-gray-400'
                                        } disabled:opacity-50`}
                                    >
                                        <div className="flex-shrink-0 w-10 h-10 rounded border border-gray-200 bg-white overflow-hidden">
                                            {src && <img src={src} alt={t.templateName} className="w-full h-full object-contain" />}
                                        </div>
                                        <div className="min-w-0 flex-1 leading-tight">
                                            <p className="text-sm font-semibold text-gray-900 truncate" title={t.templateName}>{t.templateName}</p>
                                            {t.description && (
                                                <p className="text-xs text-gray-600 truncate" title={t.description}>{t.description}</p>
                                            )}
                                        </div>
                                        {isSelected && (
                                            <svg className="absolute top-2 right-2 w-5 h-5 text-blue-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                                            </svg>
                                        )}
                                    </button>
                                );
                            })}
                            {filteredAndSortedTemplates.length === 0 && (
                                <div className="text-sm text-gray-500">No guides match your search.</div>
                            )}
                        </div>
                        <p className="mt-2 text-xs text-gray-500">
                            {filteredAndSortedTemplates.length} of {templates.filter(t => !HIDDEN_TEMPLATES.has(t.templateName)).length} shown
                        </p>
                    </div>
                </div>

                {/* Footer */}
                <div className="p-6 border-t border-gray-200 flex justify-end space-x-3">
                    <button
                        onClick={handleClose}
                        disabled={isSaving}
                        className="px-4 py-2 text-sm border border-gray-300 rounded-md hover:bg-gray-50 disabled:opacity-50"
                    >
                        Cancel
                    </button>
                    <button
                        onClick={handleSubmit}
                        disabled={!title.trim() || isSaving || disabled}
                        className="px-4 py-2 text-sm bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:opacity-50"
                    >
                        {isSaving ? (initialFiles ? 'Creating & Copying...' : 'Creating...') : 'Create'}
                    </button>
                </div>
            </div>
        </div>
    );

    return createPortal(dialog, document.body);
}; 
