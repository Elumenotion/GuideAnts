import React, { useState, useEffect } from 'react';
import { NotebookFileDto } from '../../../types/notebook';
import { notebookFilesApi } from '../../../services/notebookFiles';
import MarkdownViewer from '../../common/MarkdownViewer';
import TextViewer from '../../common/TextViewer';
import { ImageViewer } from '../../common/ImageViewer';
import PdfViewer from '../../common/PdfViewer';
import VideoPlayer from '../../common/VideoPlayer';
import AudioPlayer from '../../common/AudioPlayer';
import LoadingSpinner from '../../LoadingSpinner';
import { FileLoadingErrorBoundary } from '../../common/FileLoadingErrorBoundary';

interface InlineFileViewerProps {
  file: NotebookFileDto;
  projectId: string;
  notebookId: string;
}

// Helper function to determine content type from filename
const getContentTypeFromFileName = (fileName: string): string => {
    const extension = fileName.split('.').pop()?.toLowerCase();
    switch (extension) {
        case 'pdf': return 'application/pdf';
        case 'jpg':
        case 'jpeg': return 'image/jpeg';
        case 'png': return 'image/png';
        case 'gif': return 'image/gif';
        case 'webp': return 'image/webp';
        case 'svg': return 'image/svg+xml';
        case 'txt': return 'text/plain';
        case 'md': 
        case 'markdown': return 'text/markdown';
        case 'json': return 'application/json';
        case 'html':
        case 'htm': return 'text/html';
        case 'css': return 'text/css';
        case 'js': return 'application/javascript';
        case 'ts': return 'application/typescript';
        case 'py': return 'text/x-python';
        case 'java': return 'text/x-java';
        case 'c': return 'text/x-c';
        case 'cpp': return 'text/x-c++';
        case 'cs': return 'text/x-csharp';
        case 'xml': return 'application/xml';
        case 'yaml':
        case 'yml': return 'application/x-yaml';
        case 'csv': return 'text/csv';
        case 'mp4':
        case 'mov':
        case 'avi':
        case 'webm': return 'video/mp4';
        case 'mp3':
        case 'wav':
        case 'ogg': return 'audio/mpeg';
        default: return 'application/octet-stream';
    }
};

