import React, { useEffect, useState, useCallback } from 'react';
import { useParams, useSearchParams } from 'react-router-dom';
import { notebookFilesApi } from '../services/notebookFiles';
import { NotebookFileDto } from '../types/notebook';
import { FilePreviewOverlay } from '../components/notebook/content/FilePreviewOverlay';
import LoadingSpinner from '../components/LoadingSpinner';

const FilePreviewPage: React.FC = () => {
  const { projectId, notebookId } = useParams<{ projectId: string; notebookId: string }>();
  const [searchParams, setSearchParams] = useSearchParams();
  const path = searchParams.get('path');
  
  // Handle navigation to a different file by updating URL
  const handleNavigate = useCallback((newPath: string) => {
    console.log('[FilePreviewPage] handleNavigate called:', newPath);
    setSearchParams({ path: newPath });
  }, [setSearchParams]);
  
  const [file, setFile] = useState<NotebookFileDto | null>(null);
  const [allFiles, setAllFiles] = useState<NotebookFileDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const fetchFile = async () => {
      if (!projectId || !notebookId || !path) {
        setError('Missing required parameters');
        setLoading(false);
        return;
      }

      try {
        setLoading(true);
        // We need the file DTO. Currently we fetch all files and find the one matching the path.
        // This is not ideal for huge notebooks but works with existing API.
        const files = await notebookFilesApi.listFiles(projectId, notebookId);
        setAllFiles(files);
        const foundFile = files.find(f => f.relativePath === path || f.id === path); // Check ID too just in case
        
        if (foundFile) {
          setFile(foundFile);
        } else {
          setError('File not found');
        }
      } catch (err: any) {
        console.error('Failed to load file info:', err);
        setError(err.message || 'Failed to load file information');
      } finally {
        setLoading(false);
      }
    };

    fetchFile();
  }, [projectId, notebookId, path]);

  // Check if a file exists at a given path (for HTML resource resolution)
  const fileExists = useCallback((filePath: string): boolean => {
    return allFiles.some(f => f.relativePath === filePath);
  }, [allFiles]);

  if (loading) {
    return <LoadingSpinner message="Loading file preview..." />;
  }

  if (error) {
    return (
      <div className="flex flex-col items-center justify-center h-screen bg-gray-100 p-4">
        <div className="bg-white p-6 rounded-lg shadow-md max-w-md w-full text-center">
          <h2 className="text-xl font-semibold text-red-600 mb-2">Error</h2>
          <p className="text-gray-700">{error}</p>
          <button 
            onClick={() => window.close()}
            className="mt-4 px-4 py-2 bg-gray-200 text-gray-800 rounded hover:bg-gray-300"
          >
            Close Window
          </button>
        </div>
      </div>
    );
  }

  if (!file || !projectId || !notebookId) {
    return null;
  }

  return (
    <div className="h-screen w-screen bg-black">
      <FilePreviewOverlay
        file={file}
        projectId={projectId}
        notebookId={notebookId}
        onClose={() => window.close()}
        isStandalone={true}
        onNavigate={handleNavigate}
        fileExists={fileExists}
      />
    </div>
  );
};

export default FilePreviewPage;

