import React, { useState, useEffect, useMemo, useCallback } from 'react';
import { createPortal } from 'react-dom';
import ReactMarkdown, { defaultUrlTransform } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { FaTimes } from 'react-icons/fa';
import MermaidRenderer from '../../common/MermaidRenderer';
import { api } from '../../../services/api';
import { API_BASE_URL, getApiHost } from '../../../config/apiConfig';
import ImageFullscreenViewer from './ImageFullscreenViewer';
import { encodeMarkdownLinkSpaces } from '../../../utils/markdownLinkEncoding';
import { safeDecodeURIComponent } from '../../../utils/urlEncoding';

// Convert HTML video/audio tags to a format ReactMarkdown can handle
function preprocessHtmlMediaTags(text: string): string {
  // Replace complete video elements (including those with source children)
  text = text.replace(/<video\s+[^>]*?>(.*?)<\/video>/gis, (match, content) => {
    // First try to find src attribute on the video tag itself
    const videoSrcMatch = match.match(/<video\s+[^>]*?src\s*=\s*["']([^"']*?)["']/i);
    if (videoSrcMatch) {
      return `[VIDEO:${videoSrcMatch[1]}]`;
    }
    
    // Then try to find src in source child elements
    const sourceSrcMatch = content.match(/<source\s+[^>]*?src\s*=\s*["']([^"']*?)["']/i);
    if (sourceSrcMatch) {
      return `[VIDEO:${sourceSrcMatch[1]}]`;
    }
    
    return match;
  });
  
  // Replace complete audio elements (including those with source children)
  text = text.replace(/<audio\s+[^>]*?>(.*?)<\/audio>/gis, (match, content) => {
    // First try to find src attribute on the audio tag itself
    const audioSrcMatch = match.match(/<audio\s+[^>]*?src\s*=\s*["']([^"']*?)["']/i);
    if (audioSrcMatch) {
      return `[AUDIO:${audioSrcMatch[1]}]`;
    }
    
    // Then try to find src in source child elements
    const sourceSrcMatch = content.match(/<source\s+[^>]*?src\s*=\s*["']([^"']*?)["']/i);
    if (sourceSrcMatch) {
      return `[AUDIO:${sourceSrcMatch[1]}]`;
    }
    
    return match;
  });
  
  return text;
}

