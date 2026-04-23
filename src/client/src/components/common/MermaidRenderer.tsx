import { useEffect, useRef, useState } from 'react';
import { cleanupOrphanedMermaidElements, validateMermaidSyntax } from '../../utils/mermaidCleanup';

// Ensure Mermaid is only initialized once across the frontend bundle
let mermaidInitialized = false;

interface MermaidRendererProps {
    /**
     * The Mermaid diagram source text to render as SVG.
     */
    chart: string;
    /**
     * Optional TailwindCSS classes applied to the SVG container.
     */
    className?: string;
    /**
     * When true, indicates the chart is being streamed in and may be incomplete.
     * Triggers debounce and safe rendering modes.
     */
    isStreaming?: boolean;
}

/**
 * Renders a Mermaid diagram from the provided chart definition.
 *
 * The component dynamically imports the `mermaid` library to avoid increasing the
 * initial JavaScript bundle size for users who never view diagrams.
 */
export default function MermaidRenderer({ chart, className = '', isStreaming = false }: MermaidRendererProps) {
    const containerRef = useRef<HTMLDivElement>(null);
    const [svgContent, setSvgContent] = useState<string | null>(null);
    const [errorMessage, setErrorMessage] = useState<string | null>(null);

    useEffect(() => {
        let isCancelled = false;
        let timeoutId: ReturnType<typeof setTimeout>;

        async function renderMermaid() {
            if (!containerRef.current) return;

            try {
                // Dynamically import mermaid so it is only loaded in the browser when needed
                const mermaidLib = await import('mermaid');
                const mermaid = (mermaidLib as any).default ?? mermaidLib;

                if (!mermaidInitialized) {
                    // Configure mermaid with proper error handling and cleanup
                    mermaid.initialize({ 
                        startOnLoad: false,
                        // Prevent mermaid from using body as container
                        suppressErrorRendering: true,
                        // Configure security settings
                        securityLevel: 'loose',
                        // Ensure proper cleanup
                        htmlLabels: false
                    });
                    mermaidInitialized = true;
                }

                const id = `mermaid-${Math.random().toString(36).substring(2, 9)}`;

                // Clean up any existing orphaned SVG elements from previous failed renders
                // NOTE: We only do this before a fresh render to avoid thrashing during streaming
                cleanupOrphanedMermaidElements();

                // Basic syntax validation before attempting to render
                const syntaxCheck = validateMermaidSyntax(chart);
                if (!syntaxCheck.isValid) {
                    throw new Error(syntaxCheck.error || 'Invalid Mermaid syntax');
                }

                // Advanced validation using mermaid's parse function
                const isValidSyntax = await mermaid.parse(chart);
                if (!isValidSyntax) {
                    throw new Error('Invalid Mermaid syntax - failed advanced validation');
                }

                // mermaid.render returns a Promise in v10+. Destructure the SVG string
                const { svg } = await mermaid.render(id, chart);

                if (!isCancelled) {
                    setSvgContent(svg);
                    setErrorMessage(null);
                }
            } catch (err) {
                // Clean up any orphaned elements that might have been created during failed render
                cleanupOrphanedMermaidElements();

                if (!isCancelled) {
                    // Show an inline error if the diagram fails to render
                    // eslint-disable-next-line no-console
                    console.error('Failed to render mermaid diagram:', err);
                    const msg = err instanceof Error ? err.message : String(err);
                    setErrorMessage(msg);
                    
                    // Clear SVG content so we don't show a stale diagram mixed with error?
                    // Or keep it? Let's clear to be safe and show error/code.
                    setSvgContent(null);
                }
            }
        }

        if (isStreaming) {
            // Debounce during streaming to avoid rendering every token
            timeoutId = setTimeout(renderMermaid, 1000);
        } else {
            renderMermaid();
        }

        return () => {
            // Mark as cancelled so async work doesn't try to manipulate an unmounted node
            isCancelled = true;
            clearTimeout(timeoutId);
            
            // NOTE: We DO NOT call cleanupOrphanedMermaidElements here during unmount
            // because in a streaming/React environment, this component might unmount/remount
            // rapidly, and aggressive global DOM cleanup can affect other components.
        };
    }, [chart, isStreaming]);

    if (errorMessage) {
        return (
            <div className={`mermaid-error my-4 ${className}`.trim()}>
                <div className="border border-red-300 bg-red-50 p-4 rounded-md">
                    <div className="flex">
                        <div className="flex-shrink-0">
                            <svg className="h-5 w-5 text-red-400" viewBox="0 0 20 20" fill="currentColor">
                                <path fill-rule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.707 7.293a1 1 0 00-1.414 1.414L8.586 10l-1.293 1.293a1 1 0 101.414 1.414L10 11.414l1.293 1.293a1 1 0 001.414-1.414L11.414 10l1.293-1.293a1 1 0 00-1.414-1.414L10 8.586 8.707 7.293z" clip-rule="evenodd" />
                            </svg>
                        </div>
                        <div className="ml-3">
                            <h3 className="text-sm font-medium text-red-800">Mermaid Diagram Error</h3>
                            <div className="mt-2 text-sm text-red-700">
                                <p>Failed to render diagram. Please check the syntax.</p>
                                <details className="mt-2">
                                    <summary className="cursor-pointer text-red-600 underline">View error details</summary>
                                    <pre className="mt-2 text-xs bg-red-100 p-2 rounded overflow-auto">{errorMessage}</pre>
                                </details>
                            </div>
                        </div>
                    </div>
                </div>
                {/* Fallback to showing code when error occurs */}
                <pre className="mt-2 text-xs text-gray-500 bg-gray-50 p-2 rounded overflow-x-auto">{chart}</pre>
            </div>
        );
    }

    if (svgContent) {
        return (
            <div 
                ref={containerRef} 
                className={`mermaid-diagram my-4 ${className}`.trim()}
                dangerouslySetInnerHTML={{ __html: svgContent }}
            />
        );
    }

    // Pending state (waiting for debounce or render)
    // Show raw code block so content doesn't "disappear" during streaming
    return (
        <div ref={containerRef} className={`mermaid-pending my-4 ${className}`.trim()}>
            <div className="bg-gray-50 border border-blue-200 rounded p-3 relative">
                {isStreaming && (
                    <div className="absolute top-2 right-2 flex items-center space-x-1 text-xs text-blue-500">
                        <div className="w-2 h-2 bg-blue-500 rounded-full animate-pulse"></div>
                        <span>Rendering diagram...</span>
                    </div>
                )}
                <pre className="text-sm overflow-x-auto text-gray-700">{chart}</pre>
            </div>
        </div>
    );
}
