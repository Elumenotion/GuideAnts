import React, { useState, useEffect, useMemo, useCallback, useRef } from 'react';
import { NotebookFileDto } from '../../../types/notebook';
import { notebookFilesApi } from '../../../services/notebookFiles';
import { NotebookFileMarkdownShadowDto, MarkdownExtractionStatus } from '../../../types/api';
import { isMarkdownExtractionSupported } from '../../../utils/markdownUtils';
import { resolveHtmlResources, cleanupBlobUrls } from '../../../utils/htmlResourceResolver';
import { useHref } from 'react-router-dom';
import { VscChromeClose, VscLinkExternal } from 'react-icons/vsc';
import { ImageViewer } from '../../common/ImageViewer';
import PdfViewer from '../../common/PdfViewer';
import MarkdownViewer from '../../common/MarkdownViewer';
import TextViewer from '../../common/TextViewer';
import VideoPlayer from '../../common/VideoPlayer';
import AudioPlayer from '../../common/AudioPlayer';
import LoadingSpinner from '../../LoadingSpinner';
import FullScreenEditor from '../conversations/FullScreenEditor';
import { FaRegEdit, FaExpandAlt, FaCompress, FaDownload } from 'react-icons/fa';
// CsvViewer and CodeViewer will be added back if found.

interface FilePreviewOverlayProps {
  file: NotebookFileDto;
  projectId: string;
  notebookId: string;
  onClose: () => void;
  isStandalone?: boolean;
  /** When true, renders inline within parent container instead of as a fixed overlay */
  isEmbedded?: boolean;
  /** Called when user navigates to a different file via link click. Parent should update the file prop. */
  onNavigate?: (path: string) => void;
  /** Function to check if a file exists at a given path (for resolving root-relative URLs in HTML) */
  fileExists?: (path: string) => boolean;
}



// Helper function to determine content type from filename (similar to project view)
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
        case 'doc':
        case 'docx': return 'application/vnd.openxmlformats-officedocument.wordprocessingml.document';
        case 'xls':
        case 'xlsx': return 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
        case 'ppt':
        case 'pptx': return 'application/vnd.openxmlformats-officedocument.presentationml.presentation';
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
        case 'xml': return 'application/xml';
        case 'csv': return 'text/csv';
        case 'puml': return 'text/plain';
        case 'mp3': return 'audio/mpeg';
        case 'wav': return 'audio/wav';
        case 'ogg': return 'audio/ogg';
        case 'mp4': return 'video/mp4';
        case 'webm': return 'video/webm';
        default: return 'application/octet-stream';
    }
};

