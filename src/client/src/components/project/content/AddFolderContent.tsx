import { useState } from 'react';
import { CreateFolderDto, ProjectFolderDto } from '../../../types/project';

interface AddFolderContentProps {
    onAdd: (folderData: CreateFolderDto) => Promise<void>;
    onCancel: () => void;
    folders: ProjectFolderDto[];
    disabled?: boolean;
}

export function AddFolderContent({ onAdd, onCancel, folders, disabled = false }: AddFolderContentProps) {
    const [folderName, setFolderName] = useState('');
    const [parentFolderId, setParentFolderId] = useState<string>('');
    const [isCreating, setIsCreating] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        
        const trimmedName = folderName.trim();
        if (!trimmedName) {
            setError('Folder name is required');
            return;
        }

        // Check for duplicate folder names in the same parent folder
        const existingFolder = folders.find(f => 
            f.name.toLowerCase() === trimmedName.toLowerCase() &&
            f.parentFolderId === (parentFolderId || undefined)
        );
        
        if (existingFolder) {
            setError('A folder with this name already exists in the selected location');
            return;
        }

        setError(null);
        setIsCreating(true);
        try {
            const folderData: CreateFolderDto = {
                name: trimmedName,
                parentFolderId: parentFolderId || undefined
            };
            await onAdd(folderData);
            onCancel();
        } catch (error) {
            console.error('Failed to create folder:', error);
            setError('Failed to create folder. Please try again.');
        } finally {
            setIsCreating(false);
        }
    };

    // Build folder hierarchy for parent selection
    const buildFolderOptions = () => {
        const options = [{ id: '', name: 'Root', path: '' }];
        
        const addFolderWithPath = (folder: ProjectFolderDto, path: string) => {
            const folderPath = path ? `${path}/${folder.name}` : folder.name;
            options.push({ id: folder.id, name: folder.name, path: folderPath });
            
            // Add subfolders
            folders
                .filter(f => f.parentFolderId === folder.id)
                .sort((a, b) => a.name.localeCompare(b.name))
                .forEach(subfolder => addFolderWithPath(subfolder, folderPath));
        };

        // Add root folders and their children
        folders
            .filter(f => !f.parentFolderId)
            .sort((a, b) => a.name.localeCompare(b.name))
            .forEach(folder => addFolderWithPath(folder, ''));

        return options;
    };

    const folderOptions = buildFolderOptions();

    return (
        <div>
            <div className="flex items-center justify-between mb-4">
                <h2 className="text-xl font-semibold">Create New Folder</h2>
            </div>

            {error && (
                <div className="mb-4 p-4 bg-red-50 text-red-600 rounded">
                    {error}
                </div>
            )}

            <form onSubmit={handleSubmit} className="space-y-4">
                <div className="border rounded p-4">
                    <h3 className="font-medium mb-2">Folder Details</h3>
                    <div className="space-y-4">
                        <div>
                            <label htmlFor="folderName" className="block text-sm font-medium text-gray-700 mb-1">
                                Folder Name
                            </label>
                            <input
                                type="text"
                                id="folderName"
                                value={folderName}
                                onChange={(e) => {
                                    setFolderName(e.target.value);
                                    setError(null);
                                }}
                                className="w-full px-3 py-2 border rounded focus:outline-none focus:ring-2 focus:ring-blue-500"
                                placeholder="Enter folder name..."
                                disabled={isCreating || disabled}
                                aria-disabled={isCreating || disabled}
                                required
                                maxLength={255}
                                autoFocus
                            />
                        </div>

                        <div>
                            <label htmlFor="parentFolder" className="block text-sm font-medium text-gray-700 mb-1">
                                Parent Folder
                            </label>
                            <select
                                id="parentFolder"
                                value={parentFolderId}
                                onChange={(e) => setParentFolderId(e.target.value)}
                                className="w-full px-3 py-2 border rounded focus:outline-none focus:ring-2 focus:ring-blue-500"
                                disabled={isCreating || disabled}
                                aria-disabled={isCreating || disabled}
                            >
                                {folderOptions.map(option => (
                                    <option key={option.id} value={option.id}>
                                        {option.path || 'Root'}
                                    </option>
                                ))}
                            </select>
                            <p className="mt-1 text-sm text-gray-500">
                                Select where to create the new folder
                            </p>
                        </div>
                    </div>
                </div>

                <div className="flex gap-2 justify-end">
                    <button
                        type="button"
                        onClick={onCancel}
                        disabled={isCreating}
                        aria-disabled={isCreating}
                        className="px-4 py-2 text-sm border rounded hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed"
                    >
                        Cancel
                    </button>
                    <button
                        type="submit"
                        disabled={isCreating || disabled || !folderName.trim()}
                        aria-disabled={isCreating || disabled || !folderName.trim()}
                        className="px-4 py-2 text-sm bg-blue-600 text-white rounded hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed flex items-center"
                    >
                        {isCreating && (
                            <svg className="animate-spin -ml-1 mr-2 h-4 w-4 text-white" fill="none" viewBox="0 0 24 24">
                                <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                                <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
                            </svg>
                        )}
                        {isCreating ? 'Creating...' : 'Create Folder'}
                    </button>
                </div>
            </form>
        </div>
    );
} 