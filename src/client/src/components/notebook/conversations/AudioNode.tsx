import React, { useContext, useEffect, useState } from 'react';
import {
  $applyNodeReplacement,
  DecoratorNode,
  LexicalNode,
  NodeKey,
  SerializedLexicalNode,
  Spread,
  TextNode,
} from 'lexical';
import { Transformer } from '@lexical/markdown';
import { api } from '../../../services/api';
import { API_BASE_URL } from '../../../config/apiConfig';
import { ImageNodeContext } from './ImageNode';
import { safeDecodeURIComponent } from '../../../utils/urlEncoding';

// Cache for authenticated media URLs => blob object URLs (lives for app session)
const authenticatedBlobCache = new Map<string, string>();

const AuthenticatedAudio: React.FC<{ src: string }> = ({ src }) => {
  const { projectId, notebookId, resolveProjectFilePath, basePath } = useContext(ImageNodeContext);
  const [objectUrl, setObjectUrl] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [retryCount, setRetryCount] = useState(0);

  const apiBaseUrl = API_BASE_URL;
  const normalizeUrl = (value?: string): string | undefined => {
    if (!value) return value;
    return value
      .replace(/%5Cu0026/gi, '&')
      .replace(/\\u0026/gi, '&');
  };

  const resolveUrl = (value: string): string => {
    const isRelative = !value.startsWith('http://') &&
                       !value.startsWith('https://') &&
                       !value.startsWith('/') &&
                       !value.startsWith('data:');
    if (isRelative && projectId && notebookId) {
      // Remove ./ prefix but keep track of ../ for parent directory navigation
      let cleanValue = value.replace(/^\.\//, '');
      
      // Separate path from query string (e.g., "audio.mp3?m=12345")
      // The query params (like m=) are used for cache busting when files are modified
      let pathPart = cleanValue;
      let queryPart = '';
      const queryIndex = cleanValue.indexOf('?');
      if (queryIndex >= 0) {
        pathPart = cleanValue.substring(0, queryIndex);
        queryPart = cleanValue.substring(queryIndex + 1);
      }
      
      // Prepend basePath if provided (the directory containing the markdown file)
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
      const apiBase = (apiBaseUrl || '').replace(/\/$/, '');
      let resolvedUrl = `${apiBase}/projects/${projectId}/notebooks/${notebookId}/files/content?path=${encodedPath}`;
      
      // Append additional query params (like m= for cache busting)
      if (queryPart) {
        resolvedUrl += `&${queryPart}`;
      }
      
      return resolvedUrl;
    }
    if (isRelative && projectId && !notebookId && resolveProjectFilePath) {
      const cleanPath = value.replace(/^\.\.?\//, '');
      const fileId = resolveProjectFilePath(cleanPath);
      if (fileId) {
        const apiBase = (apiBaseUrl || '').replace(/\/$/, '');
        return `${apiBase}/projects/${projectId}/files/${fileId}/content`;
      }
      console.warn('[AudioNode] Could not resolve project file for relative path:', value);
    }
    return value;
  };

  const normalizedSrc = normalizeUrl(resolveUrl(src));
  const isAuthenticatedUrl = (() => {
    if (!normalizedSrc) return false;
    try {
      const u = new URL(normalizedSrc, window.location.origin);
      if (u.pathname.startsWith('/api/')) return true;
    } catch {}
    return normalizedSrc.startsWith(apiBaseUrl);
  })();

  const isLikelyMalformedUrl = (url: string): boolean => {
    if (!url) return true;
    const malformedPatterns = [
      /^https?:\/\/[^\/]*$/,
      /\.\.\./,
      /\s/,
      /[<>\{}|\\^`\[\]]/,
      /^https?:\/\/[^\/]*\/?$/,
    ];
    return malformedPatterns.some(pattern => pattern.test(url));
  };

  useEffect(() => {
    if (!normalizedSrc || !isAuthenticatedUrl) {
      setObjectUrl(normalizedSrc || null);
      return;
    }
    setError(null);
    if (isLikelyMalformedUrl(normalizedSrc)) {
      setIsLoading(true);
      const retryTimeout = setTimeout(() => {
        if (!isLikelyMalformedUrl(normalizedSrc)) {
          loadAuthenticatedMedia(normalizedSrc);
        } else if (retryCount < 3) {
          setRetryCount(prev => prev + 1);
        } else {
          setIsLoading(false);
          setError('Invalid media URL');
        }
      }, Math.min(500 * (retryCount + 1), 2000));
      return () => clearTimeout(retryTimeout);
    } else {
      loadAuthenticatedMedia(normalizedSrc);
    }
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

  const loadAuthenticatedMedia = (mediaUrl: string) => {
    const effectiveUrl = toApiUrl(mediaUrl);
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
        setError(null);
      })
      .catch(err => {
        if (!isLikelyMalformedUrl(mediaUrl) || retryCount >= 3) {
          setError(err.message);
        } else {
          setError(null);
        }
      })
      .finally(() => setIsLoading(false));
  };

  if (!isAuthenticatedUrl) {
    return (
      <audio className="max-w-full" src={normalizedSrc || undefined} controls />
    );
  }

  if (isLoading) {
    return (
      <div className="inline-flex items-center space-x-1 px-2 py-1 bg-blue-50 border border-blue-200 rounded text-xs text-blue-600">
        <div className="w-3 h-3 border-2 border-blue-200 border-t-blue-600 rounded-full animate-spin"></div>
        <span>Loading audio...</span>
      </div>
    );
  }

  if (error) {
    return (
      <div className="inline-flex items-center space-x-1 px-2 py-1 bg-red-50 border border-red-200 rounded text-xs text-red-600">
        <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L3.732 16.5c-.77.833.192 2.5 1.732 2.5z" />
        </svg>
        <span>Audio unavailable</span>
      </div>
    );
  }

  return objectUrl ? (
    <audio className="max-w-full" src={objectUrl} controls />
  ) : null;
};

export interface AudioPayload {
  src: string;
  key?: NodeKey;
}

export type SerializedAudioNode = Spread<
  {
    src: string;
  },
  SerializedLexicalNode
>;

export class AudioNode extends DecoratorNode<React.JSX.Element> {
  __src: string;

  static getType(): string {
    return 'audio';
  }

  static clone(node: AudioNode): AudioNode {
    return new AudioNode(node.__src, node.__key);
  }

  constructor(src: string, key?: NodeKey) {
    super(key);
    this.__src = src;
  }

  exportJSON(): SerializedAudioNode {
    return {
      src: this.getSrc(),
      type: 'audio',
      version: 1,
    };
  }

  static importJSON(serializedNode: SerializedAudioNode): AudioNode {
    const { src } = serializedNode;
    const node = $createAudioNode({ src });
    return node;
  }

  getSrc(): string {
    return this.__src;
  }

  setSrc(src: string): void {
    const writable = this.getWritable();
    (writable as any).__src = src;
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
      <div className="audio-node my-4">
        <AuthenticatedAudio src={this.__src} />
      </div>
    );
  }
}

export function $createAudioNode({ src, key }: AudioPayload): AudioNode {
  return $applyNodeReplacement(new AudioNode(src, key));
}

export function $isAudioNode(node: LexicalNode | null | undefined): node is AudioNode {
  return node instanceof AudioNode;
}

// Markdown transformer for audio (uses token form [AUDIO:src] for import convenience)
export const AUDIO_TRANSFORMER: Transformer = {
  dependencies: [AudioNode],
  export: (node: LexicalNode) => {
    if (!$isAudioNode(node)) return null;
    // Export as HTML so our markdownUrlConverter can rewrite to relative
    return `<audio src="${node.getSrc()}" controls></audio>`;
  },
  importRegExp: /\[AUDIO:([^\]]+)\]/,
  regExp: /\[AUDIO:([^\]]+)\]/,
  replace: (textNode: TextNode, match: RegExpMatchArray) => {
    const [, src] = match;
    const audioNode = $createAudioNode({ src });
    textNode.replace(audioNode);
  },
  trigger: ']',
  type: 'text-match',
};



