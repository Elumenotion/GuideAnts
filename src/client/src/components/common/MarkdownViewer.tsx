import React, { useEffect, useState } from 'react';
import ReactMarkdown, { defaultUrlTransform } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeSlug from 'rehype-slug';
import rehypeRaw from 'rehype-raw';

import MermaidRenderer from './MermaidRenderer';
import PreviewContainer from './PreviewContainer';
import { api } from '@/services/api';
import { API_BASE_URL } from '@/config/apiConfig';
import { encodeMarkdownLinkSpaces } from '@/utils/markdownLinkEncoding';
import { safeDecodeURIComponent } from '@/utils/urlEncoding';

export interface MarkdownViewerProps {
    /** Markdown source text */
    text: string;
    /** Optional TailwindCSS classes applied to the container */
    className?: string;
    /** When true, renders without PreviewContainer wrapper (for use in modals/overlays) */
    inlineMode?: boolean;
    /** Hide the PreviewContainer header when inlineMode is false */
    hidePreviewHeader?: boolean;
    /** Project ID for resolving relative file paths to authenticated URLs */
    projectId?: string;
    /** Notebook ID for resolving relative file paths to authenticated URLs */
    notebookId?: string;
    /** Function to resolve relative path to file ID for project files (should be memoized by caller) */
    resolveProjectFilePath?: (relativePath: string) => string | undefined;
    /** Base path for resolving relative URLs (directory of the markdown file, e.g., "Output" or "docs/guides") */
    basePath?: string;
}

