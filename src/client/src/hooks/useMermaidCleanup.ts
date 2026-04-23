import { useEffect } from 'react';
import { setupMermaidCleanupInterval } from '../utils/mermaidCleanup';

/**
 * React hook that sets up a global cleanup interval for orphaned Mermaid elements.
 * This provides a safety net to ensure DOM elements don't accumulate over time.
 * 
 * @param intervalMs - How often to run cleanup in milliseconds (default: 30 seconds)
 * @param enabled - Whether the cleanup should be enabled (default: true)
 */
export function useMermaidCleanup(intervalMs: number = 30000, enabled: boolean = true) {
    useEffect(() => {
        if (!enabled) return;

        // Set up the cleanup interval
        const cleanup = setupMermaidCleanupInterval(intervalMs);

        // Return the cleanup function to be called when component unmounts
        return cleanup;
    }, [intervalMs, enabled]);
} 