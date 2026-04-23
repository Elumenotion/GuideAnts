import { useState, useEffect, useRef, useCallback } from 'react';
import { api } from '../services/api';
import { ProjectNotebook } from '../types/project';

interface UseProjectNotebooksPollingProps {
    projectId: string;
    enabled?: boolean;
    pollInterval?: number; // milliseconds, default 10s
}

export function useProjectNotebooksPolling({
    projectId,
    enabled = true,
    pollInterval = 10000 // 10 seconds
}: UseProjectNotebooksPollingProps) {
    const [notebooks, setNotebooks] = useState<ProjectNotebook[]>([]);
    const [isLoading, setIsLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [lastUpdated, setLastUpdated] = useState<Date | null>(null);
    
    const intervalRef = useRef<NodeJS.Timeout | null>(null);
    const abortControllerRef = useRef<AbortController | null>(null);

    const fetchProjectNotebooks = useCallback(async (signal?: AbortSignal) => {
        try {
            setIsLoading(true);
            setError(null);
            
            //console.log(`📚 Polling project notebooks for project ${projectId}...`);
            
            // Check if request was aborted before making API call
            if (signal?.aborted) {
                return;
            }
            
            // Get project details and extract notebooks
            const projectDetails = await api.projects.getProjectDetails(projectId);
            
            // Check if request was aborted after API call
            if (!signal?.aborted) {
                setNotebooks(projectDetails.notebooks || []);
                setLastUpdated(new Date());
                //console.log(`📚 Project notebooks updated: ${projectDetails.notebooks?.length || 0} notebooks`);
            }
        } catch (err) {
            if (!signal?.aborted) {
                const errorMessage = err instanceof Error ? err.message : 'Failed to fetch project notebooks';
                setError(errorMessage);
                //console.error('📚 Project notebooks polling error:', err);
            }
        } finally {
            if (!signal?.aborted) {
                setIsLoading(false);
            }
        }
    }, [projectId]);

    // Start/stop polling based on enabled flag
    useEffect(() => {
        if (!enabled || !projectId) {
            //console.log('📚 Project notebooks polling disabled or missing project ID');
            return;
        }

        // Initial fetch
        const controller = new AbortController();
        abortControllerRef.current = controller;
        fetchProjectNotebooks(controller.signal);

        // Set up polling interval
        intervalRef.current = setInterval(() => {
            const newController = new AbortController();
            abortControllerRef.current = newController;
            fetchProjectNotebooks(newController.signal);
        }, pollInterval);

        // Cleanup function
        return () => {
            if (intervalRef.current) {
                clearInterval(intervalRef.current);
                intervalRef.current = null;
            }
            if (abortControllerRef.current) {
                abortControllerRef.current.abort();
                abortControllerRef.current = null;
            }
        };
    }, [enabled, projectId, pollInterval, fetchProjectNotebooks]);

    // Manual refresh function
    const refresh = useCallback(() => {
        if (abortControllerRef.current) {
            abortControllerRef.current.abort();
        }
        const controller = new AbortController();
        abortControllerRef.current = controller;
        fetchProjectNotebooks(controller.signal);
    }, [fetchProjectNotebooks]);

    return {
        notebooks,
        isLoading,
        error,
        lastUpdated,
        refresh
    };
}