// Convert HTML video/audio tags to a format ReactMarkdown can handle
function preprocessHtmlMediaTags(text: string): string {
    // Replace complete video elements (including those with source children)
    text = text.replace(/<video\s+[^>]*?>(.*?)<\/video>/gis, (match, content) => {
        const videoSrcMatch = match.match(/<video\s+[^>]*?src\s*=\s*["']([^"']*?)["']/i);
        const widthMatch = match.match(/\bwidth\s*=\s*["']?(\d+)["']?/i);
        const heightMatch = match.match(/\bheight\s*=\s*["']?(\d+)["']?/i);
        const meta = `${widthMatch ? `|w=${widthMatch[1]}` : ''}${heightMatch ? `|h=${heightMatch[1]}` : ''}`;
        if (videoSrcMatch) {
            return `[VIDEO:${videoSrcMatch[1]}${meta}]`;
        }
        const sourceSrcMatch = content.match(/<source\s+[^>]*?src\s*=\s*["']([^"']*?)["']/i);
        if (sourceSrcMatch) {
            return `[VIDEO:${sourceSrcMatch[1]}${meta}]`;
        }
        return match;
    });

    // Replace complete audio elements (including those with source children)
    text = text.replace(/<audio\s+[^>]*?>(.*?)<\/audio>/gis, (match, content) => {
        const audioSrcMatch = match.match(/<audio\s+[^>]*?src\s*=\s*["']([^"']*?)["']/i);
        if (audioSrcMatch) {
            return `[AUDIO:${audioSrcMatch[1]}]`;
        }
        const sourceSrcMatch = content.match(/<source\s+[^>]*?src\s*=\s*["']([^"']*?)["']/i);
        if (sourceSrcMatch) {
            return `[AUDIO:${sourceSrcMatch[1]}]`;
        }
        return match;
    });

    return text;
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

// Custom link component with API handling (downloads via authenticated blob when needed)
const ExternalLink: React.FC<{ href?: string; children: React.ReactNode }> = ({ href, children }) => {
    const isApiHref = (raw?: string): boolean => {
        if (!raw) return false;
        try {
            const u = new URL(raw, window.location.origin);
            if (u.pathname.startsWith('/api/')) return true;
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

        const openExternal = (window as any).electron?.openExternal as ((url: string) => void) | undefined;
        if (openExternal && href) {
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

// Cache for authenticated image/video/audio URLs => blob object URLs
const authenticatedBlobCache = new Map<string, string>();

const AuthenticatedContent: React.FC<{
    src?: string;
    href?: string;
    alt?: string;
    title?: string;
    children?: React.ReactNode;
    elementType: 'img' | 'a' | 'video' | 'audio';
    width?: number;
    height?: number;
    projectId?: string;
    notebookId?: string;
    resolveProjectFilePath?: (relativePath: string) => string | undefined;
    basePath?: string;
}> = ({ src, href, alt, title, children, elementType, width, height, projectId, notebookId, resolveProjectFilePath, basePath }) => {
    // Extract width/height metadata from tokenized URLs like 
    //   [VIDEO:/path/file.mp4|w=480|h=270]
    // and strip metadata from the actual media URL
    let computedWidth: number | undefined = width;
    let computedHeight: number | undefined = height;
    let alignment: 'left' | 'right' | 'center' | undefined;
    const original = src || href;
    let cleanedSource = original;
    if (original) {
        const widthToken = original.match(/(?:\||%7C)w=(\d+)/i);
        const heightToken = original.match(/(?:\||%7C)h=(\d+)/i);
        const alignToken = original.match(/(?:\||%7C)align=(right|left|center)/i);
        if (!computedWidth && widthToken) {
            computedWidth = parseInt(widthToken[1], 10);
        }
        if (!computedHeight && heightToken) {
            computedHeight = parseInt(heightToken[1], 10);
        }
        if (alignToken) {
            alignment = (alignToken[1].toLowerCase() as 'left' | 'right' | 'center');
        }
        // If tokens present, strip everything after the first '|' or '%7C'
        const pipeIndex = original.indexOf('|');
        const encPipeIndex = original.toLowerCase().indexOf('%7c');
        const cutIndex = [pipeIndex, encPipeIndex].filter(i => i >= 0).sort((a, b) => a - b)[0];
        if (cutIndex !== undefined) {
            cleanedSource = original.substring(0, cutIndex);
        }
    }
    const normalizeUrl = (value?: string): string | undefined => {
        if (!value) return value;
        return value
            .replace(/%5Cu0026/gi, '&')
            .replace(/\\u0026/gi, '&');
    };

    // Resolve relative paths to absolute authenticated URLs
    const resolveUrl = (value?: string): string | undefined => {
        if (!value) return value;
        
        // Check if it's a relative path (not a URL with protocol or absolute path)
        const isRelative = !value.startsWith('http://') && 
                          !value.startsWith('https://') && 
                          !value.startsWith('/') &&
                          !value.startsWith('mailto:') &&
                          !value.startsWith('tel:') &&
                          !value.startsWith('data:');
        
        // Only resolve if we have BOTH projectId AND notebookId (notebook files)
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
            const apiBase = (API_BASE_URL || '').replace(/\/$/, '');
            let resolvedUrl = `${apiBase}/projects/${projectId}/notebooks/${notebookId}/files/content?path=${encodedPath}`;
            
            // Append additional query params (like m= for cache busting)
            if (queryPart) {
                resolvedUrl += `&${queryPart}`;
            }
            
            return resolvedUrl;
        }
        
        // For project files (no notebookId), use lookup function to get file ID
        if (isRelative && projectId && !notebookId && resolveProjectFilePath) {
            // Remove ./ or ../ prefix
            const cleanPath = value.replace(/^\.\.?\//, '');
            
            // Use provided lookup function
            const fileId = resolveProjectFilePath(cleanPath);
            
            if (fileId) {
                // Use ID-based API for project files
                const apiBase = (API_BASE_URL || '').replace(/\/$/, '');
                return `${apiBase}/projects/${projectId}/files/${fileId}/content`;
            }
            
            console.warn('[MarkdownViewer] Could not resolve project file for relative path:', value);
        }
        
        return value;
    };

    const url = normalizeUrl(resolveUrl(cleanedSource));
    const [objectUrl, setObjectUrl] = useState<string | null>(null);
    const [isLoading, setIsLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const isAuthenticatedUrl = (() => {
        if (!url) return false;
        try {
            const u = new URL(url, window.location.origin);
            // Check if pathname starts with /api/ (works for both absolute and relative URLs)
            if (u.pathname.startsWith('/api/')) return true;
        } catch {}
        // Also check if URL starts with API_BASE_URL (for relative URLs or matching base)
        return !!url && url.startsWith(API_BASE_URL);
    })();

    const toApiUrl = (originalUrl: string): string => {
        // If URL is already absolute (starts with http:// or https://), use it as-is
        if (originalUrl.startsWith('http://') || originalUrl.startsWith('https://')) {
            return originalUrl;
        }
        
        // For relative URLs, convert them to use API_BASE_URL
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

    useEffect(() => {
        if (!url || !isAuthenticatedUrl) {
            setObjectUrl(url || null);
            return;
        }

        if ((elementType === 'img' || elementType === 'video' || elementType === 'audio') && url) {
            const effectiveUrl = toApiUrl(url);
            const cached = authenticatedBlobCache.get(effectiveUrl);
            if (cached) {
                setObjectUrl(cached);
                setIsLoading(false);
                return;
            }

            let cancelled = false;
            const maxFetchRetries = 3;

            const fetchWithRetry = (attempt: number) => {
                if (cancelled) return;
                
                setIsLoading(true);
                api.utils.getAuthenticatedUrl(effectiveUrl)
                    .then(result => {
                        if (cancelled) return;
                        authenticatedBlobCache.set(effectiveUrl, result.objectUrl);
                        setObjectUrl(result.objectUrl);
                        setError(null);
                        setIsLoading(false);
                    })
                    .catch(err => {
                        if (cancelled) return;
                        
                        const status = (err as any).status;
                        // Retry on transient errors (404 = not ready yet, 5xx = server error)
                        // but NOT on 401/403 (auth errors)
                        const isTransientError = status === 404 || (status >= 500 && status < 600);
                        
                        if (isTransientError && attempt < maxFetchRetries) {
                            // Exponential backoff: 500ms, 1000ms, 1500ms
                            const delay = 500 * (attempt + 1);
                            setTimeout(() => fetchWithRetry(attempt + 1), delay);
                            return; // Don't set error or clear loading yet
                        }
                        
                        setError(err.message || 'Failed to load media');
                        setIsLoading(false);
                    });
            };

            fetchWithRetry(0);

            return () => {
                cancelled = true;
            };
        }
    }, [url, isAuthenticatedUrl, elementType]);

    const alignClass = alignment === 'right'
        ? 'float-right ml-4 mb-2'
        : alignment === 'left'
            ? 'float-left mr-4 mb-2'
            : alignment === 'center'
                ? 'mx-auto block my-2'
                : '';

    if (!isAuthenticatedUrl) {
        if (elementType === 'img') {
            return <img src={cleanedSource} alt={alt} title={title} className={`max-w-full h-auto ${alignClass}`.trim()} />;
        } else if (elementType === 'video') {
            const style = (computedWidth || computedHeight) ? { width: computedWidth ? `${computedWidth}px` : undefined, height: computedHeight ? `${computedHeight}px` : undefined } : undefined;
            return <video src={cleanedSource} controls className="max-w-full h-auto" style={style} />;
        } else if (elementType === 'audio') {
            return <audio src={src} controls className="max-w-full" />;
        } else {
            return <ExternalLink href={href}>{children}</ExternalLink>;
        }
    }

    if (elementType === 'img') {
        if (isLoading) return <span className="text-xs text-blue-600">Loading image...</span>;
        if (error) return <span className="text-xs text-red-600">Image unavailable</span>;
        return objectUrl ? <img src={objectUrl} alt={alt} title={title} className={`max-w-full h-auto ${alignClass}`.trim()} /> : null;
    }
    if (elementType === 'video') {
        if (isLoading) return <span className="text-xs text-blue-600">Loading video...</span>;
        if (error) return <span className="text-xs text-red-600">Video unavailable</span>;
        const style = (computedWidth || computedHeight) ? { width: computedWidth ? `${computedWidth}px` : undefined, height: computedHeight ? `${computedHeight}px` : undefined } : undefined;
        return objectUrl ? <video src={objectUrl} controls className="max-w-full h-auto" style={style} /> : null;
    }
    if (elementType === 'audio') {
        if (isLoading) return <span className="text-xs text-blue-600">Loading audio...</span>;
        if (error) return <span className="text-xs text-red-600">Audio unavailable</span>;
        return objectUrl ? <audio src={objectUrl} controls className="max-w-full" /> : null;
    }

    // For authenticated links, show the resolved URL in href so browser displays it correctly
    // The onClick will handle the actual authenticated download
    const handleAuthenticatedLink = async (e: React.MouseEvent) => {
        e.preventDefault();
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
 * Renders markdown with GitHub-flavoured markdown (GFM) extensions enabled and Mermaid diagram support.
 */
function MarkdownViewer({ text, className = '', inlineMode = false, hidePreviewHeader = false, projectId, notebookId, resolveProjectFilePath, basePath }: MarkdownViewerProps) {
   
    const combinedClass = `${className} select-text`.trim();
    
    // For very large content, disable GFM plugin to prevent performance issues with figure references
    const LARGE_CONTENT_THRESHOLD = 200000; // 200KB
    const isLargeContent = text.length > LARGE_CONTENT_THRESHOLD;
    const remarkPlugins = isLargeContent ? [] : [remarkGfm]; // Skip GFM for large extracted documents
    
    // Configure rehype plugins - rehype-raw MUST come before rehype-slug so that 
    // HTML headings are parsed into elements before slugging occurs
    const rehypePlugins = [rehypeRaw, rehypeSlug];
    
    const processedText = React.useMemo(
        () => encodeMarkdownLinkSpaces(preprocessHtmlMediaTags(text)),
        [text]
    );
    
    // Check if the text appears to be a full HTML document (e.g. starts with <!DOCTYPE html> or <html>)
    // In this case, we render it inside an iframe to preserve scripts, styles, and document structure.
    const isFullHtmlDocument = React.useMemo(() => {
        const trimmed = text.trimStart();
        return /^(<!DOCTYPE\s+html|<html)/i.test(trimmed);
    }, [text]);

    if (isFullHtmlDocument) {
        const iframeContent = (
            <iframe
                srcDoc={text}
                className="w-full h-full border-0 block"
                sandbox="allow-scripts allow-same-origin allow-popups allow-forms"
                title="HTML Content"
                style={{ minHeight: '600px' }} // Fallback height
            />
        );

        if (inlineMode) {
            return iframeContent;
        }
        return (
            <PreviewContainer contentClassName={combinedClass} hideHeader={hidePreviewHeader}>
                {iframeContent}
            </PreviewContainer>
        );
    }

    // Configure react-markdown to support HTML by using rehype-raw
    // Note: react-markdown disables HTML by default for security, but we need it for home pages.
    // The rehypePlugins prop handles the AST transformation.
    const markdownContent = (
        <ReactMarkdown
            children={processedText}
            remarkPlugins={remarkPlugins}
            rehypePlugins={rehypePlugins}
            urlTransform={markdownUrlTransform}
            // Enable HTML parsing in the remark-to-rehype step so rehype-raw receives 'raw' nodes
            remarkRehypeOptions={{ allowDangerousHtml: true }}
            components={{
                // Explicitly pass through common layout elements to ensure they aren't filtered
                // Type definitions handle stripping the 'node' prop which isn't valid DOM attribute
                div: ({node, children, ...props}: React.HTMLAttributes<HTMLDivElement> & { node?: any, children?: React.ReactNode }) => <div {...props}>{children}</div>,
                span: ({node, children, ...props}: React.HTMLAttributes<HTMLSpanElement> & { node?: any, children?: React.ReactNode }) => <span {...props}>{children}</span>,
                section: ({node, children, ...props}: React.HTMLAttributes<HTMLElement> & { node?: any, children?: React.ReactNode }) => <section {...props}>{children}</section>,
                br: ({node, ...props}: React.HTMLAttributes<HTMLBRElement> & { node?: any }) => <br {...props} />,
                hr: ({node, ...props}: React.HTMLAttributes<HTMLHRElement> & { node?: any }) => <hr {...props} className="my-4 border-gray-300" />,

                // We define specific components for markdown elements.
                // Any HTML tag NOT in this list will be rendered as is (if rehype-raw works) or skipped.
                // But since we are passing `components`, react-markdown might use default handling for others?
                // Yes, it falls back to intrinsic elements.
                
                h1({ children, ...props }: React.HTMLAttributes<HTMLHeadingElement> & { children: React.ReactNode }) {
                    return <h1 {...props} className="text-2xl font-bold mb-2">{children}</h1>;
                },
                h2({ children, ...props }: React.HTMLAttributes<HTMLHeadingElement> & { children: React.ReactNode }) {
                    return <h2 {...props} className="text-xl font-semibold mb-2">{children}</h2>;
                },
                h3({ children, ...props }: React.HTMLAttributes<HTMLHeadingElement> & { children: React.ReactNode }) {
                    return <h3 {...props} className="text-lg font-semibold mb-2">{children}</h3>;
                },
                h4({ children, ...props }: React.HTMLAttributes<HTMLHeadingElement> & { children: React.ReactNode }) {
                    return <h4 {...props} className="text-base font-semibold mb-2">{children}</h4>;
                },
                h5({ children, ...props }: React.HTMLAttributes<HTMLHeadingElement> & { children: React.ReactNode }) {
                    return <h5 {...props} className="text-sm font-semibold mb-1">{children}</h5>;
                },
                p({ node, children }: { node?: any; children: React.ReactNode }) {
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
                                    <AuthenticatedContent src={videoMatch[1]} elementType="video" projectId={projectId} notebookId={notebookId} resolveProjectFilePath={resolveProjectFilePath} basePath={basePath} />
                                </div>
                            );
                        }

                        const audioMatch = raw.match(/\[AUDIO:([^\]]+)\]/);
                        if (audioMatch) {
                            return (
                                <div className="my-4">
                                    <AuthenticatedContent src={audioMatch[1]} elementType="audio" projectId={projectId} notebookId={notebookId} resolveProjectFilePath={resolveProjectFilePath} basePath={basePath} />
                                </div>
                            );
                        }
                    }

                    // Preserve single newlines from extracted text (common from OCR/markdown extraction)
                    // without requiring remark-breaks. This renders soft line breaks as <br/>
                    // while keeping normal wrapping behavior for long lines.
                    return <p className="mb-2 leading-relaxed whitespace-pre-line break-words">{children}</p>;
                },
                ul({ children, ...props }: React.HTMLAttributes<HTMLUListElement> & { children: React.ReactNode }) {
                    const cls = `list-disc list-outside mb-2 ml-6 pl-2 ${props.className || ''}`.trim();
                    return <ul {...props} className={cls}>{children}</ul>;
                },
                ol({ children, ...props }: React.OlHTMLAttributes<HTMLOListElement> & { children: React.ReactNode }) {
                    // Pass through props so attributes like start/type are preserved
                    const cls = `list-decimal list-outside mb-2 ml-6 pl-2 ${props.className || ''}`.trim();
                    return <ol {...props} className={cls}>{children}</ol>;
                },
                li({ children, ...props }: React.LiHTMLAttributes<HTMLLIElement> & { children: React.ReactNode }) {
                    // Preserve value attribute for list items inside ordered lists
                    const cls = `mb-1 pl-2 ${props.className || ''}`.trim();
                    return <li {...props} className={cls}>{children}</li>;
                },
                blockquote({ children }: { children: React.ReactNode }) {
                    return (
                        <blockquote className="border-l-4 border-gray-300 pl-4 italic my-4 text-gray-600">
                            {children}
                        </blockquote>
                    );
                },
                a: (props: { href?: string; children?: React.ReactNode; title?: string }) => {
                    // Handle in-document anchors (#fragment) by scrolling within the markdown container
                    if (props.href && props.href.startsWith('#')) {
                        const onClick = (e: React.MouseEvent<HTMLAnchorElement>) => {
                            e.preventDefault();
                            const id = props.href!.slice(1);
                            const target = e.currentTarget.ownerDocument?.getElementById(id);
                            if (target && typeof (target as any).scrollIntoView === 'function') {
                                try {
                                    (target as any).scrollIntoView({ behavior: 'smooth', block: 'start', inline: 'nearest' });
                                } catch {
                                    (target as any).scrollIntoView();
                                }
                            }
                        };
                        return (
                            <a href={props.href} onClick={onClick} className="text-blue-600 hover:text-blue-800 underline cursor-pointer">
                                {props.children}
                            </a>
                        );
                    }
                    // Check if it's a relative path that needs authentication
                    const isRelative = props.href && 
                                      !props.href.startsWith('http://') && 
                                      !props.href.startsWith('https://') && 
                                      !props.href.startsWith('/') &&
                                      !props.href.startsWith('mailto:') &&
                                      !props.href.startsWith('tel:') &&
                                      !props.href.startsWith('#');
                    
                    // If relative and we have context (notebook OR project), use AuthenticatedContent
                    if (isRelative && projectId && (notebookId || resolveProjectFilePath)) {
                        return <AuthenticatedContent href={props.href} elementType="a" projectId={projectId} notebookId={notebookId} resolveProjectFilePath={resolveProjectFilePath} basePath={basePath}>{props.children}</AuthenticatedContent>;
                    }
                    
                    // Otherwise use standard external link handler
                    return <ExternalLink href={props.href}>{props.children}</ExternalLink>;
                },
                img: (props: { src?: string; alt?: string; title?: string }) => (
                    <AuthenticatedContent {...props} elementType="img" projectId={projectId} notebookId={notebookId} resolveProjectFilePath={resolveProjectFilePath} basePath={basePath} />
                ),
                table({ children }: { children: React.ReactNode }) {
                    return (
                        <div className="my-4" style={{ overflowX: 'auto', minHeight: 'fit-content', height: 'auto', display: 'block' }}>
                            <table className="min-w-full border-collapse border border-gray-300">
                                {children}
                            </table>
                        </div>
                    );
                },
                th({ children }: { children: React.ReactNode }) {
                    return (
                        <th className="px-4 py-2 text-left text-sm font-medium text-gray-900 bg-gray-100 border border-gray-300">
                            {children}
                        </th>
                    );
                },
                td({ children }: { children: React.ReactNode }) {
                    // Attempt to detect media tokens inside table cells too
                    const buildText = (n: any): string => {
                        if (!n) return '';
                        if (typeof n === 'string') return n;
                        if (Array.isArray(n)) return n.map(buildText).join('');
                        if (n?.props && n.props.children !== undefined) return buildText(n.props.children);
                        return '';
                    };
                    const raw = buildText(children);
                    if (raw) {
                        const videoMatch = raw.match(/\[VIDEO:([^\]]+)\]/);
                        if (videoMatch) {
                            return (
                                <td className="px-4 py-2 text-sm text-gray-900 border border-gray-300">
                                    <AuthenticatedContent src={videoMatch[1]} elementType="video" projectId={projectId} notebookId={notebookId} resolveProjectFilePath={resolveProjectFilePath} basePath={basePath} />
                                </td>
                            );
                        }
                        const audioMatch = raw.match(/\[AUDIO:([^\]]+)\]/);
                        if (audioMatch) {
                            return (
                                <td className="px-4 py-2 text-sm text-gray-900 border border-gray-300">
                                    <AuthenticatedContent src={audioMatch[1]} elementType="audio" projectId={projectId} notebookId={notebookId} resolveProjectFilePath={resolveProjectFilePath} basePath={basePath} />
                                </td>
                            );
                        }
                    }
                    return (
                        <td className="px-4 py-2 text-sm text-gray-900 border border-gray-300">
                            {children}
                        </td>
                    );
                },
                code({ inline, className: codeClassName, children, ...props }: {
                    inline?: boolean;
                    className?: string;
                    children: React.ReactNode;
                }) {
                    const match = /language-(\w+)/.exec(codeClassName || '');
                    if (!inline && match?.[1] === 'mermaid') {
                        return <MermaidRenderer chart={String(children).replace(/\n$/, '')} />;
                    }
                    return (
                        <code className={codeClassName} {...props}>
                            {children}
                        </code>
                    );
                },
            }}
        />
    );

    if (inlineMode) {
        return markdownContent;
    }

    return (
        <PreviewContainer contentClassName={combinedClass} hideHeader={hidePreviewHeader}>
            {markdownContent}
        </PreviewContainer>
    );
}

export default React.memo(MarkdownViewer); 
