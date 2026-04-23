/**
 * HTML Resource Resolver
 * 
 * Resolves relative resource URLs in HTML content to authenticated blob URLs.
 * Handles images, stylesheets, scripts, video, audio, and CSS url() references.
 */

import { api } from '../services/api';
import { API_BASE_URL } from '../config/apiConfig';
import { safeDecodeURIComponent } from './urlEncoding';

export interface HtmlResourceResolverOptions {
  /** The HTML content to process */
  html: string;
  /** Project ID for API calls */
  projectId: string;
  /** Notebook ID for notebook files (optional - if not provided, uses project file API) */
  notebookId?: string;
  /** Base path for resolving relative URLs (directory of the HTML file) */
  basePath?: string;
  /** File ID for project files (required when notebookId is not provided) */
  fileId?: string;
  /** Function to resolve relative path to file ID for project files */
  resolveProjectFilePath?: (relativePath: string) => string | undefined;
  /** Function to check if a file exists at a given path (for root-relative path resolution) */
  fileExists?: (path: string) => boolean;
}

export interface HtmlResourceResolverResult {
  /** Processed HTML with blob URLs */
  html: string;
  /** Set of blob URLs created (for cleanup) */
  blobUrls: Set<string>;
  /** Discovered site root from resolving root-relative paths (e.g., "site/dist") */
  discoveredRoot: string | null;
}

interface ResourceMatch {
  /** Full match string */
  match: string;
  /** Attribute name (src, href, etc.) */
  attribute: string;
  /** Original URL value */
  url: string;
  /** Full resolved path */
  fullPath: string;
  /** API URL to fetch the resource */
  apiUrl: string;
}

/**
 * Check if a URL needs resolution (relative path or root-relative path)
 * 
 * We need to resolve:
 * - Relative paths: "images/logo.png", "../styles/main.css"
 * - Root-relative paths: "/images/logo.png" (these resolve to page origin in srcdoc iframes)
 * 
 * We skip:
 * - Full URLs: "http://...", "https://..."
 * - Data URIs: "data:..."
 * - Protocol-relative: "//cdn.example.com/..."
 * - Blob URLs: "blob:..."
 * - Special protocols: "javascript:", "mailto:", "tel:"
 * - Hash links: "#section" (internal page anchors)
 * - Empty URLs
 */
function needsResolution(url: string): boolean {
  if (!url || url.trim() === '') return false;
  
  return !url.startsWith('http://') &&
         !url.startsWith('https://') &&
         !url.startsWith('data:') &&
         !url.startsWith('//') &&
         !url.startsWith('#') &&
         !url.startsWith('blob:') &&
         !url.startsWith('javascript:') &&
         !url.startsWith('mailto:') &&
         !url.startsWith('tel:');
}

/**
 * Resolve a RELATIVE path against a base path, handling ../ navigation
 * 
 * Only handles relative paths. Root-relative paths should be handled separately
 * by searching the ancestor chain with fileExists.
 */
