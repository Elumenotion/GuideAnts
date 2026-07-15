import { useState, useEffect, useRef, useCallback } from 'react';
import { api } from '../services/api';
import { FolderTreeDto } from '../types/project';

interface UseProjectFilesPollingProps {
    projectId: string;
    enabled?: boolean;
    pollInterval?: number; // milliseconds, default 10s
}

/**
 * Structural comparison of two project folder trees. Returns true when the trees are
 * identical, so the polling hook can keep the same reference and avoid re-renders that
 * interrupt user interactions (mirrors the notebook polling hook's behavior).
 */
function areTreesEqual(a: FolderTreeDto | null, b: FolderTreeDto | null): boolean {
    if (a === b) return true;
    if (!a || !b) return false;

    if (a.id !== b.id ||
        a.name !== b.name ||
        a.relativePath !== b.relativePath ||
        a.isHostMount !== b.isHostMount ||
        a.mountId !== b.mountId ||
        a.mountStatus !== b.mountStatus ||
        a.isLinked !== b.isLinked) {
        return false;
    }

    if (a.files.length !== b.files.length) return false;
    for (let i = 0; i < a.files.length; i++) {
        const fileA = a.files[i];
        const fileB = b.files[i];
        if (fileA.id !== fileB.id ||
            fileA.fileName !== fileB.fileName ||
            fileA.relativePath !== fileB.relativePath ||
            fileA.fileSize !== fileB.fileSize ||
            fileA.latestVersion !== fileB.latestVersion) {
            return false;
        }
    }

    if (a.subFolders.length !== b.subFolders.length) return false;
    for (let i = 0; i < a.subFolders.length; i++) {
        if (!areTreesEqual(a.subFolders[i], b.subFolders[i])) {
            return false;
        }
    }

    return true;
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
                // Only update state when the tree actually changed. Keeping the same
                // reference on no-op polls prevents unnecessary re-renders.
                setFolderTree(prev => (areTreesEqual(prev, tree) ? prev : tree));
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

        // Listen for manual refresh requests (e.g. after mounted file rename)
        const handleRefreshEvent = () => {
            if (abortControllerRef.current) {
                abortControllerRef.current.abort();
            }
            const refreshController = new AbortController();
            abortControllerRef.current = refreshController;
            fetchProjectFiles(refreshController.signal);
        };
        window.addEventListener('refresh-project-files', handleRefreshEvent);

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
            window.removeEventListener('refresh-project-files', handleRefreshEvent);
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