import { 
  NotebookFileDto, 
  NotebookFolderTreeDto, 
  CreateNotebookFolderDto,
  PublishNotebookFileDto,
  PublishNotebookFileResultDto,
  OriginFileInfoDto
} from '../types/notebook';
import { NotebookFileMarkdownShadowDto } from '../types/api';
import { getCachedFile, cacheFile, deleteCachedFile } from '../utils/fileCache';
import { API_BASE_URL } from '../config/apiConfig';

// API_BASE_URL is resolved at runtime via window.__RUNTIME_CONFIG__

async function callNotebookApi<T>(endpoint: string, options: RequestInit = {}): Promise<T> {
  try {
    const response = await fetch(`${API_BASE_URL}${endpoint}`, {
      ...options,
      headers: {
        'Content-Type': 'application/json',
        ...options.headers,
      }
    });

    if (!response.ok) {
      throw new Error(`API call failed: ${response.statusText}`);
    }

    if (response.status === 204) {
      return undefined as T;
    }

    return response.json();
  } catch (error) {
    console.error('Notebook API call error:', error);
    throw error;
  }
}

export const notebookFilesApi = {
  // List all files and folders for a notebook
  listFiles: async (projectId: string, notebookId: string): Promise<NotebookFileDto[]> => {
    return callNotebookApi<NotebookFileDto[]>(`/projects/${projectId}/notebooks/${notebookId}/files`);
  },

  // Get folder tree structure
  getFolderTree: async (projectId: string, notebookId: string): Promise<NotebookFolderTreeDto> => {
    return callNotebookApi<NotebookFolderTreeDto>(`/projects/${projectId}/notebooks/${notebookId}/files/tree`);
  },

  // Upload files to notebook
  uploadFiles: async (projectId: string, notebookId: string, files: File[], targetRelativePath: string, index: boolean): Promise<NotebookFileDto[]> => {
    const formData = new FormData();
    files.forEach(file => formData.append('files', file));
    formData.append('targetRelativePath', targetRelativePath);
    formData.append('index', String(index));

    const response = await fetch(`${API_BASE_URL}/projects/${projectId}/notebooks/${notebookId}/files/upload`, {
      method: 'POST',
      body: formData,
    });
    if (!response.ok) throw new Error('Failed to upload files');
    
    const result = await response.json();
    
    // Clear cache for uploaded files
    files.forEach(file => {
      const relativePath = targetRelativePath ? `${targetRelativePath}/${file.name}` : file.name;
      const cacheKey = `${notebookId}:${relativePath}`;
      deleteCachedFile(projectId, cacheKey).catch(() => {/*noop*/});
    });
    
    return result;
  },

  // Create folder
  createFolder: async (projectId: string, notebookId: string, dto: CreateNotebookFolderDto): Promise<NotebookFolderTreeDto> => {
    return callNotebookApi<NotebookFolderTreeDto>(`/projects/${projectId}/notebooks/${notebookId}/files/create-folder`, {
      method: 'POST',
      body: JSON.stringify(dto),
    });
  },

  // Rename file or folder
  rename: async (projectId: string, notebookId: string, path: string, newName: string): Promise<void> => {
    // Clear cache for the old path before renaming
    const oldCacheKey = `${notebookId}:${path}`;
    deleteCachedFile(projectId, oldCacheKey).catch(() => {/*noop*/});
    
    return callNotebookApi<void>(`/projects/${projectId}/notebooks/${notebookId}/files/rename`, {
      method: 'PATCH',
      body: JSON.stringify({ path, newName }),
    });
  },

  // Move file or folder
  move: async (projectId: string, notebookId: string, path: string, destFolderPath: string): Promise<void> => {
    // Clear cache for the old path before moving
    const oldCacheKey = `${notebookId}:${path}`;
    deleteCachedFile(projectId, oldCacheKey).catch(() => {/*noop*/});
    
    return callNotebookApi<void>(`/projects/${projectId}/notebooks/${notebookId}/files/move`, {
      method: 'PATCH',
      body: JSON.stringify({ path, destFolderPath }),
    });
  },

  // Delete file or folder
  delete: async (projectId: string, notebookId: string, path: string): Promise<void> => {
    // Clear cache for the deleted file
    const cacheKey = `${notebookId}:${path}`;
    deleteCachedFile(projectId, cacheKey).catch(() => {/*noop*/});
    
    return callNotebookApi<void>(`/projects/${projectId}/notebooks/${notebookId}/files?path=${encodeURIComponent(path)}`, {
      method: 'DELETE',
    });
  },

  // Sync notebook files (manual trigger)
  sync: async (projectId: string, notebookId: string): Promise<void> => {
    const result = await callNotebookApi<void>(`/projects/${projectId}/notebooks/${notebookId}/files/sync`, {
      method: 'POST',
    });
    
    // Clear all notebook cache after sync since file metadata may have changed
    // Note: This is a conservative approach - in a more sophisticated implementation,
    // we could compare file hashes to only clear changed files
    console.warn('Notebook sync completed - consider clearing cache for changed files');
    
    return result;
  },

  // Copy file from project to notebook
  copyFromProject: async (projectId: string, notebookId: string, sourceFileId: string, version?: number): Promise<NotebookFileDto> => {
    const result = await callNotebookApi<NotebookFileDto>(`/projects/${projectId}/notebooks/${notebookId}/files/copy-from-project`, {
      method: 'POST',
      body: JSON.stringify({ 
        contentFileId: sourceFileId, 
        versionNumber: version,
        targetRelativePath: null 
      }),
    });
    
    // Clear cache for the copied file
    const cacheKey = `${notebookId}:${result.relativePath}`;
    deleteCachedFile(projectId, cacheKey).catch(() => {/*noop*/});
    
    return result;
  },

  // Get origin file information for lineage display
  getOriginFileInfo: async (
    projectId: string, 
    notebookId: string, 
    contentFileVersionId: string
  ): Promise<OriginFileInfoDto> => {
    return callNotebookApi<OriginFileInfoDto>(
      `/projects/${projectId}/notebooks/${notebookId}/files/origin-info?contentFileVersionId=${contentFileVersionId}`);
  },

  // Publish notebook file to project
  publishToProject: async (
    projectId: string, 
    notebookId: string, 
    data: PublishNotebookFileDto
  ): Promise<PublishNotebookFileResultDto> => {
    return callNotebookApi<PublishNotebookFileResultDto>(
      `/projects/${projectId}/notebooks/${notebookId}/files/publish-to-project`, {
        method: 'POST',
        body: JSON.stringify(data),
      });
  },

  // Get raw file content for preview
  /**
   * Fetches the raw notebook file content. Uses IndexedDB cache if the stored
   * fileHash matches the server-side hash supplied via folder-tree metadata.
   * @param currentHash Latest file hash as reported by the folder tree. If omitted
   *                    the method falls back to previous behaviour.
   */
  getNotebookFileContent: async (
    projectId: string,
    notebookId: string,
    relativePath: string,
    currentHash?: string,
    forceNetwork: boolean = false
  ): Promise<Blob> => {
    const cacheKey = `${notebookId}:${relativePath}`;

    // 1️⃣ Try IndexedDB first
    let cached: { blob: Blob; contentType: string; fileName: string; fileHash?: string } | null = null;
    try {
      cached = await getCachedFile(projectId, cacheKey);
    } catch (err) {
      console.warn('IndexedDB cache read failed – falling back to network', err);
    }

    // If we have a cached entry AND the hash matches the current one, use it
    if (!forceNetwork && cached && (!currentHash || cached.fileHash === currentHash)) {
      return cached.blob;
    }

    const url = `${API_BASE_URL}/projects/${projectId}/notebooks/${notebookId}/files/content?path=${encodeURIComponent(relativePath)}`;

    const response = await fetch(url);

    if (!response.ok) {
      if (response.status === 404) {
        throw new Error('File not found');
      }
      throw new Error(`API call failed: ${response.statusText}`);
    }

    const blob = await response.blob();
    const contentType = response.headers.get('Content-Type') || 'application/octet-stream';
    const fileName = relativePath.split('/').pop() || 'unknown';

    // 3️⃣ Persist to cache (fire-and-forget)
    cacheFile(projectId, cacheKey, { blob, contentType, fileName, fileHash: currentHash })
      .catch((err) => {
        console.warn('Cache write failed', err);
      });

    return blob;
  },



  // Helper function to clear cache for a specific notebook file
  clearNotebookFileCache: async (projectId: string, notebookId: string, relativePath: string): Promise<void> => {
    const cacheKey = `${notebookId}:${relativePath}`;
    await deleteCachedFile(projectId, cacheKey);
  },

  // Helper function to clear cache for all files in a notebook
  clearNotebookCache: async (_projectId: string, _notebookId: string): Promise<void> => {
    // This function is currently a placeholder - would need implementation 
    // to clear all cached files for a specific notebook
    console.warn('clearNotebookCache not yet implemented');
  },

  // Markdown shadow functions
  getNotebookFileMarkdownShadow: async (projectId: string, notebookId: string, fileId: string): Promise<NotebookFileMarkdownShadowDto> => {
    return callNotebookApi<NotebookFileMarkdownShadowDto>(`/projects/${projectId}/notebooks/${notebookId}/files/${fileId}/markdown`);
  },

  getNotebookFileMarkdownContent: async (projectId: string, notebookId: string, fileId: string, currentHash?: string): Promise<{ blob: Blob; contentType: string; fileName: string }> => {
    // 1. Try IndexedDB cache first
    const cacheKey = `${notebookId}:markdown:${fileId}`;
    if (currentHash) {
      try {
        const cached = await getCachedFile(projectId, cacheKey);
        if (cached && cached.fileHash === currentHash) {
          return { blob: cached.blob, contentType: cached.contentType, fileName: cached.fileName };
        }
      } catch (err) {
        console.warn('Cache read failed for markdown', err);
      }
    }

    const response = await fetch(`${API_BASE_URL}/projects/${projectId}/notebooks/${notebookId}/files/${fileId}/markdown/content`);

    if (!response.ok) {
      throw new Error('Failed to fetch markdown content');
    }

    const blob = await response.blob();
    const contentType = response.headers.get('Content-Type') || 'text/markdown';
    const contentDisposition = response.headers.get('Content-Disposition') || '';
    let fileName = 'markdown-content.md';
    
    if (contentDisposition) {
      const starMatch = contentDisposition.match(/filename\*=(?:UTF-8''|)([^;]+)/i);
      if (starMatch?.[1]) {
        try {
          fileName = decodeURIComponent(starMatch[1].replace(/"/g, ''));
        } catch {
          fileName = starMatch[1].replace(/"/g, '');
        }
      } else {
        const regularMatch = contentDisposition.match(/filename=([^;]+)/i);
        if (regularMatch?.[1]) {
          fileName = regularMatch[1].replace(/"/g, '');
        }
      }
    }

    // 2. Persist to cache
    if (currentHash) {
      cacheFile(projectId, cacheKey, { blob, contentType, fileName, fileHash: currentHash })
        .catch(err => console.warn('Failed to cache markdown file', err));
    }

    return { blob, contentType, fileName };
  },

}; 