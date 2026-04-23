import React, { useState, useEffect } from 'react';
import { PublishToProjectDialogProps } from '../../../types/notebook';
import { ProjectFolderDto } from '../../../types/project';
import { FiUpload, FiX, FiCheck, FiAlertCircle } from 'react-icons/fi';

export const PublishToProjectDialog: React.FC<PublishToProjectDialogProps> = ({
  isOpen,
  onClose,
  notebookFiles,
  projectFolders,
  projectTitle,
  onPublish,
  onComplete,
  originFileInfoMap
}) => {
  const [selectedFolderId, setSelectedFolderId] = useState<string | undefined>();
  const [isPublishing, setIsPublishing] = useState(false);
  const [publishProgress, setPublishProgress] = useState<Map<string, 'pending' | 'publishing' | 'success' | 'error'>>(new Map());
  const [publishErrors, setPublishErrors] = useState<Map<string, string>>(new Map());

  const isBatchPublish = notebookFiles.length > 1;

  // For batch: show folder selection only if ALL files have no lineage
  // For single file: show folder selection if the file has no lineage
  const filesWithoutLineage = notebookFiles.filter(f => !f.originContentFileVersionId);
  const filesWithLineage = notebookFiles.filter(f => f.originContentFileVersionId);
  const showFolderSelection = filesWithoutLineage.length > 0;

  // Reset progress when dialog opens with new files
  useEffect(() => {
    if (isOpen && notebookFiles.length > 0) {
      const initial = new Map<string, 'pending' | 'publishing' | 'success' | 'error'>();
      notebookFiles.forEach(f => initial.set(f.id, 'pending'));
      setPublishProgress(initial);
      setPublishErrors(new Map());
    }
  }, [isOpen, notebookFiles]);

  const handlePublish = async () => {
    if (notebookFiles.length === 0) return;
    setIsPublishing(true);
    
    let hasError = false;
    const newProgress = new Map(publishProgress);
    const newErrors = new Map(publishErrors);
    const publishedFileIds: string[] = [];

    for (const file of notebookFiles) {
      newProgress.set(file.id, 'publishing');
      setPublishProgress(new Map(newProgress));

      try {
        const hasLineage = file.originContentFileVersionId != null;
        const contentFileId = await onPublish({
          notebookFileId: file.id,
          // Only include destinationFolderId for files without lineage
          destinationFolderId: !hasLineage ? selectedFolderId : undefined,
        });
        publishedFileIds.push(contentFileId);
        newProgress.set(file.id, 'success');
        setPublishProgress(new Map(newProgress));
      } catch (error) {
        console.error(`Failed to publish file ${file.fileName}:`, error);
        newProgress.set(file.id, 'error');
        newErrors.set(file.id, error instanceof Error ? error.message : 'Failed to publish');
        setPublishProgress(new Map(newProgress));
        setPublishErrors(new Map(newErrors));
        hasError = true;
      }
    }

    setIsPublishing(false);
    
    // Only close and call onComplete if all succeeded
    if (!hasError) {
      onClose();
      // Call onComplete with all published file IDs after closing
      if (onComplete && publishedFileIds.length > 0) {
        onComplete(publishedFileIds);
      }
    }
  };

  useEffect(() => {
    if (!isOpen) return;
    const handleKeyDown = (e: KeyboardEvent) => {
        if (e.key === 'Escape' && !isPublishing) onClose();
        if (e.key === 'Enter' && !isPublishing) {
            const target = e.target as HTMLElement;
            if (target.tagName === 'BUTTON') return;
            
            e.preventDefault();
            handlePublish();
        }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isOpen, isPublishing, onClose, handlePublish]);

  if (!isOpen || notebookFiles.length === 0) {
    return null;
  }

  const buildFolderOptions = (folders: ProjectFolderDto[]): { id: string; path: string }[] => {
    const options: { id: string; path: string }[] = [];
    
    // Helper function to build path for a folder
    const buildPath = (folderId: string, visited = new Set<string>()): string => {
      // Prevent infinite recursion
      if (visited.has(folderId)) return '';
      visited.add(folderId);
      
      const folder = folders.find(f => f.id === folderId);
      if (!folder) return '';
      
      if (!folder.parentFolderId) {
        return `${projectTitle}/${folder.name}`;
      }
      
      const parentPath = buildPath(folder.parentFolderId, visited);
      return parentPath ? `${parentPath}/${folder.name}` : `${projectTitle}/${folder.name}`;
    };
    
    // Sort folders alphabetically by name and add to options
    const sortedFolders = [...folders].sort((a, b) => a.name.localeCompare(b.name));
    
    for (const folder of sortedFolders) {
      const path = buildPath(folder.id);
      if (path) {
        options.push({ id: folder.id, path });
      }
    }
    
    // Sort options by path for better UX
    options.sort((a, b) => a.path.localeCompare(b.path));
    
    return options;
  };

  const getStatusIcon = (fileId: string) => {
    const status = publishProgress.get(fileId);
    switch (status) {
      case 'publishing':
        return <span className="animate-spin inline-block w-4 h-4 border-2 border-blue-500 border-t-transparent rounded-full" />;
      case 'success':
        return <FiCheck className="text-green-500" />;
      case 'error':
        return <FiAlertCircle className="text-red-500" />;
      default:
        return null;
    }
  };

  const allSucceeded = Array.from(publishProgress.values()).every(s => s === 'success');
  const someStarted = Array.from(publishProgress.values()).some(s => s !== 'pending');

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 z-50 flex justify-center items-center">
      <div className="bg-white rounded-lg p-6 w-full max-w-md max-h-[80vh] flex flex-col">
        <div className="flex justify-between items-center mb-4">
          <h2 className="text-lg font-semibold">Publish to Project</h2>
          <button onClick={onClose} className="p-1 rounded-full hover:bg-gray-200" disabled={isPublishing}>
            <FiX />
          </button>
        </div>

        <div className="mb-4">
          <p>You are about to publish {isBatchPublish ? `${notebookFiles.length} files` : 'the following file'}:</p>
        </div>

        {/* File list */}
        <div className={`mb-4 ${isBatchPublish ? 'max-h-40 overflow-y-auto border rounded-md p-2' : ''}`}>
          {notebookFiles.map(file => {
            const originInfo = originFileInfoMap?.get(file.id);
            const hasLineage = file.originContentFileVersionId != null;
            const error = publishErrors.get(file.id);
            
            return (
              <div key={file.id} className={`flex items-center justify-between py-1 ${isBatchPublish ? 'border-b last:border-b-0' : ''}`}>
                <div className="flex-1 min-w-0">
                  <p className={`font-semibold truncate ${error ? 'text-red-600' : ''}`} title={file.fileName}>
                    {file.fileName}
                  </p>
                  {hasLineage && originInfo && (
                    <p className="text-xs text-blue-600 truncate" title={originInfo.folderPath}>
                      → {originInfo.folderPath}
                    </p>
                  )}
                  {error && (
                    <p className="text-xs text-red-500">{error}</p>
                  )}
                </div>
                <div className="ml-2 flex-shrink-0">
                  {getStatusIcon(file.id)}
                </div>
              </div>
            );
          })}
        </div>

        {/* Lineage info for batch */}
        {isBatchPublish && filesWithLineage.length > 0 && (
          <div className="mb-4 p-3 bg-blue-50 rounded-md">
            <p className="text-sm text-blue-800">
              <strong>{filesWithLineage.length} file{filesWithLineage.length > 1 ? 's' : ''}</strong> originated from the project and will be published as new versions to their original locations.
            </p>
          </div>
        )}

        {/* Lineage info for single file */}
        {!isBatchPublish && filesWithLineage.length === 1 && originFileInfoMap?.get(filesWithLineage[0].id) && (
          <div className="mb-4 p-3 bg-blue-50 rounded-md">
            <p className="text-sm text-blue-800">
              <strong>Creating new version:</strong> This file originated from the project and will be published as a new version of the original file.
            </p>
            <p className="text-sm text-blue-600 mt-1">
              Original location: {originFileInfoMap.get(filesWithLineage[0].id)?.folderPath}
            </p>
          </div>
        )}

        {showFolderSelection && (
          <div className="mb-4">
            <label htmlFor="folder-select" className="block text-sm font-medium text-gray-700 mb-1">
              Destination Folder {filesWithoutLineage.length < notebookFiles.length ? `(for ${filesWithoutLineage.length} new file${filesWithoutLineage.length > 1 ? 's' : ''})` : '(optional)'}
            </label>
            <select
              id="folder-select"
              value={selectedFolderId || ''}
              onChange={(e) => setSelectedFolderId(e.target.value || undefined)}
              className="w-full p-2 border border-gray-300 rounded-md"
              disabled={isPublishing}
            >
              <option value="">{projectTitle || 'Project Root'}</option>
              {buildFolderOptions(projectFolders).map(option => (
                <option key={option.id} value={option.id}>
                  {option.path}
                </option>
              ))}
            </select>
            <p className="text-xs text-gray-500 mt-1">
              If no folder is selected, {filesWithoutLineage.length > 1 ? 'files' : 'the file'} will be published to {projectTitle || 'the project root'}.
            </p>
          </div>
        )}

        {!showFolderSelection && !isBatchPublish && (
          <div className="mb-4 p-3 bg-gray-50 rounded-md">
            <p className="text-sm text-gray-700">
              This file will be published as a new version to its original location in the project.
            </p>
          </div>
        )}
        
        <div className="flex justify-end space-x-4 mt-auto">
          <button
            onClick={onClose}
            className="px-4 py-2 bg-gray-200 rounded-md hover:bg-gray-300"
            disabled={isPublishing}
          >
            {allSucceeded && someStarted ? 'Close' : 'Cancel'}
          </button>
          {!allSucceeded && (
            <button
              onClick={handlePublish}
              className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 flex items-center"
              disabled={isPublishing}
            >
              <FiUpload className="mr-2" />
              {isPublishing ? 'Publishing...' : `Publish${isBatchPublish ? ` ${notebookFiles.length} Files` : ''}`}
            </button>
          )}
        </div>
      </div>
    </div>
  );
};
