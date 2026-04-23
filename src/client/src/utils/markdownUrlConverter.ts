/**
 * Utility module for converting between absolute API URLs and relative paths in markdown content.
 * 
 * This enables portable markdown files that work across:
 * - Different NOTEBOOKS (backend has path-based API)
 * - Local file systems
 * - Exported/imported content
 * 
 * IMPORTANT: Relative URLs are ONLY supported for NOTEBOOK files.
 * Project files use file IDs, not paths, so relative URLs cannot be resolved.
 * 
 * Security is preserved by converting relative paths back to authenticated API URLs during rendering.
 */

import { API_BASE_URL } from '../config/apiConfig';
import { safeDecodeURIComponent } from './urlEncoding';

/**
 * Pattern to match notebook file content URLs in markdown
 * Matches: ![alt](https://domain/api/projects/{guid}/notebooks/{guid}/files/content?path=...)
 * Also matches: [text](https://domain/api/projects/{guid}/notebooks/{guid}/files/content?path=...)
 */
const ABSOLUTE_URL_PATTERN = /(!?\[[^\]]*\])\((https?:\/\/[^)]*?\/api\/projects\/[0-9a-f-]+\/notebooks\/[0-9a-f-]+\/files\/content\?path=([^)&]+)(?:[^)]*)?)\)/gi;

/**
 * Pattern to match relative paths in markdown (images and links)
 * Matches: ![alt](./path), ![alt](../path), ![alt](path), [text](./path), etc.
 * Excludes: URLs with protocols, absolute paths starting with /
 */
const RELATIVE_PATH_PATTERN = /(!?\[[^\]]*\])\((?!https?:\/\/|\/)(\.\.?\/)?([^)]+)\)/gi;

/**
 * Generic pattern to match HTML media tags with a src attribute: <audio>, <video>, <source>
 * Captures: tag name, attributes before src, quote, src value, attributes after src (until >)
 */
