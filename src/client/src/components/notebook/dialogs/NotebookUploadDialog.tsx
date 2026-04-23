import { useState, useRef, useEffect } from 'react';
import { createPortal } from 'react-dom';
import { NotebookFolderTreeDto } from '../../../types/notebook';
import { FolderTreeDto, ProjectContentFile } from '../../../types/project';
import { FileIcon } from '../../common/FileIcon';
import { useToast } from '../../common/Toast';

interface NotebookUploadDialogProps {
  isOpen: boolean;
  onClose: () => void;
  onUpload: (files: File[], folderId?: string) => Promise<void>;
  onCopyFromProject?: (files: { fileId: string; version?: number }[], folderId?: string) => Promise<void>;
  disabled?: boolean;
  initialFolderId?: string;
  folderTree?: NotebookFolderTreeDto | null;
  projectFolderTree?: FolderTreeDto | null;
}

type TabType = 'local' | 'project';

interface SelectedProjectFile {
  file: ProjectContentFile;
  version?: number;
}

export const NotebookUploadDialog: React.FC<NotebookUploadDialogProps> = ({
  isOpen,
  onClose,
  onUpload,
  onCopyFromProject,
  disabled = false,
  initialFolderId,
  folderTree,
  projectFolderTree
}) => {
  const [activeTab, setActiveTab] = useState<TabType>('local');
  const [selectedFiles, setSelectedFiles] = useState<File[]>([]);
  const [selectedProjectFiles, setSelectedProjectFiles] = useState<SelectedProjectFile[]>([]);
  const [isUploading, setIsUploading] = useState(false);
  
  const fileInputRef = useRef<HTMLInputElement>(null);
  const { showToast } = useToast();

  const handleClose = () => {
    if (!isUploading) {
      setSelectedFiles([]);
      setSelectedProjectFiles([]);
      onClose();
    }
  };

  const handleUpload = async () => {
    if (activeTab === 'local') {
      if (selectedFiles.length === 0) return;
      
      setIsUploading(true);
      try {
        await onUpload(selectedFiles, initialFolderId);
        
        // Show success toast
        showToast({
          type: 'success',
          title: `${selectedFiles.length} file${selectedFiles.length > 1 ? 's' : ''} uploaded successfully`,
          duration: 3000
        });
        
        setSelectedFiles([]);
        onClose();
      } catch (error) {
        console.error('Upload failed:', error);
        
        // Show error toast
        showToast({
          type: 'error',
          title: 'Upload failed',
          message: 'Could not upload the selected files. Please try again.',
          duration: 5000
        });
      } finally {
        setIsUploading(false);
      }
    } else {
      if (selectedProjectFiles.length === 0 || !onCopyFromProject) return;
      
      setIsUploading(true);
      try {
        const filesToCopy = selectedProjectFiles.map(spf => ({
          fileId: spf.file.id,
          version: spf.version
        }));
        await onCopyFromProject(filesToCopy, initialFolderId);
        
        // Show success toast
        showToast({
          type: 'success',
          title: `${selectedProjectFiles.length} file${selectedProjectFiles.length > 1 ? 's' : ''} copied successfully`,
          message: `Files have been copied from the project to the notebook.`,
          duration: 3000
        });
        
        setSelectedProjectFiles([]);
        onClose();
      } catch (error) {
        console.error('Copy from project failed:', error);
        
        // Show error toast
        showToast({
          type: 'error',
          title: 'Copy failed',
          message: 'Could not copy the selected files from the project. Please try again.',
          duration: 5000
        });
      } finally {
        setIsUploading(false);
      }
    }
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

  const handleProjectFileToggle = (file: ProjectContentFile) => {
    setSelectedProjectFiles(current => {
      const exists = current.find(sf => sf.file.id === file.id);
      if (exists) {
        return current.filter(sf => sf.file.id !== file.id);
      } else {
        return [...current, { file, version: file.latestVersion }];
      }
    });
  };

  const isProjectFileSelected = (fileId: string) => {
    return selectedProjectFiles.some(sf => sf.file.id === fileId);
  };

  const formatFileSize = (bytes: number): string => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
  };

  // Find the selected folder in the folder tree
  const findFolderByPath = (tree: NotebookFolderTreeDto | null, targetPath?: string): NotebookFolderTreeDto | null => {
    if (!tree || !targetPath) return null;
    
    if (tree.relativePath === targetPath) return tree;
    
    for (const subFolder of tree.subFolders) {
      const found = findFolderByPath(subFolder, targetPath);
      if (found) return found;
    }
    
    return null;
  };

  const selectedFolder = initialFolderId ? findFolderByPath(folderTree || null, initialFolderId) : null;
  const folderDisplayName = selectedFolder ? selectedFolder.relativePath || selectedFolder.name : 'Root';

  // Render project folder tree
  const renderProjectFolderTree = (folder: FolderTreeDto, level: number = 0): React.ReactNode => {
    return (
      <div key={folder.id || folder.relativePath} className="select-none">
        {/* Folder Header */}
        {level > 0 && (
          <div
            className="flex items-center py-1 px-2 text-sm hover:bg-gray-50"
            style={{ paddingLeft: `${level * 16 + 8}px` }}
          >
            <svg className="w-4 h-4 mr-2 text-blue-500" fill="currentColor" viewBox="0 0 20 20">
              <path d="M2 6a2 2 0 012-2h5l2 2h5a2 2 0 012 2v6a2 2 0 01-2 2H4a2 2 0 01-2-2V6z" />
            </svg>
            <span className="text-gray-700">{folder.name}</span>
          </div>
        )}
        
        {/* Files in this folder */}
        {folder.files.map((file) => (
          <div
            key={file.id}
            className={`flex items-center py-1 px-2 text-sm cursor-pointer hover:bg-gray-50 ${
              isProjectFileSelected(file.id) ? 'bg-blue-50 border-l-2 border-blue-500' : ''
            }`}
            style={{ paddingLeft: `${(level + 1) * 16 + 8}px` }}
            onClick={() => handleProjectFileToggle(file)}
          >
            <input
              type="checkbox"
              checked={isProjectFileSelected(file.id)}
              onChange={() => handleProjectFileToggle(file)}
              className="mr-2"
              onClick={(e) => e.stopPropagation()}
            />
            <FileIcon contentType={file.contentType} fileName={file.fileName} />
            <span className="flex-1 truncate" title={file.fileName}>{file.fileName}</span>
            <span className="ml-2 text-xs text-gray-500">
              {formatFileSize(file.fileSize)}
            </span>
            {file.latestVersion && file.latestVersion > 1 && (
              <span className="ml-2 text-xs bg-gray-200 text-gray-700 px-1 rounded">
                v{file.latestVersion}
              </span>
            )}
          </div>
        ))}
        
        {/* Subfolders */}
        {folder.subFolders.map((subFolder) => renderProjectFolderTree(subFolder, level + 1))}
      </div>
    );
  };

  if (!isOpen) return null;

  const hasProjectFiles = projectFolderTree && onCopyFromProject;
  const canProceed = activeTab === 'local' ? selectedFiles.length > 0 : selectedProjectFiles.length > 0;
  const fileCount = activeTab === 'local' ? selectedFiles.length : selectedProjectFiles.length;

  const dialogMarkup = (
    <div className="fixed inset-0 z-[9999] flex items-center justify-center bg-black bg-opacity-50">
      <div className="bg-white rounded-lg shadow-xl max-w-2xl w-full mx-4 max-h-[80vh] flex flex-col">
        {/* Header */}
        <div className="flex items-center justify-between p-6 border-b border-gray-200">
          <h2 className="text-xl font-semibold text-gray-900 flex items-baseline gap-2">
            Add Files to Notebook
            <span className="text-sm font-normal text-gray-500">to {folderDisplayName}</span>
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

        {/* Tabs */}
        {hasProjectFiles && (
          <div className="flex border-b border-gray-200">
            <button
              onClick={() => setActiveTab('local')}
              className={`px-6 py-3 text-sm font-medium border-b-2 ${
                activeTab === 'local'
                  ? 'border-blue-500 text-blue-600'
                  : 'border-transparent text-gray-500 hover:text-gray-700'
              }`}
            >
              Upload Local Files
            </button>
            <button
              onClick={() => setActiveTab('project')}
              className={`px-6 py-3 text-sm font-medium border-b-2 ${
                activeTab === 'project'
                  ? 'border-blue-500 text-blue-600'
                  : 'border-transparent text-gray-500 hover:text-gray-700'
              }`}
            >
              Select Project Files
            </button>
          </div>
        )}

        {/* Content */}
        <div className="p-6 flex-1 overflow-hidden flex flex-col">
          {activeTab === 'local' ? (
            <>
              <h3 className="text-sm font-medium text-gray-700 mb-3">Select Files from Your Computer</h3>
              
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
            </>
          ) : (
            <>
              <h3 className="text-sm font-medium text-gray-700 mb-3">Select Files from Current Project</h3>
              
              {projectFolderTree ? (
                <div className="flex-1 overflow-hidden flex flex-col">
                  {selectedProjectFiles.length > 0 && (
                    <div className="mb-3 p-2 bg-blue-50 border border-blue-200 rounded">
                      <span className="text-sm text-blue-800 font-medium">
                        {selectedProjectFiles.length} file{selectedProjectFiles.length > 1 ? 's' : ''} selected
                      </span>
                    </div>
                  )}
                  
                  <div className="flex-1 border border-gray-200 rounded-md overflow-y-auto">
                    {renderProjectFolderTree(projectFolderTree)}
                  </div>
                </div>
              ) : (
                <div className="flex-1 flex items-center justify-center text-gray-500">
                  <div className="text-center">
                    <svg className="w-12 h-12 mx-auto mb-4 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 21h10a2 2 0 002-2V9.414a1 1 0 00-.293-.707l-5.414-5.414A1 1 0 0012.586 3H7a2 2 0 00-2 2v14a2 2 0 002 2z" />
                    </svg>
                    <p>No project files available</p>
                  </div>
                </div>
              )}
            </>
          )}
        </div>

        {/* Footer */}
        <div className="p-6 border-t border-gray-200 flex items-center justify-between">
          <div />
          <div className="text-sm text-gray-600">
            {fileCount > 0 && (
              `${fileCount} file${fileCount > 1 ? 's' : ''} ${activeTab === 'local' ? 'selected' : 'to copy'}`
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
              disabled={!canProceed || isUploading || disabled}
              className="px-4 py-2 text-sm bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:opacity-50 flex items-center"
            >
              {isUploading ? (
                <>
                  <svg className="w-4 h-4 mr-2 animate-spin" fill="none" viewBox="0 0 24 24">
                    <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                    <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z" />
                  </svg>
                  {activeTab === 'local' ? 'Uploading...' : 'Copying...'}
                </>
              ) : (
                activeTab === 'local' ? 'Upload Files' : 'Copy Files'
              )}
            </button>
          </div>
        </div>
      </div>
    </div>
  );

  return createPortal(dialogMarkup, document.body);
};