function resolveRelativePath(inputPath: string, basePath?: string): string {
  // Remove query params for path resolution
  let pathPart = inputPath;
  const queryIndex = inputPath.indexOf('?');
  if (queryIndex >= 0) {
    pathPart = inputPath.substring(0, queryIndex);
  }

  // Remove ./ prefix
  pathPart = pathPart.replace(/^\.\//, '');

  if (!basePath) {
    return pathPart;
  }

  // Handle ../ navigation for relative paths
  const basePathParts = basePath.split('/').filter(Boolean);
  const pathSegments = pathPart.split('/');

  // Count and remove leading ../ segments
  while (pathSegments.length > 0 && pathSegments[0] === '..') {
    pathSegments.shift();
    basePathParts.pop();
  }

  // Combine remaining basePath with the path
  return [...basePathParts, ...pathSegments].join('/');
}

/**
 * Resolve a ROOT-RELATIVE path by walking up the ancestor chain
 * 
 * For a path like "/images/logo.png" and basePath "site/dist/pages/about",
 * this checks:
 * - site/dist/pages/about/images/logo.png
 * - site/dist/pages/images/logo.png
 * - site/dist/images/logo.png  <- returns this if found
 * - site/images/logo.png
 * - images/logo.png
 * 
 * Returns the first path where the file exists, or null if not found.
 */
function resolveRootRelativePath(
  inputPath: string, 
  basePath: string | undefined, 
  fileExists: ((path: string) => boolean) | undefined,
  discoveredRoot: { value: string | null }
): string | null {
  // Remove leading slash and query params
  let pathPart = inputPath.startsWith('/') ? inputPath.substring(1) : inputPath;
  const queryIndex = pathPart.indexOf('?');
  if (queryIndex >= 0) {
    pathPart = pathPart.substring(0, queryIndex);
  }

  // Handle "/" specifically - look for index.html
  if (pathPart === '' || pathPart === '/') {
    pathPart = 'index.html';
  }

  // If we've already discovered the root from a previous resource, use it
  if (discoveredRoot.value !== null) {
    const fullPath = discoveredRoot.value ? `${discoveredRoot.value}/${pathPart}` : pathPart;
    return fullPath;
  }

  // If no fileExists function provided, we can't search - return null
  if (!fileExists) {
    return null;
  }

  // Walk up the ancestor chain looking for the file
  const ancestors = basePath ? basePath.split('/').filter(Boolean) : [];
  
  // Try each ancestor level, from deepest to shallowest
  for (let i = ancestors.length; i >= 0; i--) {
    const potentialRoot = ancestors.slice(0, i).join('/');
    const fullPath = potentialRoot ? `${potentialRoot}/${pathPart}` : pathPart;
    
    if (fileExists(fullPath)) {
      // Remember this root for subsequent resolutions
      discoveredRoot.value = potentialRoot;
      return fullPath;
    }
  }

  // Not found anywhere - return null
  return null;
}

/**
 * Build API URL for a notebook file
 */
function buildNotebookFileUrl(projectId: string, notebookId: string, fullPath: string, queryPart?: string): string {
  const encodedPath = encodeURIComponent(safeDecodeURIComponent(fullPath));
  const apiBase = (API_BASE_URL || '').replace(/\/$/, '');
  let url = `${apiBase}/projects/${projectId}/notebooks/${notebookId}/files/content?path=${encodedPath}`;
  if (queryPart) {
    url += `&${queryPart}`;
  }
  return url;
}

/**
 * Build API URL for a project file
 */
function buildProjectFileUrl(projectId: string, fileId: string): string {
  const apiBase = (API_BASE_URL || '').replace(/\/$/, '');
  return `${apiBase}/projects/${projectId}/files/${fileId}/content`;
}

/**
 * Extract all resource references from HTML that need resolution
 */
function extractResourceReferences(
  html: string, 
  basePath: string | undefined,
  projectId: string,
  notebookId: string | undefined,
  resolveProjectFilePath?: (relativePath: string) => string | undefined,
  fileExists?: (path: string) => boolean
): { resources: ResourceMatch[]; discoveredRoot: string | null } {
  const resources: ResourceMatch[] = [];
  
  // Track discovered root for root-relative paths (shared across all resolutions)
  const discoveredRoot: { value: string | null } = { value: null };

  // Patterns for different resource types
  const patterns = [
    // <img src="...">
    { regex: /<img\s+[^>]*?src\s*=\s*["']([^"']+?)["'][^>]*?>/gi, attribute: 'src' },
    // <link href="..."> (stylesheets)
    { regex: /<link\s+[^>]*?href\s*=\s*["']([^"']+?)["'][^>]*?>/gi, attribute: 'href' },
    // <script src="...">
    { regex: /<script\s+[^>]*?src\s*=\s*["']([^"']+?)["'][^>]*?>/gi, attribute: 'src' },
    // <video src="...">
    { regex: /<video\s+[^>]*?src\s*=\s*["']([^"']+?)["'][^>]*?>/gi, attribute: 'src' },
    // <audio src="...">
    { regex: /<audio\s+[^>]*?src\s*=\s*["']([^"']+?)["'][^>]*?>/gi, attribute: 'src' },
    // <source src="..."> (inside video/audio)
    { regex: /<source\s+[^>]*?src\s*=\s*["']([^"']+?)["'][^>]*?>/gi, attribute: 'src' },
    // <video poster="...">
    { regex: /<video\s+[^>]*?poster\s*=\s*["']([^"']+?)["'][^>]*?>/gi, attribute: 'poster' },
    // <object data="...">
    { regex: /<object\s+[^>]*?data\s*=\s*["']([^"']+?)["'][^>]*?>/gi, attribute: 'data' },
    // <embed src="...">
    { regex: /<embed\s+[^>]*?src\s*=\s*["']([^"']+?)["'][^>]*?>/gi, attribute: 'src' },
    // <iframe src="...">
    { regex: /<iframe\s+[^>]*?src\s*=\s*["']([^"']+?)["'][^>]*?>/gi, attribute: 'src' },
    // NOTE: <a href> and <form action> are NOT included - they are navigation links
    // handled by the injected click interceptor script, not pre-fetched as resources
  ];

  for (const { regex, attribute } of patterns) {
    let match;
    while ((match = regex.exec(html)) !== null) {
      const [fullMatch, url] = match;
      
      if (!needsResolution(url)) {
        continue;
      }

      // Extract query part if present
      let queryPart = '';
      const queryIndex = url.indexOf('?');
      if (queryIndex >= 0) {
        queryPart = url.substring(queryIndex + 1);
      }

      // Resolve the path differently based on whether it's root-relative or relative
      let fullPath: string | null;
      if (url.startsWith('/')) {
        // Root-relative path - walk up ancestors to find the file
        fullPath = resolveRootRelativePath(url, basePath, fileExists, discoveredRoot);
      } else {
        // Relative path - resolve from basePath
        fullPath = resolveRelativePath(url, basePath);
      }
      
      // Skip if we couldn't resolve the path
      if (!fullPath) {
        continue;
      }

      let apiUrl: string | null = null;

      if (notebookId) {
        // Notebook file - use path-based API
        apiUrl = buildNotebookFileUrl(projectId, notebookId, fullPath, queryPart);
      } else if (resolveProjectFilePath) {
        // Project file - need to resolve path to file ID
        const fileId = resolveProjectFilePath(fullPath);
        if (fileId) {
          apiUrl = buildProjectFileUrl(projectId, fileId);
        }
      }

      if (apiUrl) {
        resources.push({
          match: fullMatch,
          attribute,
          url,
          fullPath,
          apiUrl,
        });
      }
    }
  }

  // Also handle CSS url() references in style attributes and style tags
  const cssUrlPattern = /url\(\s*["']?([^"')]+?)["']?\s*\)/gi;
  let cssMatch;
  while ((cssMatch = cssUrlPattern.exec(html)) !== null) {
    const [fullMatch, url] = cssMatch;
    
    if (!needsResolution(url)) {
      continue;
    }

    let queryPart = '';
    const queryIndex = url.indexOf('?');
    if (queryIndex >= 0) {
      queryPart = url.substring(queryIndex + 1);
    }

    // Resolve the path differently based on whether it's root-relative or relative
    let fullPath: string | null;
    if (url.startsWith('/')) {
      // Root-relative path - walk up ancestors to find the file
      fullPath = resolveRootRelativePath(url, basePath, fileExists, discoveredRoot);
    } else {
      // Relative path - resolve from basePath
      fullPath = resolveRelativePath(url, basePath);
    }
    
    // Skip if we couldn't resolve the path
    if (!fullPath) {
      continue;
    }

    let apiUrl: string | null = null;

    if (notebookId) {
      apiUrl = buildNotebookFileUrl(projectId, notebookId, fullPath, queryPart);
    } else if (resolveProjectFilePath) {
      const fileId = resolveProjectFilePath(fullPath);
      if (fileId) {
        apiUrl = buildProjectFileUrl(projectId, fileId);
      }
    }

    if (apiUrl) {
      resources.push({
        match: fullMatch,
        attribute: 'url()',
        url,
        fullPath,
        apiUrl,
      });
    }
  }

  return { resources, discoveredRoot: discoveredRoot.value };
}

/**
 * Resolve HTML resources to blob URLs
 * 
 * @param options - Resolution options
 * @returns Promise with processed HTML and blob URLs for cleanup
 */
export async function resolveHtmlResources(options: HtmlResourceResolverOptions): Promise<HtmlResourceResolverResult> {
  const { html, projectId, notebookId, basePath, resolveProjectFilePath, fileExists } = options;

  console.log('[HtmlResourceResolver] Starting resolution:', { projectId, notebookId, basePath });

  // Inject a script to handle navigation within srcdoc iframes
  // - Hash links (#anchor) scroll within the iframe
  // - Internal links (/path or relative paths) send a message to parent for handling
  // - External links (http://, https://) open in new tab
  const navigationFixScript = `<script>
document.addEventListener('click', function(e) {
  var link = e.target.closest('a[href]');
  if (!link) return;
  
  var href = link.getAttribute('href');
  if (!href) return;
  
  // Hash links - scroll within iframe
  if (href.startsWith('#')) {
    e.preventDefault();
    if (href.length > 1) {
      var element = document.querySelector(href) || document.querySelector('[id="' + href.slice(1) + '"]');
      if (element) {
        element.scrollIntoView({ behavior: 'smooth' });
      }
    }
    return;
  }
  
  // External links - let them open normally (in new tab due to sandbox)
  if (href.startsWith('http://') || href.startsWith('https://') || href.startsWith('//')) {
    link.setAttribute('target', '_blank');
    return;
  }
  
  // Skip special protocols
  if (href.startsWith('mailto:') || href.startsWith('tel:') || href.startsWith('javascript:') || href.startsWith('data:') || href.startsWith('blob:')) {
    return;
  }
  
  // Internal links - send message to parent to handle navigation
  e.preventDefault();
  window.parent.postMessage({ type: 'html-preview-navigate', href: href }, '*');
});
</script>`;
  
  // Helper to inject the navigation script into HTML
  const injectNavigationFix = (htmlContent: string): string => {
    if (htmlContent.includes('</body>')) {
      return htmlContent.replace('</body>', navigationFixScript + '</body>');
    } else if (htmlContent.includes('</html>')) {
      return htmlContent.replace('</html>', navigationFixScript + '</html>');
    } else {
      return htmlContent + navigationFixScript;
    }
  };

  // Extract all resource references
  const { resources, discoveredRoot } = extractResourceReferences(html, basePath, projectId, notebookId, resolveProjectFilePath, fileExists);

  console.log('[HtmlResourceResolver] Found', resources.length, 'resources to resolve, discoveredRoot:', discoveredRoot);
  if (resources.length > 0) {
    console.log('[HtmlResourceResolver] First few resources:', resources.slice(0, 5).map(r => ({
      url: r.url,
      fullPath: r.fullPath,
      apiUrl: r.apiUrl,
    })));
  }

  if (resources.length === 0) {
    return { html: injectNavigationFix(html), blobUrls: new Set(), discoveredRoot };
  }

  // Fetch all resources in parallel
  const fetchResults = await Promise.all(
    resources.map(async (resource) => {
      try {
        const result = await api.utils.getAuthenticatedUrl(resource.apiUrl);
        return { resource, blobUrl: result.objectUrl, error: null };
      } catch (error) {
        console.warn('[HtmlResourceResolver] Failed to fetch:', resource.apiUrl, error);
        return { resource, blobUrl: null, error };
      }
    })
  );

  const successCount = fetchResults.filter(r => r.blobUrl).length;
  console.log('[HtmlResourceResolver] Fetch complete:', successCount, 'of', resources.length, 'succeeded');

  // Build URL map and collect blob URLs
  const blobUrls = new Set<string>();
  const urlMap = new Map<string, { resource: ResourceMatch; blobUrl: string | null }>();
  
  for (const result of fetchResults) {
    urlMap.set(result.resource.apiUrl, { resource: result.resource, blobUrl: result.blobUrl });
    if (result.blobUrl) {
      blobUrls.add(result.blobUrl);
    }
  }

  // Replace URLs in HTML
  let processedHtml = html;
  
  for (const { resource, blobUrl } of urlMap.values()) {
    if (!blobUrl) continue;

    if (resource.attribute === 'url()') {
      // CSS url() replacement
      // Need to handle both url("path") and url('path') and url(path)
      const escapedUrl = resource.url.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      const cssUrlRegex = new RegExp(`url\\(\\s*["']?${escapedUrl}["']?\\s*\\)`, 'g');
      processedHtml = processedHtml.replace(cssUrlRegex, `url("${blobUrl}")`);
    } else {
      // HTML attribute replacement
      // Replace the specific URL in the matched tag
      const escapedUrl = resource.url.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      const attrRegex = new RegExp(`(${resource.attribute}\\s*=\\s*["'])${escapedUrl}(["'])`, 'g');
      processedHtml = processedHtml.replace(attrRegex, `$1${blobUrl}$2`);
    }
  }

  return { html: injectNavigationFix(processedHtml), blobUrls, discoveredRoot };
}

/**
 * Cleanup blob URLs created by the resolver
 */
export function cleanupBlobUrls(blobUrls: Set<string>): void {
  for (const url of blobUrls) {
    try {
      URL.revokeObjectURL(url);
    } catch (e) {
      // Ignore errors when revoking
    }
  }
}

