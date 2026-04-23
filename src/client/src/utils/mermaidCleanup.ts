/**
 * Utility functions for cleaning up Mermaid diagram artifacts from the DOM
 */

/**
 * Removes orphaned Mermaid elements from the document body.
 * This is necessary because Mermaid can leave behind SVG elements when rendering fails.
 */
export function cleanupOrphanedMermaidElements(): void {
    // Look for elements that Mermaid might have created and left behind
    const selectors = [
        'svg[id^="mermaid-"]',
        'div[id^="mermaid-"]',
        'div[data-processed-by="mermaid"]',
        '.mermaid[data-processed]'
    ];

    selectors.forEach(selector => {
        const elements = document.querySelectorAll(selector);
        elements.forEach(element => {
            // Only remove elements that are direct children of body (orphaned)
            if (element.parentNode === document.body) {
                console.warn('Cleaning up orphaned Mermaid element:', element);
                element.remove();
            }
        });
    });
}

/**
 * Validates if a Mermaid chart string has basic syntax requirements
 * before attempting to render it. This helps prevent some common parsing errors.
 */
export function validateMermaidSyntax(chart: string): { isValid: boolean; error?: string } {
    if (!chart || typeof chart !== 'string') {
        return { isValid: false, error: 'Chart must be a non-empty string' };
    }

    const trimmedChart = chart.trim();
    if (!trimmedChart) {
        return { isValid: false, error: 'Chart cannot be empty' };
    }

    // Check for common diagram types
    const diagramTypes = [
        'graph', 'flowchart', 'sequenceDiagram', 'classDiagram', 'stateDiagram',
        'erDiagram', 'journey', 'gantt', 'pie', 'gitgraph', 'mindmap', 'timeline',
        'requirementDiagram', 'c4Context'
    ];

    const hasValidStart = diagramTypes.some(type => 
        trimmedChart.toLowerCase().startsWith(type.toLowerCase())
    );

    if (!hasValidStart) {
        return { 
            isValid: false, 
            error: `Chart must start with a valid diagram type. Supported types: ${diagramTypes.join(', ')}` 
        };
    }

    return { isValid: true };
}

/**
 * Sets up a global cleanup interval to periodically check for and remove
 * orphaned Mermaid elements. Useful as a safety net.
 */
export function setupMermaidCleanupInterval(intervalMs: number = 30000): () => void {
    const intervalId = setInterval(() => {
        cleanupOrphanedMermaidElements();
    }, intervalMs);

    // Return cleanup function
    return () => {
        clearInterval(intervalId);
    };
} 