export const FilePreviewOverlay: React.FC<FilePreviewOverlayProps> = ({ file, projectId, notebookId, onClose, isStandalone, isEmbedded, onNavigate, fileExists }) => {
  const [content, setContent] = useState<Blob | null>(null);
  const [textContent, setTextContent] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [isLoadingText, setIsLoadingText] = useState(false);
  const [error, setError] = useState<string | null>(null);
  
  // Tab and markdown state
  const [activeTab, setActiveTab] = useState<'original' | 'markdown'>('original');
  const [markdownShadow, setMarkdownShadow] = useState<NotebookFileMarkdownShadowDto | null>(null);
  const [markdownContent, setMarkdownContent] = useState<string | null>(null);
  const [isLoadingMarkdown, setIsLoadingMarkdown] = useState(false);
  const [markdownError, setMarkdownError] = useState<string | null>(null);
  const [hasAutoSwitched, setHasAutoSwitched] = useState(false);
  
  // Full-screen state
  const [isFullScreen, setIsFullScreen] = useState(false);

  // Markdown edit state
  const [isEditingMd, setIsEditingMd] = useState(false);
  const [mdEditorContent, setMdEditorContent] = useState<string>('');
  const [mdEditorLoading, setMdEditorLoading] = useState<boolean>(false);
  const [mdEditorError, setMdEditorError] = useState<string | undefined>(undefined);

    const previewUrl = useHref(`/projects/${projectId}/notebooks/${notebookId}/files/preview?path=${encodeURIComponent(file.relativePath)}`);
  
    const handleDownload = () => {
        if (!content) return;
        const url = URL.createObjectURL(content);
        const a = document.createElement('a');
        a.href = url;
        a.download = file.fileName;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    };

  // Refs for scroll containers
  const originalScrollRef = useRef<HTMLDivElement>(null);
  const markdownScrollRef = useRef<HTMLDivElement>(null);

  const contentType = getContentTypeFromFileName(file.fileName);
  
  // Check if file supports markdown extraction
  const supportsMarkdownExtraction = useMemo(() => 
    isMarkdownExtractionSupported(file.fileName, contentType), 
    [file.fileName, contentType]
  );

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
        if (e.key === 'Escape' && !isEditingMd) onClose();
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [onClose, isEditingMd]);

  // Handle navigation messages from HTML preview iframe
  useEffect(() => {
    const handleMessage = (e: MessageEvent) => {
      if (e.data?.type !== 'html-preview-navigate') return;
      
      const href = e.data.href as string;
      if (!href) return;
      
      // Get the current base path for resolving relative links
      const currentBasePath = file.relativePath.includes('/') 
        ? file.relativePath.substring(0, file.relativePath.lastIndexOf('/'))
        : '';
      
      // Build the path part (without leading slash, with index.html if needed)
      let pathPart: string;
      
      if (href.startsWith('/')) {
        pathPart = href.substring(1); // Remove leading slash
      } else {
        // Relative path - resolve from current file's directory
        const baseParts = currentBasePath.split('/').filter(Boolean);
        const hrefParts = href.split('/');
        
        // Handle ../ navigation
        while (hrefParts.length > 0 && hrefParts[0] === '..') {
          hrefParts.shift();
          baseParts.pop();
        }
        
        // Remove ./ prefix
        if (hrefParts[0] === '.') hrefParts.shift();
        
        pathPart = [...baseParts, ...hrefParts].join('/');
      }
      
      // Handle empty path (root)
      if (pathPart === '' || pathPart === '/') {
        pathPart = 'index.html';
      }
      
      // Add index.html if path has no extension
      if (!pathPart.match(/\.[^/]+$/)) {
        pathPart = pathPart.replace(/\/$/, '') + '/index.html';
      }
      
      // Resolve the target path
      let targetPath: string | null = null;
      
      if (href.startsWith('/') && discoveredSiteRootRef.current !== null) {
        // Use discovered site root for root-relative paths
        const fullPath = discoveredSiteRootRef.current 
          ? `${discoveredSiteRootRef.current}/${pathPart}` 
          : pathPart;
        if (fileExists && fileExists(fullPath)) {
          targetPath = fullPath;
        }
      }
      
      // If not found via discovered root, walk up ancestors
      if (!targetPath && fileExists) {
        const ancestors = currentBasePath ? currentBasePath.split('/').filter(Boolean) : [];
        
        for (let i = ancestors.length; i >= 0; i--) {
          const potentialRoot = ancestors.slice(0, i).join('/');
          const fullPath = potentialRoot ? `${potentialRoot}/${pathPart}` : pathPart;
          if (fileExists(fullPath)) {
            targetPath = fullPath;
            break;
          }
        }
      }
      
      console.log('[FilePreviewOverlay] Navigation:', { href, pathPart, targetPath, hasFileExists: !!fileExists });
      
      if (onNavigate && targetPath) {
        onNavigate(targetPath);
      } else {
        console.warn('[FilePreviewOverlay] Navigation failed - file not found:', pathPart);
      }
    };
    
    window.addEventListener('message', handleMessage);
    return () => window.removeEventListener('message', handleMessage);
  }, [file.relativePath, onNavigate, fileExists]);

  // Reset state when file changes
  useEffect(() => {
    setActiveTab('original');
    setMarkdownShadow(null);
    setMarkdownContent(null);
    setIsLoadingMarkdown(false);
    setMarkdownError(null);
    setHasAutoSwitched(false);
  }, [file.id]);

  useEffect(() => {
    const fetchContent = async () => {
      try {
        setIsLoading(true);
        setError(null);
        setTextContent(null);
        setHtmlContent(null);
        const blob = await notebookFilesApi.getNotebookFileContent(projectId, notebookId, file.relativePath, file.fileHash);
        setContent(blob);
      } catch (err: any) {
        setError(err.message || 'Failed to load file content.');
      } finally {
        setIsLoading(false);
      }
    };

    fetchContent();
  }, [file, projectId, notebookId]);

  // Fetch markdown shadow info
  useEffect(() => {
    const fetchMarkdownShadow = async () => {
      if (!supportsMarkdownExtraction) {
        setMarkdownShadow(null);
        return;
      }

      try {
        const shadow = await notebookFilesApi.getNotebookFileMarkdownShadow(projectId, notebookId, file.id);
        setMarkdownShadow(shadow);
      } catch (error) {
        console.error('Failed to fetch markdown shadow:', error);
        setMarkdownShadow(null);
      }
    };

    fetchMarkdownShadow();
  }, [file.id, projectId, notebookId, supportsMarkdownExtraction]);

  // Poll for status updates when extraction is in progress
  useEffect(() => {
    if (!markdownShadow || !supportsMarkdownExtraction) return;
    
    if (markdownShadow.status !== MarkdownExtractionStatus.Pending && 
        markdownShadow.status !== MarkdownExtractionStatus.Processing) return;

    const pollInterval = setInterval(async () => {
      try {
        const shadow = await notebookFilesApi.getNotebookFileMarkdownShadow(projectId, notebookId, file.id);
        setMarkdownShadow(shadow);

        if (shadow.status === MarkdownExtractionStatus.Completed || 
            shadow.status === MarkdownExtractionStatus.Failed || 
            shadow.status === MarkdownExtractionStatus.Skipped) {
          clearInterval(pollInterval);
        }
      } catch (error) {
        console.error('Failed to poll markdown shadow status:', error);
      }
    }, 3000);

    return () => clearInterval(pollInterval);
  }, [markdownShadow?.status, file.id, projectId, notebookId, supportsMarkdownExtraction]);

  // Separate effect for loading text content (matching project view)
  useEffect(() => {
    const loadTextContent = async () => {
      if (content && (contentType.startsWith('text/') || contentType === 'application/json' || contentType === 'application/typescript')) {
        try {
          setIsLoadingText(true);
          const text = await content.text();
          setTextContent(text);
        } catch (error) {
          console.error('Failed to read text content:', error);
          setError('Failed to read text content. Please try again.');
        } finally {
          setIsLoadingText(false);
        }
      }
    };

    loadTextContent();
  }, [content, contentType]);

  // Markdown detection (matching project view)
  const isMarkdown = 
    contentType === 'text/markdown' ||
    contentType === 'text/x-markdown' ||
    file.fileName.toLowerCase().endsWith('.md') ||
    file.fileName.toLowerCase().endsWith('.markdown');

  // Check if content is text-based and should use append mode
  const isTextBasedContent = contentType.startsWith('text/') || 
                              contentType === 'application/json' || 
                              contentType === 'application/typescript' ||
                              isMarkdown;

  const openMarkdownEditorFromPreview = async () => {
    if (!isMarkdown) return;
    setMdEditorError(undefined);
    setMdEditorLoading(true);
    try {
      // Try markdown content endpoint first
      try {
        const result = await notebookFilesApi.getNotebookFileMarkdownContent(projectId, notebookId, file.id);
        const text = await result.blob.text();
        setMdEditorContent(text);
      } catch {
        // Fallback to raw file content
        const blob = await notebookFilesApi.getNotebookFileContent(projectId, notebookId, file.relativePath, file.fileHash);
        const text = await blob.text();
        setMdEditorContent(text);
      }
      setIsEditingMd(true);
    } catch (err) {
      setMdEditorError('Failed to load markdown content.');
    } finally {
      setMdEditorLoading(false);
    }
  };

  const saveMarkdownFromPreview = async (newContent: string) => {
    setMdEditorLoading(true);
    setMdEditorError(undefined);
    try {
      // Determine folder path
      const relPath = file.relativePath || file.fileName;
      const folderPath = relPath.includes('/') ? relPath.substring(0, relPath.lastIndexOf('/')) : '';
      const fileName = file.fileName;
      const markdownFile = new File([newContent], fileName, { type: 'text/markdown' });
      await notebookFilesApi.uploadFiles(projectId, notebookId, [markdownFile], folderPath, false);

      // Update local preview state for immediate feedback
      setTextContent(newContent);
      setMarkdownContent(newContent);

      try { window.dispatchEvent(new Event('refresh-notebook-files')); } catch {}
      setIsEditingMd(false);
    } catch (err) {
      setMdEditorError('Failed to save markdown file.');
    } finally {
      setMdEditorLoading(false);
    }
  };

  // Auto-switch to markdown tab for non-previewable files with completed extraction
  useEffect(() => {
    if (!markdownShadow || markdownShadow.status !== MarkdownExtractionStatus.Completed || hasAutoSwitched) {
      return;
    }

    // Check if the original file type can't be previewed
    const isPreviewable = 
      contentType.startsWith('text/') ||
      contentType === 'application/json' ||
      contentType === 'application/typescript' ||
      contentType.startsWith('image/') ||
      contentType.startsWith('application/pdf') ||
      contentType.startsWith('audio/') ||
      contentType.startsWith('video/') ||
      isMarkdown;

    if (!isPreviewable && activeTab === 'original') {
      setActiveTab('markdown');
      setHasAutoSwitched(true);
    }
  }, [markdownShadow, activeTab, hasAutoSwitched, contentType, isMarkdown]);

  // Fetch markdown content when switching to markdown tab
  const fetchMarkdownContent = useCallback(async () => {
    if (!markdownShadow || markdownShadow.status !== MarkdownExtractionStatus.Completed) return;

    try {
      setIsLoadingMarkdown(true);
      setMarkdownError(null);
      
      const result = await notebookFilesApi.getNotebookFileMarkdownContent(
        projectId, 
        notebookId, 
        file.id,
        markdownShadow.contentHash
      );
      const text = await result.blob.text();
      setMarkdownContent(text);
    } catch (error) {
      console.error('Failed to fetch markdown content:', error);
      setMarkdownError('Failed to load markdown content. Please try again.');
    } finally {
      setIsLoadingMarkdown(false);
    }
  }, [markdownShadow?.status, markdownShadow?.contentHash, projectId, notebookId, file.id]);

  // PRE-FETCH markdown content as soon as shadow is available (don't wait for tab click)
  useEffect(() => {
    if (markdownShadow?.status === MarkdownExtractionStatus.Completed && 
        !markdownContent && !isLoadingMarkdown && !markdownError) {
      fetchMarkdownContent();
    }
  }, [markdownShadow?.status, markdownContent, isLoadingMarkdown, markdownError, fetchMarkdownContent]);

  // Track previous file hash to detect when original content changes
  const prevFileHashRef = useRef<string | undefined>(file.fileHash);

  // Refresh function for ORIGINAL content - forces network call to detect server-side changes
  const refreshOriginalContent = useCallback(async () => {
    try {
      // Force network call to check for changes (bypass cache)
      const newBlob = await notebookFilesApi.getNotebookFileContent(
        projectId, 
        notebookId, 
        file.relativePath, 
        file.fileHash,
        true  // forceNetwork = true
      );
      
      // Quick size check - if size is different, content changed
      if (!content || content.size !== newBlob.size) {
        // IMPORTANT: Capture iframe hash BEFORE updating content
        // The iframe stores view state (e.g., current slide) in its location hash
        const currentIframe = htmlIframeRef.current;
        if (currentIframe?.contentWindow) {
          try {
            const currentHash = currentIframe.contentWindow.location.hash;
            if (currentHash) {
              lastIframeHashRef.current = currentHash;
            }
          } catch (e) {
            // Ignore cross-origin access errors
          }
        }
        
        if (isTextBasedContent) {
          const newText = await newBlob.text();
          setTextContent(newText);
        }
        setContent(newBlob);
        
        // If markdown extraction is supported, re-fetch shadow to trigger new extraction
        if (supportsMarkdownExtraction) {
          setMarkdownContent(null);
          notebookFilesApi.getNotebookFileMarkdownShadow(projectId, notebookId, file.id)
            .then(shadow => setMarkdownShadow(shadow))
            .catch(err => console.error('Failed to refresh markdown shadow:', err));
        }
      }
    } catch (err: any) {
      console.error('Failed to refresh original content:', err);
    }
  }, [projectId, notebookId, file.relativePath, file.fileHash, file.id, content, isTextBasedContent, supportsMarkdownExtraction]);

  // When file.fileHash changes (original file was modified), re-fetch markdown shadow
  // This triggers new extraction if needed - no separate polling required
  useEffect(() => {
    if (prevFileHashRef.current !== file.fileHash && supportsMarkdownExtraction) {
      // File content changed - invalidate markdown and re-fetch shadow
      setMarkdownContent(null);
      setMarkdownError(null);
      
      notebookFilesApi.getNotebookFileMarkdownShadow(projectId, notebookId, file.id)
        .then(shadow => setMarkdownShadow(shadow))
        .catch(err => console.error('Failed to refresh markdown shadow:', err));
    }
    prevFileHashRef.current = file.fileHash;
  }, [file.fileHash, file.id, projectId, notebookId, supportsMarkdownExtraction]);

  // Auto-refresh content - check for file changes every 5 seconds
  useEffect(() => {
    const interval = setInterval(refreshOriginalContent, 5000);
    return () => clearInterval(interval);
  }, [refreshOriginalContent]);

  // Preserve scroll position for ORIGINAL content
  useEffect(() => {
    const scrollContainer = originalScrollRef.current;
    if (!scrollContainer) return;

    const scrollTop = scrollContainer.scrollTop;
    const scrollHeight = scrollContainer.scrollHeight;

    requestAnimationFrame(() => {
      if (scrollContainer) {
        const newScrollHeight = scrollContainer.scrollHeight;
        if (newScrollHeight > scrollHeight) {
          const wasNearBottom = scrollHeight - scrollTop - scrollContainer.clientHeight < 100;
          if (wasNearBottom) {
            scrollContainer.scrollTop = scrollContainer.scrollHeight;
          } else {
            scrollContainer.scrollTop = scrollTop;
          }
        } else {
          scrollContainer.scrollTop = scrollTop;
        }
      }
    });
  }, [textContent]);

  // Preserve scroll position for MARKDOWN content (independent)
  useEffect(() => {
    const scrollContainer = markdownScrollRef.current;
    if (!scrollContainer) return;

    const scrollTop = scrollContainer.scrollTop;
    const scrollHeight = scrollContainer.scrollHeight;

    requestAnimationFrame(() => {
      if (scrollContainer) {
        const newScrollHeight = scrollContainer.scrollHeight;
        if (newScrollHeight > scrollHeight) {
          const wasNearBottom = scrollHeight - scrollTop - scrollContainer.clientHeight < 100;
          if (wasNearBottom) {
            scrollContainer.scrollTop = scrollContainer.scrollHeight;
          } else {
            scrollContainer.scrollTop = scrollTop;
          }
        } else {
          scrollContainer.scrollTop = scrollTop;
        }
      }
    });
  }, [markdownContent]);

  const objectUrl = useMemo(() => {
    if (content && !contentType.startsWith('application/pdf')) {
      return URL.createObjectURL(content);
    }
    return null;
  }, [content, contentType]);

  // HTML content for rendering HTML files with resolved resource paths
  const [htmlContent, setHtmlContent] = useState<string | null>(null);
  const [isLoadingHtmlResources, setIsLoadingHtmlResources] = useState(false);
  const htmlIframeRef = useRef<HTMLIFrameElement>(null);
  
  // Discovered site root from resource resolution (used for navigation)
  const discoveredSiteRootRef = useRef<string | null>(null);
  
  // Track current hash from iframe to preserve it during refreshes
  const lastIframeHashRef = useRef<string>('');
  
  // Track blob URLs created for HTML resources for cleanup
  const htmlResourceBlobUrlsRef = useRef<Set<string>>(new Set());

  
  useEffect(() => {
    if (contentType !== 'text/html' && contentType !== 'application/xhtml+xml') {
      setHtmlContent(null);
      return;
    }
    
    if (!textContent) {
      setHtmlContent(null);
      return;
    }
    
    // Extract basePath from current path (e.g., "Output/slide.html" -> "Output")
    const basePath = file.relativePath.includes('/') 
      ? file.relativePath.substring(0, file.relativePath.lastIndexOf('/'))
      : undefined;
    
    console.log('[FilePreviewOverlay] Resolving HTML resources:', {
      relativePath: file.relativePath,
      basePath,
      projectId,
      notebookId,
    });
    
    // Resolve all resource URLs (images, stylesheets, scripts, etc.)
    setIsLoadingHtmlResources(true);
    
    resolveHtmlResources({
      html: textContent,
      projectId,
      notebookId,
      basePath,
      fileExists,
    }).then((result) => {
      // Clean up old blob URLs before storing new ones
      cleanupBlobUrls(htmlResourceBlobUrlsRef.current);
      htmlResourceBlobUrlsRef.current = result.blobUrls;
      
      // Store discovered site root for navigation
      discoveredSiteRootRef.current = result.discoveredRoot;
      
      // IMPORTANT: Capture hash BEFORE updating content
      // The iframe stores view state (e.g., current slide) in its location hash
      const currentIframe = htmlIframeRef.current;
      if (currentIframe?.contentWindow) {
        try {
          const currentHash = currentIframe.contentWindow.location.hash;
          if (currentHash) {
            lastIframeHashRef.current = currentHash;
          }
        } catch (e) {
          // Ignore cross-origin access errors
        }
      }
      
      setHtmlContent(result.html);
      setIsLoadingHtmlResources(false);
    }).catch((error) => {
      console.error('Failed to resolve HTML resources:', error);
      // Fall back to original content
      setHtmlContent(textContent);
      setIsLoadingHtmlResources(false);
    });
  }, [textContent, contentType, file.relativePath, projectId, notebookId, fileExists]);
  
  useEffect(() => {
    return () => {
      if (objectUrl) {
        URL.revokeObjectURL(objectUrl);
      }
      // Clean up HTML resource blob URLs
      cleanupBlobUrls(htmlResourceBlobUrlsRef.current);
      htmlResourceBlobUrlsRef.current.clear();
    };
  }, [objectUrl]);

  // Determine if this file type needs a large modal
  const needsLargeModal = () => {
    if (isLoading || error || !content) return false;
    
    // Always use large modal if tabs are shown
    if (supportsMarkdownExtraction) return true;
    
    // HTML files need large modal for best viewing
    if (contentType === 'text/html' || contentType === 'application/xhtml+xml') return true;
    
    return contentType.startsWith('image/') || 
           contentType.startsWith('video/') || 
           contentType.startsWith('audio/') ||
           contentType.startsWith('application/pdf') ||
           contentType.startsWith('text/') ||
           contentType === 'application/json' ||
           contentType === 'application/typescript';
  };

  const getModalClasses = () => {
    if (isEmbedded) {
      // Embedded mode: fill parent container, no fixed positioning
      return "bg-white flex flex-col h-full w-full";
    }
    if (isFullScreen || isStandalone) {
      return "fixed inset-0 z-50 bg-white flex flex-col shadow-lg";
    }
    
    if (needsLargeModal()) {
      // HTML files benefit from larger modal for better viewing
      const isHtml = contentType === 'text/html' || contentType === 'application/xhtml+xml';
      if (isHtml) {
        return "bg-white rounded-lg shadow-xl w-full max-w-6xl h-full max-h-[95vh] flex flex-col";
      }
      return "bg-white rounded-lg shadow-xl w-full max-w-4xl h-full max-h-[90vh] flex flex-col";
    } else {
      return "bg-white rounded-lg shadow-xl w-full max-w-md max-h-96 flex flex-col";
    }
  };

  const renderViewer = () => {
    // Show tabs if markdown extraction is supported
    if (supportsMarkdownExtraction) {
      return (
        <div className="w-full h-full min-h-0 relative">
          {/* Original content - only render when active */}
          {activeTab === 'original' && (
            <div className="w-full h-full absolute inset-0 overflow-auto">
              {renderOriginalContent()}
            </div>
          )}
          
          {/* Markdown content - only render when active (54MB+ content would freeze UI otherwise) */}
          {activeTab === 'markdown' && (
            <div className="w-full h-full absolute inset-0 overflow-auto">
              {renderMarkdownContent()}
            </div>
          )}
        </div>
      );
    }
    
    // Fallback to existing logic for files without markdown support
    return renderOriginalContent();
  };

  const renderOriginalContent = () => {
    if (isLoading) {
      return <div className="flex items-center justify-center h-full"><p>Loading file content...</p></div>;
    }
    if (error) {
      return <div className="flex items-center justify-center h-full"><p className="text-red-500">{error}</p></div>;
    }
    if (!content) {
      return null;
    }

    // When in tabbed mode, use inline mode to avoid nested containers
    const useInlineMode = supportsMarkdownExtraction;

    // Handle Markdown with Mermaid support (matching project view)
    if (isMarkdown) {
      if (isLoadingText || textContent === null) {
        return <div className="flex items-center justify-center h-full"><p>Processing markdown...</p></div>;
      }
      // Extract directory from current path for resolving relative URLs (e.g., "Output/guide.md" -> "Output")
      const basePath = file.relativePath.includes('/') 
        ? file.relativePath.substring(0, file.relativePath.lastIndexOf('/'))
        : undefined;
      return (
        <div ref={originalScrollRef} className="h-full w-full overflow-auto">
          <div className="p-4">
            <MarkdownViewer text={textContent} inlineMode={useInlineMode} hidePreviewHeader={!useInlineMode} projectId={projectId} notebookId={notebookId} basePath={basePath} />
          </div>
        </div>
      );
    }

    // Handle HTML files - render in iframe using srcdoc with resolved resource blob URLs
    // The application is agnostic to the content - just display it and let the HTML handle its own layout
    if (contentType === 'text/html' || contentType === 'application/xhtml+xml') {
      if (isLoadingText || textContent === null) {
        return <div className="flex items-center justify-center h-full"><p>Processing HTML content...</p></div>;
      }
      if (isLoadingHtmlResources || !htmlContent) {
        return <div className="flex items-center justify-center h-full"><p>Loading resources...</p></div>;
      }
      
      return (
        <div ref={originalScrollRef} className="h-full w-full overflow-hidden">
          <iframe
            ref={htmlIframeRef}
            srcDoc={htmlContent}
            className="w-full h-full border-0"
            title="HTML Preview"
            sandbox="allow-same-origin allow-scripts allow-popups allow-modals"
            allow="fullscreen"
            onLoad={() => {
              // Restore hash if we have one (for preserving navigation state on refresh)
              setTimeout(() => {
                const iframe = htmlIframeRef.current;
                if (iframe?.contentWindow && lastIframeHashRef.current) {
                  try {
                    if (iframe.contentWindow.location.hash !== lastIframeHashRef.current) {
                      iframe.contentWindow.location.hash = lastIframeHashRef.current;
                    }
                  } catch (e) {
                    // Ignore cross-origin errors
                  }
                }
              }, 100);
            }}
          />
        </div>
      );
    }

    // Handle text files (including code, JSON, and CSV) (matching project view)
    if (contentType.startsWith('text/') || contentType === 'application/json' || contentType === 'application/typescript') {
      if (isLoadingText || textContent === null) {
        return <div className="flex items-center justify-center h-full"><p>Processing text content...</p></div>;
      }
      return (
        <div ref={originalScrollRef} className="h-full w-full overflow-auto">
          <TextViewer text={textContent} inlineMode={useInlineMode} hidePreviewHeader={!useInlineMode} />
        </div>
      );
    }

    // Handle images
    if (contentType.startsWith('image/')) {
      return <ImageViewer src={objectUrl!} alt={file.fileName} inlineMode={useInlineMode} />;
    }

    // Handle PDFs
    if (contentType.startsWith('application/pdf')) {
      return <PdfViewer blob={content} inlineMode={useInlineMode} />;
    }

    // Handle audio files
    if (contentType.startsWith('audio/')) {
      return <AudioPlayer blob={content} inlineMode={useInlineMode} />;
    }

    // Handle video files
    if (contentType.startsWith('video/')) {
      return <VideoPlayer blob={content} inlineMode={useInlineMode} />;
    }

    // Fallback for unsupported file types (matching project view)
    return (
      <div className="flex flex-col items-center justify-center text-center space-y-4">
        <div>
          <h3 className="text-lg font-semibold text-gray-800">No preview available</h3>
          <p className="text-sm text-gray-500 mt-1">Preview not available for this file type ({contentType})</p>
          <p className="text-sm text-gray-500 mt-2">You can download the file to view its contents</p>
        </div>
        <a 
          href={objectUrl!} 
          download={file.fileName} 
          className="inline-flex items-center px-4 py-2 bg-blue-600 text-white text-sm font-medium rounded-md hover:bg-blue-700 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2 transition-colors"
        >
          Download File
        </a>
      </div>
    );
  };

  const renderMarkdownContent = () => {
    if (!markdownShadow) {
      return (
        <div className="flex items-center justify-center h-full text-gray-500">
          <div className="text-center">
            <p>Markdown extraction not available for this file type</p>
          </div>
        </div>
      );
    }

    if (markdownShadow.status === MarkdownExtractionStatus.Processing) {
      return (
        <div className="flex items-center justify-center h-full">
          <div className="text-center">
            <LoadingSpinner message="Extracting markdown content..." />
          </div>
        </div>
      );
    }

    if (markdownShadow.status === MarkdownExtractionStatus.Failed) {
      return (
        <div className="flex items-center justify-center h-full text-red-600">
          <div className="text-center">
            <p>Failed to extract markdown content from this file.</p>
            <p className="text-sm text-gray-600 mt-2">
              This may be due to the file format or content structure.
            </p>
          </div>
        </div>
      );
    }

    if (isLoadingMarkdown) {
      return (
        <div className="flex items-center justify-center h-full">
          <div className="text-center">
            <LoadingSpinner message="Loading extracted text..." />
          </div>
        </div>
      );
    }

    if (markdownError) {
      return (
        <div className="flex items-center justify-center h-full text-red-600">
          <div className="text-center">
            <p>{markdownError}</p>
            <button
              className="mt-2 px-4 py-2 text-sm bg-blue-600 text-white rounded hover:bg-blue-700"
              onClick={fetchMarkdownContent}
            >
              Retry
            </button>
          </div>
        </div>
      );
    }

    if (markdownContent) {
      // Extract directory from current path for resolving relative URLs (e.g., "Output/guide.md" -> "Output")
      const extractedBasePath = file.relativePath.includes('/') 
        ? file.relativePath.substring(0, file.relativePath.lastIndexOf('/'))
        : undefined;
      return (
        <div ref={markdownScrollRef} className="h-full w-full overflow-auto">
          <div className="p-4">
            <MarkdownViewer text={markdownContent} inlineMode={true} projectId={projectId} notebookId={notebookId} basePath={extractedBasePath} />
          </div>
        </div>
      );
    }

    return (
      <div className="flex items-center justify-center h-full text-gray-500">
        No markdown content available
      </div>
    );
  };

  return (
    <div 
        className={isEmbedded
          ? "h-full w-full flex flex-col"
          : (isFullScreen || isStandalone)
            ? "fixed inset-0 z-50" 
            : "fixed inset-0 bg-black bg-opacity-60 z-50 flex justify-center items-center p-4"
        }
        onClick={isEmbedded || isFullScreen || isEditingMd ? undefined : onClose}
    >
      <div 
        className={getModalClasses()}
        onClick={(e) => e.stopPropagation()}
        style={{ display: isEditingMd ? 'none' : undefined }}
        tabIndex={-1}
      >
        <header className={`flex flex-col bg-gray-50 ${isFullScreen ? '' : 'rounded-t-lg'}`} data-tour-id="file-preview.header">
          {/* Title and close button */}
          <div className="flex items-center justify-between p-3 border-b">
            <h2 className="text-lg font-semibold truncate pr-4">{file.fileName}</h2>
            <div className="flex items-center space-x-2" data-tour-id="file-preview.controls">
              {isMarkdown && (
                <button
                  onClick={openMarkdownEditorFromPreview}
                  className="p-2 rounded hover:bg-gray-200 text-gray-600 hover:text-gray-800 disabled:opacity-50"
                  disabled={mdEditorLoading}
                  aria-label="Edit markdown"
                  title="Edit"
                >
                  <FaRegEdit className="w-4 h-4" />
                </button>
              )}
              {(!isStandalone || isEmbedded) && (
                <button
                  onClick={() => window.open(previewUrl, '_blank', 'width=1200,height=800,menubar=no,toolbar=no,location=no,status=no')}
                  className="p-2 rounded hover:bg-gray-200 text-gray-600 hover:text-gray-800"
                  aria-label="Open in new window"
                  title="Open in new window"
                >
                  <VscLinkExternal className="w-4 h-4" />
                </button>
              )}
              <button
                  onClick={handleDownload}
                  disabled={!content || isLoading}
                  className="p-2 rounded hover:bg-gray-200 text-gray-600 hover:text-gray-800 disabled:opacity-50"
                  aria-label="Download"
                  title="Download"
                >
                  <FaDownload className="w-4 h-4" />
              </button>
              {!isStandalone && !isEmbedded && (
                <button
                  onClick={() => setIsFullScreen(!isFullScreen)}
                  className="p-2 rounded hover:bg-gray-200 text-gray-600 hover:text-gray-800"
                  aria-label={isFullScreen ? 'Exit full screen' : 'Full screen'}
                  title={isFullScreen ? 'Exit full screen' : 'Full screen'}
                >
                  {isFullScreen ? <FaCompress className="w-4 h-4" /> : <FaExpandAlt className="w-4 h-4" />}
                </button>
              )}
              {!isEmbedded && (
                <button
                  onClick={onClose}
                  className="p-2 rounded hover:bg-gray-200 text-gray-600 hover:text-gray-800"
                  aria-label="Close"
                  title="Close"
                >
                  <VscChromeClose size={18} />
                </button>
              )}
            </div>
          </div>
          
          {/* Tab interface - hidden in embedded mode */}
          {supportsMarkdownExtraction && !isEmbedded && (
            <div className="flex space-x-1 px-3 pb-2" data-tour-id="file-preview.tabs">
              <button
                className={`px-3 py-1 text-sm font-medium rounded transition-colors ${
                  activeTab === 'original'
                    ? 'bg-blue-600 text-white'
                    : 'text-gray-600 hover:text-gray-800 hover:bg-gray-100'
                }`}
                onClick={() => setActiveTab('original')}
              >
                Original Content
              </button>
              <button
                className={`px-3 py-1 text-sm font-medium rounded transition-colors relative ${
                  activeTab === 'markdown'
                    ? 'bg-blue-600 text-white'
                    : 'text-gray-600 hover:text-gray-800 hover:bg-gray-100'
                }`}
                onClick={() => setActiveTab('markdown')}
                disabled={!markdownShadow || markdownShadow.status !== MarkdownExtractionStatus.Completed}
              >
                Extracted Text
                {markdownShadow && (
                  <span className={`ml-1 inline-flex items-center px-1.5 py-0.5 rounded-full text-xs font-medium ${
                    markdownShadow.status === MarkdownExtractionStatus.Completed
                      ? 'bg-green-100 text-green-800'
                      : markdownShadow.status === MarkdownExtractionStatus.Processing
                      ? 'bg-yellow-100 text-yellow-800'
                      : markdownShadow.status === MarkdownExtractionStatus.Failed
                      ? 'bg-red-100 text-red-800'
                      : 'bg-gray-100 text-gray-800'
                  }`}>
                    {markdownShadow.status}
                  </span>
                )}
              </button>
            </div>
          )}
        </header>
        <main className={`flex-1 min-h-0 ${needsLargeModal() ? 'flex' : 'flex flex-col justify-center'} ${supportsMarkdownExtraction ? '' : 'p-4 overflow-auto'}`} data-tour-id="file-preview.content">
          {renderViewer()}
        </main>
      </div>
      {/* Markdown Full Screen Editor Portal */}
      {isEditingMd && (
        <FullScreenEditor
          content={mdEditorContent}
          onSave={saveMarkdownFromPreview}
          onCancel={() => setIsEditingMd(false)}
          mode={'edit'}
          title={`Edit File: ${file.fileName}`}
          placeholder={file.fileName}
          submitLabel={'Save'}
          isLoading={mdEditorLoading}
          error={mdEditorError}
          projectId={projectId}
          notebookId={notebookId}
          basePath={file.relativePath.includes('/') ? file.relativePath.substring(0, file.relativePath.lastIndexOf('/')) : undefined}
        />
      )}
    </div>
  );
}; 