// Lightweight preprocessing to fix only the most common AI output issue:
// unindented bullets immediately following numbered items
function normalizeOrderedListStructure(text: string): string {
  const lines = text.split('\n');
  const out: string[] = [];
  let insideCodeFence = false;

  const isNumbered = (s: string) => /^(\d+)[\.\)]\s+/.test(s);
  const isBullet = (s: string) => /^[-*+]\s/.test(s);

  const isFence = (s: string) => /^```/.test(s);

  for (let i = 0; i < lines.length; i++) {
    const raw = lines[i];
    const trimmed = raw.trim();

    if (isFence(trimmed)) {
      out.push(raw);
      insideCodeFence = !insideCodeFence;
      continue;
    }
    if (insideCodeFence) {
      out.push(raw);
      continue;
    }

    // Only fix unindented bullets that immediately follow a numbered item
    if (isBullet(trimmed) && !raw.startsWith('  ')) {
      // Look back to see if the previous non-empty line was a numbered item
      let foundNumberedItem = false;
      for (let j = i - 1; j >= 0; j--) {
        const prevLine = lines[j].trim();
        if (prevLine === '') continue; // Skip empty lines
        if (isNumbered(prevLine)) {
          foundNumberedItem = true;
          break;
        }
        // Stop looking if we hit anything else
        break;
      }
      
      if (foundNumberedItem) {
        // Indent this bullet to nest it under the numbered item
        out.push('   ' + trimmed);
      } else {
        out.push(raw);
      }
    } else {
      out.push(raw);
    }
  }

  return out.join('\n');
}

// Remark plugin: if a top-level unordered list (or paragraph) immediately follows
// an ordered list, move it under the last listItem of that ordered list.
// This fixes AI output where bullets are siblings of numbered items.
function remarkNestFollowingListsUnderOrderedItems() {
  return function transformer(tree: any) {
    if (!tree || !Array.isArray((tree as any).children)) return;
    const root: any = tree;
    const children = root.children as any[];
    let i = 0;
    while (i < children.length) {
      const node = children[i];
      if (node?.type === 'list' && node.ordered && Array.isArray(node.children) && node.children.length > 0) {
        // Find the last listItem of this ordered list
        const lastLi = node.children[node.children.length - 1];
        // Gather consecutive paragraphs or unordered lists following the ordered list
        let j = i + 1;
        const toMove: any[] = [];
        while (j < children.length) {
          const next = children[j];
          if (!next) break;
          const isParagraph = next.type === 'paragraph';
          const isUnorderedList = next.type === 'list' && !next.ordered;
          // Stop if another ordered list or a heading starts
          const isStop = next.type === 'list' && next.ordered || /^heading$/.test(next.type);
          if (isStop) break;
          if (isParagraph || isUnorderedList) {
            toMove.push(next);
            j++;
            continue;
          }
          break;
        }
        if (toMove.length > 0) {
          // Ensure last list item has a children array
          lastLi.children = lastLi.children || [];
          // If last child is a paragraph and next is a list, keep order; just append all
          for (const m of toMove) {
            lastLi.children.push(m);
          }
          // Remove moved nodes from root
          children.splice(i + 1, toMove.length);
          // Continue after the ordered list (now with nested content)
          i++; 
          continue;
        }
      }
      i++;
    }
  };
}

// Remark plugin: merge adjacent ordered lists into a single list so numbering
// continues (avoids multiple <ol> siblings each restarting at 1).
function remarkMergeAdjacentOrderedLists() {
  function mergeInParent(parent: any) {
    if (!parent || !Array.isArray(parent.children)) return;
    const arr = parent.children as any[];
    let i = 0;
    while (i < arr.length - 1) {
      const a = arr[i];
      const b = arr[i + 1];
      if (a?.type === 'list' && a.ordered && b?.type === 'list' && b.ordered) {
        // Move b's items into a and remove b
        a.children = (a.children || []).concat(b.children || []);
        arr.splice(i + 1, 1);
        // Do not advance i so we can merge more that follow
        continue;
      }
      i++;
    }
    // Recurse into children to catch nested cases
    for (const child of arr) mergeInParent(child);
  }

  return function transformer(tree: any) {
    mergeInParent(tree);
  };
}

/**
 * Remark plugin: converts single newlines in text nodes to break nodes.
 * This renders as <br> instead of collapsing newlines, matching the behavior
 * of remark-breaks without adding a dependency.
 * 
 * Benefits:
 * - Content with single \n (from minimal client) displays correctly
 * - Lines stay in same <p> element, so copying doesn't add extra breaks
 */
function remarkBreaks() {
  return function transformer(tree: any) {
    function walk(node: any) {
      if (!node.children) return;
      
      const newChildren: any[] = [];
      for (const child of node.children) {
        if (child.type === 'text' && child.value.includes('\n')) {
          const parts = child.value.split('\n');
          parts.forEach((part: string, i: number) => {
            if (i > 0) newChildren.push({ type: 'break' });
            if (part) newChildren.push({ type: 'text', value: part });
          });
        } else {
          walk(child);
          newChildren.push(child);
        }
      }
      node.children = newChildren;
    }
    walk(tree);
  };
}

function normalizeInlineImageDataUrl(url: string): string {
  let normalized = url.trim();
  if ((normalized.startsWith('<') && normalized.endsWith('>')) ||
      (normalized.startsWith('"') && normalized.endsWith('"')) ||
      (normalized.startsWith("'") && normalized.endsWith("'"))) {
    normalized = normalized.substring(1, normalized.length - 1).trim();
  }

  if (!normalized.includes(',') && /%2c/i.test(normalized)) {
    normalized = normalized.replace(/%2c/i, ',');
  }

  const commaIndex = normalized.indexOf(',');
  if (commaIndex < 0) return normalized;

  const header = normalized.substring(0, commaIndex + 1);
  const payload = normalized
    .substring(commaIndex + 1)
    .replace(/\s+/g, '')
    .replace(/\\[rn]/gi, '')
    .replace(/%(?:0a|0d)/gi, '');
  return `${header}${payload}`;
}

function isInlineImageDataUrl(url: string): boolean {
  const normalized = normalizeInlineImageDataUrl(url).toLowerCase();
  return normalized.startsWith('data:image/') && normalized.includes(',');
}

function markdownUrlTransform(url: string, key: string): string {
  if (key === 'src' && isInlineImageDataUrl(url)) {
    return normalizeInlineImageDataUrl(url);
  }

  return defaultUrlTransform(url);
}

export interface ChatMarkdownViewerProps {
  /** Markdown source text */
  text: string;
  /** Optional TailwindCSS classes applied to the container */
  className?: string;
  /** Render viewer in fullscreen overlay */
  isFullScreen?: boolean;
  /** When true, content is currently streaming; suppress error states */
  isStreaming?: boolean;
  /** Callback when overlay close button clicked */
  onExitFullScreen?: () => void;
  /** Optional additional controls rendered in the top-right corner of the overlay */
  overlayControls?: React.ReactNode;
  /** Optional maximum height for images (e.g. 120 or '120px') */
  maxImageHeight?: number | string;
  /** Enable click-to-fullscreen for images (default: true) */
  enableImageFullscreen?: boolean;
  /** Project ID for resolving relative file paths to authenticated URLs */
  projectId?: string;
  /** Notebook ID for resolving relative file paths to authenticated URLs */
  notebookId?: string;
  /** Base path for resolving relative URLs (directory of the markdown file, e.g., "Output" or "docs/guides") */
  basePath?: string;
  /** Callback for when a relative link to a notebook file is clicked */
  onLinkClick?: (path: string) => void;
  /** Files modified during this turn - cache will be invalidated for these to show fresh versions */
  turnFilesModified?: string[];
  /** Files created during this turn - cache will be invalidated for these to show fresh versions */
  turnFilesCreated?: string[];
}

// Custom link component that handles external links properly in Electron
const ExternalLink: React.FC<{ href?: string; children: React.ReactNode }> = ({ href, children }) => {
    const isApiHref = (raw?: string): boolean => {
        if (!raw) return false;
        try {
            const u = new URL(raw, window.location.origin);
            if (u.pathname.startsWith('/api/')) {
                const apiHost = getApiHost(API_BASE_URL);
                return u.host === window.location.host || u.host === apiHost;
            }
        } catch {}
        return raw.startsWith(API_BASE_URL);
    };

    const toApiUrl = (raw: string): string => {
        try {
            const u = new URL(raw, window.location.origin);
            if (u.pathname.startsWith('/api/')) {
                const after = u.pathname.replace(/^\/api\//, '');
                const base = (API_BASE_URL || '').replace(/\/$/, '');
                return `${base}/${after}${u.search}`;
            }
        } catch {}
        return raw;
    };

    const handleClick = async (e: React.MouseEvent) => {
        if (!href) return;
        e.preventDefault();

        // If this points at our API, fetch an authenticated blob and trigger download/new tab
        if (isApiHref(href)) {
            try {
                const effective = toApiUrl(href);
                const result = await api.utils.getAuthenticatedUrl(effective);
                const a = document.createElement('a');
                a.href = result.objectUrl;
                a.target = '_blank';
                a.rel = 'noopener noreferrer';
                if (result.fileName) a.download = result.fileName;
                document.body.appendChild(a);
                a.click();
                document.body.removeChild(a);
                return;
            } catch {}
        }

        // Non-API links: Electron or browser open
        const openExternal = (window as any).electron?.openExternal as ((url: string) => void) | undefined;
        if (openExternal) {
            try { openExternal(href); } catch {}
            return;
        }

        try {
            window.open(href, '_blank', 'noopener,noreferrer');
        } catch {
            try { (e.currentTarget as HTMLAnchorElement).target = '_blank'; } catch {}
        }
    };

    return (
        <a
            href={href}
            onClick={handleClick}
            className="text-blue-600 hover:text-blue-800 underline cursor-pointer"
            target="_blank"
            rel="noopener noreferrer"
        >
            {children}
        </a>
    );
};

// Cache for authenticated image URLs => blob object URLs (lives for app session)
const authenticatedBlobCache = new Map<string, string>();

/**
 * Invalidate cache entries for specific file paths.
 * Call this when files are modified to ensure fresh versions are fetched.
 * 
 * NOTE: We intentionally do NOT revoke the blob URLs because previous cells
 * may still have <img> elements using those URLs. Revoking would break their display.
 * The old blobs will be garbage collected when no longer referenced.
 */
export function invalidateImageCacheForPaths(paths: string[], projectId?: string, notebookId?: string) {
    if (!paths.length || !projectId || !notebookId) return;
    
    const apiBase = (API_BASE_URL || '').replace(/\/$/, '');
    
    for (const path of paths) {
        // Build the URL pattern that would be cached
        const encodedPath = encodeURIComponent(safeDecodeURIComponent(path));
        const urlPattern = `${apiBase}/projects/${projectId}/notebooks/${notebookId}/files/content?path=${encodedPath}`;
        
        // Remove from cache but don't revoke - previous cells still need the blob
        authenticatedBlobCache.delete(urlPattern);
        
        // Also check for URLs with query params (e.g., ?m=timestamp)
        for (const cachedUrl of authenticatedBlobCache.keys()) {
            if (cachedUrl.startsWith(urlPattern + '&') || cachedUrl.startsWith(urlPattern + '?')) {
                authenticatedBlobCache.delete(cachedUrl);
            }
        }
    }
}

// Component to handle authenticated URLs for images, links, video, and audio
const AuthenticatedContent: React.FC<{ 
    src?: string; 
    href?: string; 
    alt?: string; 
    title?: string;
    children?: React.ReactNode;
    elementType: 'img' | 'a' | 'video' | 'audio';
    isStreaming?: boolean;
    imgMaxHeight?: number | string;
    onImageClick?: (src: string, alt?: string) => void;
    projectId?: string;
    notebookId?: string;
    basePath?: string;
    onLinkClick?: (path: string) => void;
}> = ({ src, href, alt, title, children, elementType, isStreaming = false, imgMaxHeight, onImageClick, projectId, notebookId, basePath, onLinkClick }) => {
    // Extract alignment token and clean pipe-suffixed tokens from URL
    const original = src || href;
    let cleanedSource = original;
    let alignment: 'left' | 'right' | 'center' | undefined;
    if (original) {
        const alignToken = original.match(/(?:\||%7C)align=(right|left|center)/i);
        if (alignToken) {
            alignment = (alignToken[1].toLowerCase() as 'left' | 'right' | 'center');
        }
        const pipeIndex = original.indexOf('|');
        const encPipeIndex = original.toLowerCase().indexOf('%7c');
        const cutIndex = [pipeIndex, encPipeIndex].filter(i => i >= 0).sort((a, b) => a - b)[0];
        if (cutIndex !== undefined) {
            cleanedSource = original.substring(0, cutIndex);
        }
    }
    // Normalize common escaped sequences coming from JSON/SSE (e.g. "\u0026" or "%5Cu0026")
    const normalizeUrl = (value?: string): string | undefined => {
        if (!value) return value;
        // Fix JSON-escaped ampersands that should delimit query params
        return value
            .replace(/%5Cu0026/gi, '&')
            .replace(/\\u0026/gi, '&');
    };

    // Resolve relative paths to absolute authenticated URLs
    const resolveNotebookPath = (value: string): string | null => {
        // Check if it's a relative path (not a URL with protocol or absolute path)
        const isRelative = !value.startsWith('http://') && 
                          !value.startsWith('https://') && 
                          !value.startsWith('/') &&
                          !value.startsWith('mailto:') &&
                          !value.startsWith('tel:') &&
                          !value.startsWith('data:');
        
        if (!isRelative || !projectId || !notebookId) return null;

        // Remove ./ prefix but keep track of ../ for parent directory navigation
        let cleanValue = value.replace(/^\.\//, '');
        
        // Separate path from query string (e.g., "image.png?m=12345")
        let pathPart = cleanValue;
        const queryIndex = cleanValue.indexOf('?');
        if (queryIndex >= 0) {
            pathPart = cleanValue.substring(0, queryIndex);
        }
        
        // Prepend basePath if provided
        let fullPath = pathPart;
        if (basePath) {
            const basePathParts = basePath.split('/').filter(Boolean);
            const pathSegments = pathPart.split('/');
            
            while (pathSegments.length > 0 && pathSegments[0] === '..') {
                pathSegments.shift();
                basePathParts.pop();
            }
            
            fullPath = [...basePathParts, ...pathSegments].join('/');
        }
        
        return fullPath;
    };

    const resolveUrl = (value?: string): string | undefined => {
        if (!value) return value;
        
        const notebookPath = resolveNotebookPath(value);
        if (notebookPath) {
             const encodedPath = encodeURIComponent(safeDecodeURIComponent(notebookPath));
             const apiBase = (API_BASE_URL || '').replace(/\/$/, '');
             let resolvedUrl = `${apiBase}/projects/${projectId}/notebooks/${notebookId}/files/content?path=${encodedPath}`;
             
             // Append original query params if any
             const queryIndex = value.indexOf('?');
             if (queryIndex >= 0) {
                 resolvedUrl += `&${value.substring(queryIndex + 1)}`;
             }
             
             return resolvedUrl;
        }
        
        // If relative but no notebookId (project file), cannot resolve
        const isRelative = !value.startsWith('http://') && !value.startsWith('https://') && !value.startsWith('/');
        if (isRelative && projectId && !notebookId) {
            console.error('[ChatMarkdownViewer] Relative URLs are not supported for project files:', value);
            return undefined;
        }
        
        return value;
    };

    const url = normalizeUrl(resolveUrl(cleanedSource));
    const [objectUrl, setObjectUrl] = useState<string | null>(null);
    const [isLoading, setIsLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [retryCount, setRetryCount] = useState(0);

    const apiBaseUrl = API_BASE_URL;
    const isAuthenticatedUrl = (() => {
        if (!url) return false;
        try {
            const u = new URL(url, window.location.origin);
            // Only treat /api/ paths as authenticated when they are on the same
            // origin or the configured API base host — not arbitrary third-party
            // hosts that happen to have "/api/" in their path (e.g. media.cnn.com).
            if (u.pathname.startsWith('/api/')) {
                const apiHost = getApiHost(apiBaseUrl);
                if (u.host === window.location.host || u.host === apiHost) return true;
                return false;
            }
        } catch {
            // ignore
        }
        // Also allow explicit API base URL prefix
        return url.startsWith(apiBaseUrl);
    })();

    // Helper function to detect likely malformed URLs during streaming
    const isLikelyMalformedUrl = (url: string): boolean => {
        if (!url) return true;
        
        // Common patterns of incomplete URLs during streaming
        const malformedPatterns = [
            /^https?:\/\/[^\/]*$/,  // Just protocol + domain, no path
            /\.\.\./,               // Contains ellipsis (truncated)
            /\s/,                   // Contains whitespace
            /[<>{}|\\^`\[\]]/,     // Contains invalid URL characters
            /^https?:\/\/[^\/]*\/?$/, // Just domain with optional trailing slash
            /%5Cu0026/i,            // JSON-escaped ampersand still percent-encoded
            /\\u0026/i,             // Literal "\u0026" sequence
        ];
        
        return malformedPatterns.some(pattern => pattern.test(url));
    };

    useEffect(() => {
        if (!url || !isAuthenticatedUrl) {
            setObjectUrl(url || null);
            return;
        }

        if ((elementType === 'img' || elementType === 'video' || elementType === 'audio') && url) {
            // Reset states when URL changes
            setError(null);
            
            // If URL looks malformed, wait before attempting to load
            if (isLikelyMalformedUrl(url)) {
                setIsLoading(true);
                // Set a timeout to retry after the URL might be complete
                const retryTimeout = setTimeout(() => {
                    if (!isLikelyMalformedUrl(url)) {
                        // URL looks better now, try loading
                        loadAuthenticatedMedia(url);
                    } else if (retryCount < 3) {
                        // Still looks bad, increment retry count and try again later
                        setRetryCount(prev => prev + 1);
                    } else {
                        // Too many retries, show minimal error
                        setIsLoading(false);
                        setError('Invalid image URL');
                    }
                }, Math.min(500 * (retryCount + 1), 2000)); // Exponential backoff
                
                return () => clearTimeout(retryTimeout);
            } else {
                // URL looks good, load immediately
                loadAuthenticatedMedia(url);
            }
        }

        return () => {
            // Do not revoke cached object URLs—they may be reused by other mounts
        };
    }, [url, isAuthenticatedUrl, elementType, retryCount]);

    const toApiUrl = (originalUrl: string): string => {
        try {
            const u = new URL(originalUrl, window.location.origin);
            if (u.pathname.startsWith('/api/')) {
                const after = u.pathname.replace(/^\/api\//, '');
                const base = (API_BASE_URL || '').replace(/\/$/, '');
                return `${base}/${after}${u.search}`;
            }
        } catch {}
        return originalUrl;
    };

    const loadAuthenticatedMedia = (mediaUrl: string, fetchAttempt: number = 0) => {
        const effectiveUrl = toApiUrl(mediaUrl);
        // If we've already fetched this media once, reuse cached blob URL
        const cached = authenticatedBlobCache.get(effectiveUrl);
        if (cached) {
            setObjectUrl(cached);
            setIsLoading(false);
            return;
        }

        setIsLoading(true);
        api.utils.getAuthenticatedUrl(effectiveUrl)
            .then(result => {
                authenticatedBlobCache.set(effectiveUrl, result.objectUrl);
                setObjectUrl(result.objectUrl);
                setError(null); // Clear any previous errors
                setIsLoading(false);
            })
            .catch(err => {
                const status = (err as any).status;
                // Retry on transient errors (404 = not ready yet, 5xx = server error)
                // but NOT on 401/403 (auth errors)
                const isTransientError = status === 404 || (status >= 500 && status < 600);
                const maxFetchRetries = 3;
                
                if (isTransientError && fetchAttempt < maxFetchRetries) {
                    // Exponential backoff: 500ms, 1000ms, 1500ms
                    const delay = 500 * (fetchAttempt + 1);
                    setTimeout(() => {
                        loadAuthenticatedMedia(mediaUrl, fetchAttempt + 1);
                    }, delay);
                    return; // Don't set error or clear loading yet
                }
                
                // For malformed URLs during streaming, also allow retries via retryCount
                if (isLikelyMalformedUrl(mediaUrl) && retryCount < 3) {
                    setError(null);
                } else {
                    setError(err.message);
                }
                setIsLoading(false);
            });
    };

    // For non-authenticated URLs, render normally
    const alignClass = alignment === 'right'
        ? 'float-right ml-4 mb-2'
        : alignment === 'left'
            ? 'float-left mr-4 mb-2'
            : alignment === 'center'
                ? 'mx-auto block my-2'
                : '';

    if (!isAuthenticatedUrl) {
        if (elementType === 'img') {
            const style = imgMaxHeight !== undefined
                ? { maxHeight: typeof imgMaxHeight === 'number' ? `${imgMaxHeight}px` : imgMaxHeight }
                : undefined;
            const isClickable = !!onImageClick;
            return (
                <img 
                    src={cleanedSource} 
                    alt={alt} 
                    title={title} 
                    className={`max-w-full h-auto ${alignClass} ${isClickable ? 'cursor-pointer hover:opacity-90 transition-opacity' : ''}`.trim()}
                    style={style}
                    onClick={isClickable && src ? () => onImageClick(src, alt) : undefined}
                />
            );
        } else if (elementType === 'video') {
            return <video src={src} controls className="max-w-full h-auto" />;
        } else if (elementType === 'audio') {
            return <audio src={src} controls className="max-w-full" />;
        } else {
            return <ExternalLink href={href}>{children}</ExternalLink>;
        }
    }

    if (elementType === 'img') {
        // During streaming, always show a gentle indicator until we have an objectUrl
        // Use span instead of div to avoid invalid HTML nesting when inside <p> tags
        if (isStreaming && !objectUrl) {
            return (
                <span className="inline-flex items-center space-x-1 px-2 py-1 bg-blue-50 border border-blue-200 rounded text-xs text-blue-600">
                    <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16l4.586-4.586a2 2 0 012.828 0L16 16m-2-2l1.586-1.586a2 2 0 012.828 0L20 14m-6-6h.01M6 20h12a2 2 0 002-2V6a2 2 0 00-2-2H6a2 2 0 00-2 2v12a2 2 0 002 2z" />
                    </svg>
                    <span>Image coming up...</span>
                </span>
            );
        }
        
        if (isLoading) {
            // Use span instead of div to avoid invalid HTML nesting when inside <p> tags
            return (
                <span className="inline-flex items-center space-x-1 px-2 py-1 bg-blue-50 border border-blue-200 rounded text-xs text-blue-600">
                    <span className="inline-block w-3 h-3 border-2 border-blue-200 border-t-blue-600 rounded-full animate-spin"></span>
                    <span>Loading image...</span>
                </span>
            );
        }
        
        if (error) {
            // Use span instead of div to avoid invalid HTML nesting when inside <p> tags
            return (
                <span className="inline-flex items-center space-x-1 px-2 py-1 bg-red-50 border border-red-200 rounded text-xs text-red-600">
                    <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L3.732 16.5c-.77.833.192 2.5 1.732 2.5z" />
                    </svg>
                    <span>Image unavailable</span>
                </span>
            );
        }
        
        const style = imgMaxHeight !== undefined
            ? { maxHeight: typeof imgMaxHeight === 'number' ? `${imgMaxHeight}px` : imgMaxHeight }
            : undefined;
        const isClickable = !!onImageClick;
        return objectUrl ? (
            <img 
                src={objectUrl} 
                alt={alt} 
                title={title} 
                className={`max-w-full h-auto ${alignClass} ${isClickable ? 'cursor-pointer hover:opacity-90 transition-opacity' : ''}`.trim()}
                style={style}
                onClick={isClickable ? () => onImageClick(objectUrl, alt) : undefined}
            />
        ) : null;
    }

    if (elementType === 'video') {
        // Use span instead of div to avoid invalid HTML nesting when inside <p> tags
        if (isStreaming && !objectUrl) {
            return (
                <span className="inline-flex items-center space-x-1 px-2 py-1 bg-blue-50 border border-blue-200 rounded text-xs text-blue-600">
                    <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 10l4.553-2.276A1 1 0 0121 8.618v6.764a1 1 0 01-1.447.894L15 14M5 18h8a2 2 0 002-2V8a2 2 0 00-2-2H5a2 2 0 00-2 2v8a2 2 0 002 2z" />
                    </svg>
                    <span>Video coming up...</span>
                </span>
            );
        }
        
        if (isLoading) {
            return (
                <span className="inline-flex items-center space-x-1 px-2 py-1 bg-blue-50 border border-blue-200 rounded text-xs text-blue-600">
                    <span className="inline-block w-3 h-3 border-2 border-blue-200 border-t-blue-600 rounded-full animate-spin"></span>
                    <span>Loading video...</span>
                </span>
            );
        }
        
        if (error) {
            return (
                <span className="inline-flex items-center space-x-1 px-2 py-1 bg-red-50 border border-red-200 rounded text-xs text-red-600">
                    <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L3.732 16.5c-.77.833.192 2.5 1.732 2.5z" />
                    </svg>
                    <span>Video unavailable</span>
                </span>
            );
        }
        
        return objectUrl ? <video src={objectUrl} controls className="max-w-full h-auto" /> : null;
    }

    if (elementType === 'audio') {
        // Use span instead of div to avoid invalid HTML nesting when inside <p> tags
        if (isStreaming && !objectUrl) {
            return (
                <span className="inline-flex items-center space-x-1 px-2 py-1 bg-blue-50 border border-blue-200 rounded text-xs text-blue-600">
                    <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15.536 8.464a5 5 0 010 7.072m2.828-9.9a9 9 0 010 12.728M5.586 15H4a1 1 0 01-1-1v-4a1 1 0 011-1h1.586l4.707-4.707C10.923 3.663 12 4.109 12 5v14c0 .891-1.077 1.337-1.707.707L5.586 15z" />
                    </svg>
                    <span>Audio coming up...</span>
                </span>
            );
        }
        
        if (isLoading) {
            return (
                <span className="inline-flex items-center space-x-1 px-2 py-1 bg-blue-50 border border-blue-200 rounded text-xs text-blue-600">
                    <span className="inline-block w-3 h-3 border-2 border-blue-200 border-t-blue-600 rounded-full animate-spin"></span>
                    <span>Loading audio...</span>
                </span>
            );
        }
        
        if (error) {
            return (
                <span className="inline-flex items-center space-x-1 px-2 py-1 bg-red-50 border border-red-200 rounded text-xs text-red-600">
                    <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L3.732 16.5c-.77.833.192 2.5 1.732 2.5z" />
                    </svg>
                    <span>Audio unavailable</span>
                </span>
            );
        }
        
        return objectUrl ? <audio src={objectUrl} controls className="max-w-full" /> : null;
    }

    // For authenticated links, show the resolved URL in href so browser displays it correctly
    // The onClick will handle the actual authenticated download OR preview
    const handleAuthenticatedLink = async (e: React.MouseEvent) => {
        e.preventDefault();
        
        // Try to resolve as notebook path first to use preview
        if (onLinkClick && cleanedSource) {
            const notebookPath = resolveNotebookPath(cleanedSource);
            if (notebookPath) {
                try {
                    onLinkClick(decodeURIComponent(notebookPath));
                } catch {
                    onLinkClick(notebookPath);
                }
                return;
            }
        }

        if (!url) return;
        
        try {
            const result = await api.utils.getAuthenticatedUrl(url);
            const link = document.createElement('a');
            link.href = result.objectUrl;
            link.download = result.fileName;
            link.click();
            URL.revokeObjectURL(result.objectUrl);
        } catch (err) {
            console.error('Failed to download:', err);
        }
    };

    return (
        <a 
            href={url || "#"} 
            onClick={handleAuthenticatedLink} 
            className="text-blue-600 hover:text-blue-800 underline"
            target="_blank"
            rel="noopener noreferrer"
        >
            {children}
        </a>
    );
};

