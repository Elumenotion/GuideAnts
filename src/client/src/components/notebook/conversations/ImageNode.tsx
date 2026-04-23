import React, { useState, useEffect, createContext, useContext } from 'react';
import {
  $applyNodeReplacement,
  DecoratorNode,
  LexicalNode,
  NodeKey,
  SerializedLexicalNode,
  Spread,
  TextNode,
} from 'lexical';
import { 
  Transformer,
} from '@lexical/markdown';
import { api } from '../../../services/api';
import { API_BASE_URL, getApiHost } from '../../../config/apiConfig';
import { safeDecodeURIComponent } from '../../../utils/urlEncoding';

// Context for providing projectId/notebookId/basePath to ImageNode
interface ImageNodeContextValue {
  projectId?: string;
  notebookId?: string;
  resolveProjectFilePath?: (relativePath: string) => string | undefined;
  basePath?: string;
}

export const ImageNodeContext = createContext<ImageNodeContextValue>({});

export const ImageNodeContextProvider = ImageNodeContext.Provider;

// Cache for authenticated image URLs => blob object URLs (lives for app session)
const authenticatedBlobCache = new Map<string, string>();

// Component to handle authenticated image URLs
const AuthenticatedImage: React.FC<{ 
    src: string; 
    alt: string;
    width?: 'inherit' | number;
    height?: 'inherit' | number;
}> = ({ src, alt, width, height }) => {
    const { projectId, notebookId, resolveProjectFilePath, basePath } = useContext(ImageNodeContext);
    const [objectUrl, setObjectUrl] = useState<string | null>(null);
    const [isLoading, setIsLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [retryCount, setRetryCount] = useState(0);

    const apiBaseUrl = API_BASE_URL;
    // Normalize common escaped sequences coming from JSON/SSE (e.g. "\u0026" or "%5Cu0026")
    const normalizeUrl = (value?: string): string | undefined => {
        if (!value) return value;
        return value
            .replace(/%5Cu0026/gi, '&')
            .replace(/\\u0026/gi, '&');
    };

    // Resolve relative paths to absolute authenticated URLs
    const resolveUrl = (value: string): string => {
        // Check if it's a relative path (not a URL with protocol or absolute path)
        const isRelative = !value.startsWith('http://') && 
                          !value.startsWith('https://') && 
                          !value.startsWith('/') &&
                          !value.startsWith('data:');
        
        // Notebooks have path-based API: /projects/{projectId}/notebooks/{notebookId}/files/content?path={path}
        if (isRelative && projectId && notebookId) {
            // Remove ./ prefix but keep track of ../ for parent directory navigation
            let cleanValue = value.replace(/^\.\//, '');
            
            // Separate path from query string (e.g., "image.png?m=12345")
            // The query params (like m=) are used for cache busting when files are modified
            let pathPart = cleanValue;
            let queryPart = '';
            const queryIndex = cleanValue.indexOf('?');
            if (queryIndex >= 0) {
                pathPart = cleanValue.substring(0, queryIndex);
                queryPart = cleanValue.substring(queryIndex + 1);
            }
            
            // Prepend basePath if provided (the directory containing the markdown file)
            // This allows relative paths like "image.png" in "Output/guide.md" to resolve to "Output/image.png"
            let fullPath = pathPart;
            if (basePath) {
                // Handle ../ navigation
                const basePathParts = basePath.split('/').filter(Boolean);
                const pathSegments = pathPart.split('/');
                
                // Count and remove leading ../ segments
                while (pathSegments.length > 0 && pathSegments[0] === '..') {
                    pathSegments.shift();
                    basePathParts.pop();
                }
                
                // Combine remaining basePath with the path
                fullPath = [...basePathParts, ...pathSegments].join('/');
            }
            
            const encodedPath = encodeURIComponent(safeDecodeURIComponent(fullPath));
            
            // Construct proper API URL matching backend format
            const apiBase = (apiBaseUrl || '').replace(/\/$/, '');
            let resolvedUrl = `${apiBase}/projects/${projectId}/notebooks/${notebookId}/files/content?path=${encodedPath}`;
            
            // Append additional query params (like m= for cache busting)
            if (queryPart) {
                resolvedUrl += `&${queryPart}`;
            }
            
            return resolvedUrl;
        }
        
        // Project files: use lookup function to get file ID, then use ID-based API
        if (isRelative && projectId && !notebookId && resolveProjectFilePath) {
            // Remove ./ or ../ prefix
            const cleanPath = value.replace(/^\.\.?\//, '');
            
            // Use provided lookup function
            const fileId = resolveProjectFilePath(cleanPath);
            
            if (fileId) {
                // Use ID-based API for project files
                const apiBase = (apiBaseUrl || '').replace(/\/$/, '');
                return `${apiBase}/projects/${projectId}/files/${fileId}/content`;
            }
            
            console.warn('[ImageNode] Could not resolve project file for relative path:', value);
        }
        
        return value;
    };
    const normalizedSrc = normalizeUrl(resolveUrl(src));
    const isAuthenticatedUrl = (() => {
        if (!normalizedSrc) return false;
        try {
            const u = new URL(normalizedSrc, window.location.origin);
            if (u.pathname.startsWith('/api/')) {
                const apiHost = getApiHost(apiBaseUrl);
                if (u.host === window.location.host || u.host === apiHost) return true;
                return false;
            }
        } catch {}
        return normalizedSrc.startsWith(apiBaseUrl);
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
        ];
        
        return malformedPatterns.some(pattern => pattern.test(url));
    };

    useEffect(() => {
        if (!normalizedSrc || !isAuthenticatedUrl) {
            setObjectUrl(normalizedSrc || null);
            return;
        }

        // Reset states when URL changes
        setError(null);
        
        // If URL looks malformed, wait before attempting to load
        if (isLikelyMalformedUrl(normalizedSrc)) {
            setIsLoading(true);
            // Set a timeout to retry after the URL might be complete
            const retryTimeout = setTimeout(() => {
                if (!isLikelyMalformedUrl(normalizedSrc)) {
                    // URL looks better now, try loading
                    loadAuthenticatedImage(normalizedSrc);
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
            loadAuthenticatedImage(normalizedSrc);
        }

        return () => {
            // Do not revoke cached object URLs—they may be reused by other mounts
        };
    }, [normalizedSrc, isAuthenticatedUrl, retryCount]);

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

    const loadAuthenticatedImage = (imageUrl: string, fetchAttempt: number = 0) => {
        const effectiveUrl = toApiUrl(imageUrl);
        // If we've already fetched this image once, reuse cached blob URL
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
                        loadAuthenticatedImage(imageUrl, fetchAttempt + 1);
                    }, delay);
                    return; // Don't set error or clear loading yet
                }
                
                // For malformed URLs during streaming, also allow retries via retryCount
                if (isLikelyMalformedUrl(imageUrl) && retryCount < 3) {
                    setError(null);
                } else {
                    setError(err.message);
                }
                setIsLoading(false);
            });
    };

    // For non-authenticated URLs, render normally
    if (!isAuthenticatedUrl) {
        return (
            <img
                className="max-w-full h-auto rounded shadow-sm"
                src={normalizedSrc}
                alt={alt}
                style={{
                    height: height === 'inherit' ? 'inherit' : height,
                    width: width === 'inherit' ? 'inherit' : width,
                }}
            />
        );
    }

    // For malformed URLs (streaming), always show "coming up" - never an error
    if (normalizedSrc && isLikelyMalformedUrl(normalizedSrc)) {
        return (
            <div className="inline-flex items-center space-x-1 px-2 py-1 bg-blue-50 border border-blue-200 rounded text-xs text-blue-600">
                <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16l4.586-4.586a2 2 0 012.828 0L16 16m-2-2l1.586-1.586a2 2 0 012.828 0L20 14m-6-6h.01M6 20h12a2 2 0 002-2V6a2 2 0 00-2-2H6a2 2 0 00-2 2v12a2 2 0 002 2z" />
                </svg>
                <span>Image coming up...</span>
            </div>
        );
    }

    if (isLoading) {
        return (
            <div className="inline-flex items-center space-x-1 px-2 py-1 bg-blue-50 border border-blue-200 rounded text-xs text-blue-600">
                <div className="w-3 h-3 border-2 border-blue-200 border-t-blue-600 rounded-full animate-spin"></div>
                <span>Loading image...</span>
            </div>
        );
    }

    if (error) {
        return (
            <div className="inline-flex items-center space-x-1 px-2 py-1 bg-red-50 border border-red-200 rounded text-xs text-red-600">
                <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L3.732 16.5c-.77.833.192 2.5 1.732 2.5z" />
                </svg>
                <span>Image unavailable</span>
            </div>
        );
    }

    return objectUrl ? (
        <img
            className="max-w-full h-auto rounded shadow-sm"
            src={objectUrl}
            alt={alt}
            style={{
                height: height === 'inherit' ? 'inherit' : height,
                width: width === 'inherit' ? 'inherit' : width,
            }}
        />
    ) : null;
};

export interface ImagePayload {
  altText: string;
  height?: number;
  key?: NodeKey;
  src: string;
  width?: number;
}

export type SerializedImageNode = Spread<
  {
    altText: string;
    height?: number;
    src: string;
    width?: number;
  },
  SerializedLexicalNode
>;

export class ImageNode extends DecoratorNode<React.JSX.Element> {
  __src: string;
  __altText: string;
  __width: 'inherit' | number;
  __height: 'inherit' | number;

  static getType(): string {
    return 'image';
  }

  static clone(node: ImageNode): ImageNode {
    return new ImageNode(
      node.__src,
      node.__altText,
      node.__width,
      node.__height,
      node.__key,
    );
  }

  constructor(
    src: string,
    altText: string,
    width?: 'inherit' | number,
    height?: 'inherit' | number,
    key?: NodeKey,
  ) {
    super(key);
    this.__src = src;
    this.__altText = altText;
    this.__width = width || 'inherit';
    this.__height = height || 'inherit';
  }

  exportJSON(): SerializedImageNode {
    return {
      altText: this.getAltText(),
      height: this.__height === 'inherit' ? 0 : this.__height,
      src: this.getSrc(),
      type: 'image',
      version: 1,
      width: this.__width === 'inherit' ? 0 : this.__width,
    };
  }

  static importJSON(serializedNode: SerializedImageNode): ImageNode {
    const { altText, height, src, width } = serializedNode;
    const node = $createImageNode({
      altText,
      height,
      src,
      width,
    });
    return node;
  }

  getSrc(): string {
    return this.__src;
  }

  getAltText(): string {
    return this.__altText;
  }

  setAltText(altText: string): void {
    const writable = this.getWritable();
    writable.__altText = altText;
  }

  setSrc(src: string): void {
    const writable = this.getWritable();
    writable.__src = src;
  }

  createDOM(): HTMLElement {
    const span = document.createElement('span');
    return span;
  }

  updateDOM(): false {
    return false;
  }

  decorate(): React.JSX.Element {
    return (
      <div className="image-node my-4">
        <AuthenticatedImage
          src={this.__src}
          alt={this.__altText}
          width={this.__width}
          height={this.__height}
        />
      </div>
    );
  }
}

export function $createImageNode({
  altText,
  height,
  src,
  width,
  key,
}: ImagePayload): ImageNode {
  return $applyNodeReplacement(
    new ImageNode(src, altText, width, height, key),
  );
}

export function $isImageNode(
  node: LexicalNode | null | undefined,
): node is ImageNode {
  return node instanceof ImageNode;
}

// Markdown transformer for images
export const IMAGE_TRANSFORMER: Transformer = {
  dependencies: [ImageNode],
  export: (node: LexicalNode) => {
    if (!$isImageNode(node)) {
      return null;
    }
    return `![${node.getAltText()}](${node.getSrc()})`;
  },
  importRegExp: /!\[([^\]]*)\]\(([^)]+)\)/,
  regExp: /!\[([^\]]*)\]\(([^)]+)\)/,
  replace: (textNode: TextNode, match: RegExpMatchArray) => {
    const [, altText, src] = match;
    const imageNode = $createImageNode({
      altText,
      src,
    });
    textNode.replace(imageNode);
  },
  trigger: ')',
  type: 'text-match',
}; 