const HTML_MEDIA_SRC_PATTERN = /<(audio|video|source)\b([^>]*?\s)src\s*=\s*(['"])([^'">]+?)\3([^>]*?)>/gi;

/**
 * Extract the path parameter from a notebook file content URL
 */
function extractPathFromUrl(url: string): string | null {
  try {
    const urlObj = new URL(url, window.location.origin);
    return urlObj.searchParams.get('path');
  } catch {
    return null;
  }
}

/**
 * Decode and normalize a file path
 */
function normalizePath(path: string): string {
  try {
    // Decode URL encoding
    const decoded = decodeURIComponent(path);
    // Normalize path separators to forward slashes
    return decoded.replace(/\\/g, '/');
  } catch {
    return path;
  }
}

/**
 * Convert absolute API URLs to relative paths in markdown content.
 * Used when saving markdown files to make them portable.
 * 
 * Example transformation:
 * Input:  ![chart](https://go.guideants.ai/api/projects/.../notebooks/.../files/content?path=Output%2Fchart.png&m=123)
 * Output: ![chart](./Output/chart.png)
 * 
 * @param markdownContent - The markdown content to process
 * @param _projectId - Current project ID (reserved for future validation)
 * @param _notebookId - Current notebook ID (reserved for future validation)
 * @returns Markdown with relative paths
 */
export function convertAbsoluteToRelative(
  markdownContent: string,
  _projectId?: string,
  _notebookId?: string
): string {
  if (!markdownContent) return markdownContent;

  // Markdown link/image syntax
  let updated = markdownContent.replace(ABSOLUTE_URL_PATTERN, (match, linkPart, fullUrl) => {
    // Extract and normalize the path
    const path = extractPathFromUrl(fullUrl);
    if (!path) return match; // Keep original if we can't extract path

    const normalizedPath = normalizePath(path);
    
    // Convert to relative path
    // If path starts with a folder, prepend ./
    const relativePath = normalizedPath.startsWith('/') 
      ? `.${normalizedPath}` 
      : `./${normalizedPath}`;

    // Return the markdown with relative path
    return `${linkPart}(${relativePath})`;
  });

  // HTML media tags (<audio>, <video>, <source>)
  updated = updated.replace(HTML_MEDIA_SRC_PATTERN, (match, tag, before, quote, src, after) => {
    if (!src) return match;
    // Only convert notebook file content URLs
    if (!isNotebookFileUrl(src)) return match;
    const path = extractPathFromUrl(src);
    if (!path) return match;
    const normalizedPath = normalizePath(path);
    const relativePath = normalizedPath.startsWith('/')
      ? `.${normalizedPath}`
      : `./${normalizedPath}`;
    return `<${tag}${before}src=${quote}${relativePath}${quote}${after}>`;
  });

  return updated;
}

/**
 * Convert relative paths to absolute authenticated API URLs in markdown content.
 * Used when rendering markdown to enable authentication and authorization.
 * 
 * Example transformation:
 * Input:  ![chart](./Output/chart.png)
 * Output: ![chart](https://api.server.com/projects/{projectId}/notebooks/{notebookId}/files/content?path=Output%2Fchart.png)
 * 
 * @param markdownContent - The markdown content to process
 * @param projectId - Current project ID (required)
 * @param notebookId - Current notebook ID (required)
 * @param baseUrl - Base URL for API (defaults to API_BASE_URL)
 * @returns Markdown with absolute authenticated URLs
 */
export function convertRelativeToAbsolute(
  markdownContent: string,
  projectId: string,
  notebookId: string,
  baseUrl: string = API_BASE_URL
): string {
  if (!markdownContent || !projectId || !notebookId) return markdownContent;

  // Remove trailing slash from base URL
  const normalizedBase = baseUrl.replace(/\/$/, '');

  // Markdown link/image syntax
  let updated = markdownContent.replace(RELATIVE_PATH_PATTERN, (match, linkPart, dotPrefix, relativePath) => {
    // Skip if it looks like an external resource (contains ://)
    if (relativePath.includes('://')) return match;
    
    // Skip if it's a fragment or special link
    if (relativePath.startsWith('#') || relativePath.startsWith('mailto:')) return match;

    // Normalize the relative path
    let normalizedPath = relativePath;
    
    // Handle ./ prefix
    if (dotPrefix === './') {
      normalizedPath = relativePath;
    }
    // Handle ../ prefix (go up one level - we're at notebook root, so just use the path after ../)
    else if (dotPrefix === '../') {
      // For notebook context, we're already at the root, so ../ is treated as ./
      normalizedPath = relativePath;
    }
    // No prefix - treat as relative to current location
    else {
      normalizedPath = relativePath;
    }

    // Build the absolute API URL
    const encodedPath = encodeURIComponent(safeDecodeURIComponent(normalizedPath));
    const absoluteUrl = `${normalizedBase}/projects/${projectId}/notebooks/${notebookId}/files/content?path=${encodedPath}`;

    // Return the markdown with absolute URL
    return `${linkPart}(${absoluteUrl})`;
  });

  // HTML media tags (<audio>, <video>, <source>)
  updated = updated.replace(HTML_MEDIA_SRC_PATTERN, (match, tag, before, quote, src, after) => {
    if (!src) return match;
    // Only transform if src is a relative URL we can resolve in notebook context
    if (!isRelativePath(src)) return match;
    // Normalize ./ or ../ prefixes
    const cleanPath = src.replace(/^\.\.?\//, '');
    const encodedPath = encodeURIComponent(safeDecodeURIComponent(cleanPath));
    const absoluteUrl = `${normalizedBase}/projects/${projectId}/notebooks/${notebookId}/files/content?path=${encodedPath}`;
    return `<${tag}${before}src=${quote}${absoluteUrl}${quote}${after}>`;
  });

  return updated;
}

/**
 * Check if a URL is a relative path (not an absolute URL)
 */
export function isRelativePath(url: string): boolean {
  if (!url) return false;
  
  // Absolute URLs have protocols
  if (url.startsWith('http://') || url.startsWith('https://')) return false;
  
  // Absolute paths start with /
  if (url.startsWith('/')) return false;
  
  // Special protocols
  if (url.startsWith('mailto:') || url.startsWith('tel:') || url.startsWith('data:')) return false;
  
  // Everything else is considered relative
  return true;
}

/**
 * Check if a URL is a notebook file content URL
 */
export function isNotebookFileUrl(url: string): boolean {
  if (!url) return false;
  
  try {
    const urlObj = new URL(url, window.location.origin);
    return urlObj.pathname.includes('/projects/') && 
           urlObj.pathname.includes('/notebooks/') && 
           urlObj.pathname.includes('/files/content');
  } catch {
    return false;
  }
}

/**
 * Extract projectId and notebookId from a notebook file content URL
 */
export function extractContextFromUrl(url: string): { projectId: string; notebookId: string } | null {
  if (!url) return null;
  
  // Pattern: /api/projects/{projectId}/notebooks/{notebookId}/files/content
  const pattern = /\/projects\/([0-9a-f-]+)\/notebooks\/([0-9a-f-]+)\/files\/content/i;
  const match = url.match(pattern);
  
  if (match) {
    return {
      projectId: match[1],
      notebookId: match[2]
    };
  }
  
  return null;
}