export const InlineFileViewer: React.FC<InlineFileViewerProps> = ({ file, projectId, notebookId }) => {
  const [content, setContent] = useState<Blob | null>(null);
  const [textContent, setTextContent] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [isLoadingText, setIsLoadingText] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Track state transitions
  React.useEffect(() => {
    console.log('[TELEMETRY] InlineFileViewer state transition', {
      fileId: `${projectId}:${notebookId}:${file.relativePath}`,
      isLoading,
      isLoadingText,
      hasContent: !!content,
      hasTextContent: !!textContent,
      hasError: !!error,
      timestamp: Date.now()
    });
  }, [isLoading, isLoadingText, content, textContent, error, projectId, notebookId, file.relativePath]);

  // Log Electron environment details on component mount
  React.useEffect(() => {
    console.log('[TELEMETRY] InlineFileViewer environment check', {
      isElectron: typeof window !== 'undefined' && (window as any).electron !== undefined,
      userAgent: typeof navigator !== 'undefined' ? navigator.userAgent : 'unknown',
      contextIsolation: typeof process !== 'undefined' ? (process as any).contextIsolated : 'unknown',
      nodeIntegration: typeof process !== 'undefined' && process.versions?.electron ? 'enabled' : 'disabled',
      timestamp: Date.now()
    });
  }, []);

  const contentType = getContentTypeFromFileName(file.fileName);
  const isMarkdown = contentType === 'text/markdown' ||
    contentType === 'text/x-markdown' ||
    file.fileName.toLowerCase().endsWith('.md') ||
    file.fileName.toLowerCase().endsWith('.markdown');
  const isText = contentType.startsWith('text/') || contentType === 'application/json' || contentType === 'application/typescript';
  const isImage = contentType.startsWith('image/');
  const isPdf = contentType === 'application/pdf';
  const isVideo = contentType.startsWith('video/');
  const isAudio = contentType.startsWith('audio/');

  useEffect(() => {
    const fetchContent = async () => {
      const startTime = Date.now();
      const fileId = `${projectId}:${notebookId}:${file.relativePath}`;
      
      console.log('[TELEMETRY] File load started', { 
        fileId, 
        projectId, 
        notebookId, 
        relativePath: file.relativePath,
        fileHash: file.fileHash,
        fileName: file.fileName,
        isMarkdown,
        isText,
        timestamp: startTime 
      });

      try {
        setIsLoading(true);
        setError(null);
        setTextContent(null);
        
        console.log('[TELEMETRY] API call initiated', { 
          fileId,
          timestamp: Date.now() 
        });
        
        const blob = await notebookFilesApi.getNotebookFileContent(projectId, notebookId, file.relativePath, file.fileHash);
        
        console.log('[TELEMETRY] API call completed', { 
          fileId,
          success: true, 
          blobSize: blob?.size,
          blobType: blob?.type,
          loadTime: Date.now() - startTime,
          timestamp: Date.now() 
        });
        
        setContent(blob);

        // If it's text-based content, read as text
        if (isMarkdown || isText) {
          console.log('[TELEMETRY] Text processing started', { 
            fileId,
            contentType: isMarkdown ? 'markdown' : 'text',
            timestamp: Date.now() 
          });
          
          setIsLoadingText(true);
          const text = await blob.text();
          
          console.log('[TELEMETRY] Text processing completed', { 
            fileId,
            textLength: text?.length,
            processingTime: Date.now() - startTime,
            timestamp: Date.now() 
          });
          
          setTextContent(text);
          setIsLoadingText(false);
        }
        
        console.log('[TELEMETRY] File load completed successfully', { 
          fileId,
          totalTime: Date.now() - startTime,
          timestamp: Date.now() 
        });
        
      } catch (err: any) {
        console.log('[TELEMETRY] File load failed', { 
          fileId,
          error: err.message,
          errorType: err.constructor.name,
          loadTime: Date.now() - startTime,
          timestamp: Date.now() 
        });
        
        setError(err.message || 'Failed to load file content.');
      } finally {
        console.log('[TELEMETRY] Loading state cleared', { 
          fileId,
          timestamp: Date.now() 
        });
        
        setIsLoading(false);
      }
    };

    fetchContent();
  }, [file, projectId, notebookId, isMarkdown, isText]);

  const renderContent = () => {
    if (isLoading) {
      return (
        <div className="flex items-center justify-center h-64">
          <LoadingSpinner message="Loading file content..." />
        </div>
      );
    }

    if (error) {
      return (
        <div className="flex items-center justify-center h-64 text-red-500">
          <div className="text-center">
            <p>{error}</p>
            <p className="text-sm text-gray-600 mt-2">
              Unable to display this file
            </p>
          </div>
        </div>
      );
    }

    if (!content) {
      return null;
    }

    // Handle Markdown
    if (isMarkdown) {
      if (isLoadingText || textContent === null) {
        return (
          <div className="flex items-center justify-center h-64">
            <LoadingSpinner message="Processing markdown..." />
          </div>
        );
      }
      // Extract directory from file path for resolving relative URLs (e.g., "Output/guide.md" -> "Output")
      const basePath = file.relativePath.includes('/') 
        ? file.relativePath.substring(0, file.relativePath.lastIndexOf('/'))
        : undefined;
      return <MarkdownViewer text={textContent} className="h-full p-4 overflow-auto" inlineMode={true} projectId={projectId} notebookId={notebookId} basePath={basePath} />;
    }

    // Handle text files
    if (isText) {
      if (isLoadingText || textContent === null) {
        return (
          <div className="flex items-center justify-center h-64">
            <LoadingSpinner message="Processing text content..." />
          </div>
        );
      }
      return <TextViewer text={textContent} inlineMode={true} />;
    }

    // Handle images
    if (isImage) {
      return <ImageViewer src={URL.createObjectURL(content)} alt={file.fileName} inlineMode={true} />;
    }

    // Handle PDFs
    if (isPdf) {
      return <PdfViewer blob={content} inlineMode={true} />;
    }

    // Handle videos
    if (isVideo) {
      return <VideoPlayer blob={content} inlineMode={true} />;
    }

    // Handle audio
    if (isAudio) {
      return <AudioPlayer blob={content} inlineMode={true} />;
    }

    // Fallback for unsupported file types
    return (
      <div className="flex items-center justify-center h-64 text-gray-500">
        <div className="text-center">
          <p className="text-lg font-medium">Preview not available</p>
          <p className="text-sm mt-2">This file type is not supported for preview</p>
          <p className="text-xs text-gray-400 mt-1">{file.fileName}</p>
        </div>
      </div>
    );
  };

  return (
    <FileLoadingErrorBoundary 
      fileId={`${projectId}:${notebookId}:${file.relativePath}`}
      componentName="InlineFileViewer"
    >
      <div className="h-full w-full flex flex-col mt-4 p-8">
        {/* File content */}
        <div className="flex-1 overflow-auto">
          {renderContent()}
        </div>
      </div>
    </FileLoadingErrorBoundary>
  );
};