/**
 * ChatMarkdownViewer is a lightweight variant of the global MarkdownViewer that is
 * purpose-built for rendering chat message content inside conversation cells.
 *
 * Differences from the global MarkdownViewer:
 * 1. Omits the PreviewContainer banner and full-screen toggle used in file previews.
 * 2. Applies a selectable text style by default to allow users to copy content easily.
 *
 * All other behaviour (GFM support, Mermaid diagram rendering) is identical.
 */
export default function ChatMarkdownViewer({ text, className = '', isFullScreen = false, isStreaming = false, onExitFullScreen, overlayControls, maxImageHeight, enableImageFullscreen = true, projectId, notebookId, basePath, onLinkClick, turnFilesModified, turnFilesCreated }: ChatMarkdownViewerProps) {
  const combinedClass = `${className} select-text max-w-full overflow-hidden`.trim();

  // Invalidate cache for modified/created files before rendering
  // This ensures that when an assistant overwrites an image, the new version is fetched
  useEffect(() => {
    if (projectId && notebookId) {
      const pathsToInvalidate = [
        ...(turnFilesModified || []),
        ...(turnFilesCreated || [])
      ];
      if (pathsToInvalidate.length > 0) {
        invalidateImageCacheForPaths(pathsToInvalidate, projectId, notebookId);
      }
    }
  }, [turnFilesModified, turnFilesCreated, projectId, notebookId]);

  // State for fullscreen image viewer
  const [fullscreenImage, setFullscreenImage] = useState<{ src: string; alt?: string } | null>(null);

  // Handler to open image in fullscreen
  const handleImageClick = useCallback((src: string, alt?: string) => {
    if (enableImageFullscreen) {
      setFullscreenImage({ src, alt });
    }
  }, [enableImageFullscreen]);

  // Preprocess markdown to handle HTML media tags and fix list structure
  const processedText = encodeMarkdownLinkSpaces(
    normalizeOrderedListStructure(preprocessHtmlMediaTags(text))
  );

  // Memoize components to prevent full tree remounts on every render
  const markdownComponents = useMemo(() => ({
    h1({ children }: { children: React.ReactNode }) {
      return <h1 className="text-2xl font-bold mb-2">{children}</h1>;
    },
    h2({ children }: { children: React.ReactNode }) {
      return <h2 className="text-xl font-semibold mb-2">{children}</h2>;
    },
    h3({ children }: { children: React.ReactNode }) {
      return <h3 className="text-lg font-semibold mb-2">{children}</h3>;
    },
    h4({ children }: { children: React.ReactNode }) {
      return <h4 className="text-base font-semibold mb-2">{children}</h4>;
    },
    h5({ children }: { children: React.ReactNode }) {
      return <h5 className="text-sm font-semibold mb-1">{children}</h5>;
    },
    p({ node, children }: { node?: any; children: React.ReactNode }) {
      // Build a safe plain-text representation of the paragraph from the AST
      const buildText = (n: any): string => {
        if (!n) return '';
        if (typeof n === 'string') return n;
        if (Array.isArray(n)) return n.map(buildText).join('');
        if (n.type === 'text') return String(n.value ?? '');
        if (n.type === 'link' && n.url) return String(n.url);
        if (Array.isArray(n.children)) return n.children.map(buildText).join('');
        return '';
      };

      const raw = buildText(node);
      if (raw) {
        const videoMatch = raw.match(/\[VIDEO:([^\]]+)\]/);
        if (videoMatch) {
          return (
            <div className="my-4">
              <AuthenticatedContent src={videoMatch[1]} elementType="video" isStreaming={isStreaming} projectId={projectId} notebookId={notebookId} basePath={basePath} />
            </div>
          );
        }

        const audioMatch = raw.match(/\[AUDIO:([^\]]+)\]/);
        if (audioMatch) {
          return (
            <div className="my-4">
              <AuthenticatedContent src={audioMatch[1]} elementType="audio" isStreaming={isStreaming} projectId={projectId} notebookId={notebookId} basePath={basePath} />
            </div>
          );
        }
      }

      return <p className="mb-2 leading-relaxed break-words max-w-full overflow-wrap-anywhere">{children}</p>;
    },
    ul({ children }: { children: React.ReactNode }) {
      return <ul className="ml-6 mb-2" style={{ listStyleType: 'disc', paddingLeft: '1rem' }}>{children}</ul>;
    },
    ol({ children }: { children: React.ReactNode }) {
      return <ol className="ml-6 mb-2" style={{ listStyleType: 'decimal', paddingLeft: '1rem' }}>{children}</ol>;
    },
    li({ children }: { children: React.ReactNode }) {
      return <li className="mb-1">{children}</li>;
    },
    blockquote({ children }: { children: React.ReactNode }) {
      return (
        <blockquote className="border-l-4 border-gray-300 pl-4 italic my-4 text-gray-600 break-words">
          {children}
        </blockquote>
      );
    },
    a: (props: { href?: string; children?: React.ReactNode; title?: string }) => {
      // Check if it's a relative path that needs authentication
      const isRelative = props.href && 
                        !props.href.startsWith('http://') && 
                        !props.href.startsWith('https://') && 
                        !props.href.startsWith('/') &&
                        !props.href.startsWith('mailto:') &&
                        !props.href.startsWith('tel:') &&
                        !props.href.startsWith('#');
      
      // If relative and we have context, use AuthenticatedContent for proper resolution
      if (isRelative && projectId && notebookId) {
        return <AuthenticatedContent href={props.href} elementType="a" projectId={projectId} notebookId={notebookId} basePath={basePath} onLinkClick={onLinkClick}>{props.children}</AuthenticatedContent>;
      }
      
      // Otherwise use standard external link handler
      return <ExternalLink href={props.href}>{props.children}</ExternalLink>;
    },
    img: (props: { src?: string; alt?: string; title?: string }) => <AuthenticatedContent {...props} elementType="img" isStreaming={isStreaming} imgMaxHeight={maxImageHeight} onImageClick={handleImageClick} projectId={projectId} notebookId={notebookId} basePath={basePath} />,
    table({ children }: { children: React.ReactNode }) {
      return (
        <div className="my-4 overflow-x-auto max-w-full">
          <table className="w-full border-collapse border border-gray-300 table-auto">{children}</table>
        </div>
      );
    },
    thead({ children }: { children: React.ReactNode }) {
      return <thead className="bg-gray-50">{children}</thead>;
    },
    tbody({ children }: { children: React.ReactNode }) {
      return <tbody className="divide-y divide-gray-200">{children}</tbody>;
    },
    tr({ children }: { children: React.ReactNode }) {
      return <tr className="hover:bg-gray-50">{children}</tr>;
    },
    th({ children }: { children: React.ReactNode }) {
      return (
        <th className="px-4 py-2 text-left text-sm font-medium text-gray-900 bg-gray-100 border border-gray-300">
          {children}
        </th>
      );
    },
    td({ children }: { children: React.ReactNode }) {
      return <td className="px-4 py-2 text-sm text-gray-900 border border-gray-300 break-words max-w-xs">{children}</td>;
    },
    code(
      { inline, className: codeClassName, children, ...props }: {
        inline?: boolean;
        className?: string;
        children: React.ReactNode;
      },
    ) {
      const match = /language-(\w+)/.exec(codeClassName || '');
      const codeText = String(children).replace(/\n$/, '');

      // Render Mermaid diagrams when explicitly requested
      if (!inline && match?.[1] === 'mermaid') {
        return <MermaidRenderer chart={codeText} isStreaming={isStreaming} />;
      }

      // Treat as inline-code if it is single-line, has no language, and contains no line breaks
      const shouldRenderInline = inline || (!codeClassName && !codeText.includes('\n'));

      if (shouldRenderInline) {
        return (
          <code className={`${codeClassName || ''} bg-gray-100 px-1 py-0.5 rounded text-sm whitespace-normal`} {...props}>
            {children}
          </code>
        );
      }

      // Otherwise render as block code
      return (
        <pre className="bg-gray-100 border border-gray-200 rounded p-3 my-3 overflow-x-auto max-w-full">
          <code className={`${codeClassName || ''} text-sm whitespace-pre-wrap break-words`} {...props}>
            {children}
          </code>
        </pre>
      );
    },
  }), [isStreaming, projectId, notebookId, basePath, maxImageHeight, handleImageClick]);

  const markdown = (
    <ReactMarkdown
      children={processedText}
      remarkPlugins={[remarkGfm, remarkBreaks, remarkNestFollowingListsUnderOrderedItems, remarkMergeAdjacentOrderedLists]}
      urlTransform={markdownUrlTransform}
      components={markdownComponents}
    />
  );

  // When in full-screen mode render via portal so it overlays the entire UI
  if (isFullScreen) {
    return (
      <>
        {createPortal(
          <div className="fixed inset-0 z-50 bg-white p-4 overflow-auto select-text">
            {/* Control stack */}
            <div className="absolute top-4 right-4 flex flex-col items-center space-y-2">
              {onExitFullScreen && (
                <button
                  onClick={onExitFullScreen}
                  className="p-1 hover:bg-gray-100 rounded text-gray-500 hover:text-gray-700"
                  aria-label="Exit full screen"
                  title="Exit full screen"
                >
                  <FaTimes className="w-4 h-4" />
                </button>
              )}
              {overlayControls}
            </div>
            <div className={`${combinedClass} pt-6 pr-16`}>{markdown}</div>
          </div>,
          document.body
        )}
        {fullscreenImage && (
          <ImageFullscreenViewer
            src={fullscreenImage.src}
            alt={fullscreenImage.alt}
            onClose={() => setFullscreenImage(null)}
          />
        )}
      </>
    );
  }

  return (
    <>
      <div className={combinedClass} data-testid="markdown-viewer">{markdown}</div>
      {fullscreenImage && (
        <ImageFullscreenViewer
          src={fullscreenImage.src}
          alt={fullscreenImage.alt}
          onClose={() => setFullscreenImage(null)}
        />
      )}
    </>
  );
}
