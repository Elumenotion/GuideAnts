import { useState, useEffect, useRef, useCallback } from 'react';
import { api } from '../services/api';
import { FolderTreeDto } from '../types/project';

interface UseProjectFilesPollingProps {
    projectId: string;
    enabled?: boolean;
    pollInterval?: number; // milliseconds, default 10s
}

export function useProjectFilesPolling({
    projectId,
    enabled = true,
    pollInterval = 10000 // 10 seconds
}: UseProjectFilesPollingProps) {
    const [folderTree, setFolderTree] = useState<FolderTreeDto | null>(null);
    const [isLoading, setIsLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [lastUpdated, setLastUpdated] = useState<Date | null>(null);
    
    const intervalRef = useRef<NodeJS.Timeout | null>(null);
    const abortControllerRef = useRef<AbortController | null>(null);

    const fetchProjectFiles = useCallback(async (signal?: AbortSignal) => {
        try {
            setIsLoading(true);
            setError(null);
            
            //console.log(`📁 Polling project files for project ${projectId}...`);
            
            // Check if request was aborted before making API call
            if (signal?.aborted) {
                return;
            }
            
            const tree = await api.projects.folders.getFolderTree(projectId);
            
            // Check if request was aborted after API call
            if (!signal?.aborted) {
                setFolderTree(tree);
                setLastUpdated(new Date());
                //console.log(`📁 Project files updated: ${tree ? 'tree loaded' : 'no tree'}`);
            }
        } catch (err) {
            if (!signal?.aborted) {
                const errorMessage = err instanceof Error ? err.message : 'Failed to fetch project files';
                setError(errorMessage);
                //console.error('📁 Project files polling error:', err);
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
            //console.log('📁 Project files polling disabled or missing project ID');
            return;
        }

        // Initial fetch
        const controller = new AbortController();
        abortControllerRef.current = controller;
        fetchProjectFiles(controller.signal);

        // Set up polling interval
        intervalRef.current = setInterval(() => {
            const newController = new AbortController();
            abortControllerRef.current = newController;
            fetchProjectFiles(newController.signal);
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
    }, [enabled, projectId, pollInterval, fetchProjectFiles]);

    // Manual refresh function
    const refresh = useCallback(() => {
        if (abortControllerRef.current) {
            abortControllerRef.current.abort();
        }
        const controller = new AbortController();
        abortControllerRef.current = controller;
        fetchProjectFiles(controller.signal);
    }, [fetchProjectFiles]);

    return {
        folderTree,
        isLoading,
        error,
        lastUpdated,
        refresh
    };
}