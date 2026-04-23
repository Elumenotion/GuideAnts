import { useState, useEffect } from 'react';
import { createPortal } from 'react-dom';
import { NotebookFolderTreeDto } from '../../../types/notebook';
import { useToast } from '../../common/Toast';
import { useNotebook } from '../../../contexts/NotebookContext';
import { useNotebookFilesPolling } from '../../../hooks/useNotebookFilesPolling';

interface SaveAssistantContentDialogProps {
  isOpen: boolean;
  onClose: () => void;
  onSave: (fileName: string, folderPath?: string) => Promise<void>;
  content: string;
  disabled?: boolean;
  assistantName?: string;
  notebookTitle?: string;
}

export const SaveAssistantContentDialog: React.FC<SaveAssistantContentDialogProps> = ({
  isOpen,
  onClose,
  onSave,
  content,
  disabled = false,
  assistantName,
  notebookTitle
}) => {
  const { projectId, notebookId } = useNotebook();
  
  // Get folder tree directly from polling hook
  const { folderTree } = useNotebookFilesPolling({
    projectId: projectId || '',
    notebookId: notebookId || '',
    enabled: !!(projectId && notebookId && isOpen), // Only poll when dialog is open
  });
  const [fileName, setFileName] = useState(() => {
    // Generate intelligent default file name
    const timestamp = new Date().toISOString()
      .replace(/:/g, '-')
      .replace(/\./g, '-')
      .slice(0, 16);
    
    // Extract first meaningful line from content for base name
    let baseName = content
      .split('\n')
      .find(line => line.trim().length > 10)
      ?.trim()
      .slice(0, 50)
      .replace(/[^a-zA-Z0-9\s-]/g, '')
      .replace(/\s+/g, '-')
      .toLowerCase();

    if (!baseName) {
      baseName = 'assistant-response';
    }

    // Add assistant name if available
    if (assistantName) {
      baseName = `${assistantName.toLowerCase().replace(/\s+/g, '-')}-${baseName}`;
    }

    return `${baseName}-${timestamp}.md`;
  });

  const [selectedFolderPath, setSelectedFolderPath] = useState<string | undefined>(undefined);
  const [isSaving, setIsSaving] = useState(false);
  const [fileNameError, setFileNameError] = useState<string | null>(null);
  
  const { showToast } = useToast();

  // Validate file name
  const validateFileName = (name: string): string | null => {
    if (!name.trim()) {
      return 'File name is required';
    }
    
    if (name.length > 255) {
      return 'File name is too long';
    }
    
    if (!/^[a-zA-Z0-9\s\-_()[\]{}.,!@#$%^&+=]+\.md$/.test(name)) {
      return 'File name must end with .md and contain only valid characters';
    }
    
    return null;
  };

  const handleFileNameChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const value = e.target.value;
    setFileName(value);
    setFileNameError(validateFileName(value));
  };

  const handleSave = async () => {
    const error = validateFileName(fileName);
    if (error) {
      setFileNameError(error);
      return;
    }

    setIsSaving(true);
    try {
      await onSave(fileName, selectedFolderPath);
      onClose();
      
      showToast({
        type: 'success',
        title: 'Assistant response saved',
        message: `Saved as "${fileName}" in notebook`,
        duration: 4000
      });
    } catch (error) {
      console.error('Save failed:', error);
      
      let userMessage = 'Failed to save assistant response. Please try again.';

      if (error instanceof Error) {
        if (error.message.includes('Authentication')) {
          userMessage = 'Authentication expired. Please refresh and try again.';
        } else if (error.message.includes('Network')) {
          userMessage = 'Network error. Please check your connection and try again.';
        } else if (error.message.includes('Storage')) {
          userMessage = 'Storage limit reached. Please free up space and try again.';
        }
      }

      showToast({
        type: 'error',
        title: 'Save Failed',
        message: userMessage,
        duration: 6000
      });
    } finally {
      setIsSaving(false);
    }
  };

  const handleClose = () => {
    if (!isSaving) {
      onClose();
    }
  };

  useEffect(() => {
    if (!isOpen) return;
    const handleKeyDown = (e: KeyboardEvent) => {
        if (e.key === 'Escape' && !isSaving) handleClose();
        if (e.key === 'Enter' && !isSaving) {
            const target = e.target as HTMLElement;
            if (target.tagName === 'BUTTON') return;
            
            e.preventDefault();
            handleSave();
        }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isOpen, isSaving, handleClose, handleSave]);

  // Render folder tree for selection
  const renderFolderTree = (folder: NotebookFolderTreeDto, level: number = 0): React.ReactNode => {
    const isSelected = selectedFolderPath === folder.relativePath;
    
    return (
      <div key={folder.relativePath || folder.name} className="select-none">
        {/* Folder option */}
        <div
          className={`flex items-center py-2 px-3 text-sm cursor-pointer hover:bg-gray-50 ${
            isSelected ? 'bg-blue-50 border-l-2 border-blue-500' : ''
          }`}
          style={{ paddingLeft: `${level * 16 + 12}px` }}
          onClick={() => setSelectedFolderPath(folder.relativePath || undefined)}
        >
          <input
            type="radio"
            name="folder"
            checked={isSelected}
            onChange={() => setSelectedFolderPath(folder.relativePath || undefined)}
            className="mr-2"
            onClick={(e) => e.stopPropagation()}
          />
          <svg className="w-4 h-4 mr-2 text-blue-500" fill="currentColor" viewBox="0 0 20 20">
            <path d="M2 6a2 2 0 012-2h5l2 2h5a2 2 0 012 2v6a2 2 0 01-2 2H4a2 2 0 01-2-2V6z" />
          </svg>
          <span className="text-gray-700">{folder.name}</span>
        </div>
        
        {/* Subfolders */}
        {folder.subFolders.map((subFolder) => renderFolderTree(subFolder, level + 1))}
      </div>
    );
  };

  // Get content preview
  const contentPreview = content.length > 200 ? `${content.slice(0, 200)}...` : content;

  // Calculate word count
  const wordCount = content.trim().split(/\s+/).length;

  if (!isOpen) return null;

  const canProceed = !fileNameError && fileName.trim().length > 0;
  const folderDisplayName = selectedFolderPath ? 
    folderTree?.relativePath === selectedFolderPath ? folderTree.name : 
    selectedFolderPath.split('/').pop() : 'Root';

  const dialogMarkup = (
    <div className="fixed inset-0 z-[9999] flex items-center justify-center bg-black bg-opacity-50">
      <div className="bg-white rounded-lg shadow-xl max-w-2xl w-full mx-4 max-h-[80vh] flex flex-col">
        {/* Header */}
        <div className="flex items-center justify-between p-6 border-b border-gray-200">
          <h2 className="text-xl font-semibold text-gray-900 flex items-baseline gap-2">
            Save Assistant Response
            {notebookTitle && (
              <span className="text-sm font-normal text-gray-500">to {notebookTitle}</span>
            )}
          </h2>
          <button
            onClick={handleClose}
            disabled={isSaving}
            className="text-gray-400 hover:text-gray-600 disabled:opacity-50"
            aria-label="Close"
          >
            <svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

        {/* Content */}
        <div className="p-6 flex-1 overflow-hidden flex flex-col">
          {/* Content Preview */}
          <div className="mb-6">
            <h3 className="text-sm font-medium text-gray-700 mb-3">Content Preview</h3>
            <div className="bg-gray-50 border border-gray-200 rounded-md p-4 text-sm text-gray-700 max-h-32 overflow-y-auto">
              <pre className="whitespace-pre-wrap font-sans">{contentPreview}</pre>
            </div>
            <div className="mt-2 text-xs text-gray-500">
              {wordCount} words • {content.length} characters
            </div>
          </div>

          {/* File Name Input */}
          <div className="mb-6">
            <label htmlFor="fileName" className="block text-sm font-medium text-gray-700 mb-2">
              File Name
            </label>
            <input
              type="text"
              id="fileName"
              value={fileName}
              onChange={handleFileNameChange}
              className={`w-full px-3 py-2 border rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 ${
                fileNameError ? 'border-red-300 focus:border-red-500' : 'border-gray-300'
              }`}
              placeholder="Enter file name..."
              disabled={isSaving}
            />
            {fileNameError && (
              <p className="mt-1 text-sm text-red-600">{fileNameError}</p>
            )}
          </div>

          {/* Folder Selection */}
          {folderTree && (
            <div className="mb-6">
              <h3 className="text-sm font-medium text-gray-700 mb-3">
                Select Folder (Optional)
              </h3>
              <div className="border border-gray-200 rounded-md max-h-40 overflow-y-auto">
                {/* Root folder option */}
                <div
                  className={`flex items-center py-2 px-3 text-sm cursor-pointer hover:bg-gray-50 ${
                    selectedFolderPath === undefined ? 'bg-blue-50 border-l-2 border-blue-500' : ''
                  }`}
                  onClick={() => setSelectedFolderPath(undefined)}
                >
                  <input
                    type="radio"
                    name="folder"
                    checked={selectedFolderPath === undefined}
                    onChange={() => setSelectedFolderPath(undefined)}
                    className="mr-2"
                    onClick={(e) => e.stopPropagation()}
                  />
                  <svg className="w-4 h-4 mr-2 text-blue-500" fill="currentColor" viewBox="0 0 20 20">
                    <path d="M2 6a2 2 0 012-2h5l2 2h5a2 2 0 012 2v6a2 2 0 01-2 2H4a2 2 0 01-2-2V6z" />
                  </svg>
                  <span className="text-gray-700 font-medium">Root</span>
                </div>
                
                {/* Folder tree */}
                {folderTree.subFolders.map((subFolder) => renderFolderTree(subFolder))}
              </div>
              <div className="mt-2 text-xs text-gray-500">
                Selected: <span>{folderDisplayName}</span>
              </div>
            </div>
          )}
        </div>

        {/* Footer */}
        <div className="p-6 border-t border-gray-200 flex justify-between items-center">
          <div className="text-sm text-gray-600">
            {assistantName && (
              <span>From {assistantName}</span>
            )}
          </div>
          <div className="flex space-x-3">
            <button
              onClick={handleClose}
              disabled={isSaving}
              className="px-4 py-2 text-sm border border-gray-300 rounded-md hover:bg-gray-50 disabled:opacity-50"
            >
              Cancel
            </button>
            <button
              onClick={handleSave}
              disabled={!canProceed || isSaving || disabled}
              className="px-4 py-2 text-sm bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:opacity-50 flex items-center"
            >
              {isSaving ? (
                <>
                  <svg className="w-4 h-4 mr-2 animate-spin" fill="none" viewBox="0 0 24 24">
                    <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                    <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z" />
                  </svg>
                  Saving...
                </>
              ) : (
                'Save File'
              )}
            </button>
          </div>
        </div>
      </div>
    </div>
  );

  return createPortal(dialogMarkup, document.body);
};
