import { useEffect, useState, useCallback } from 'react';
import { api } from '../services/api';
import { FileLineageEvent } from '../types/fileLineage';

interface UseFileLineageResult {
    events: FileLineageEvent[];
    isLoading: boolean;
    error: string | null;
    refresh: () => void;
}

export function useFileLineage(projectId: string, fileId: string): UseFileLineageResult {
    const [events, setEvents] = useState<FileLineageEvent[]>([]);
    const [isLoading, setIsLoading] = useState<boolean>(true);
    const [error, setError] = useState<string | null>(null);

    const fetchEvents = useCallback(async () => {
        if (!projectId || !fileId) return;
        try {
            setIsLoading(true);
            const list = await api.projects.getFileHistory(projectId, fileId);
            // Sort newest first (assuming timestamp field is ISO string)
            const sorted = list.sort((a, b) => new Date(b.timestamp).getTime() - new Date(a.timestamp).getTime());
            setEvents(sorted);
            setError(null);
        } catch (err) {
            const msg = err instanceof Error ? err.message : 'Failed to load history';
            setError(msg);
        } finally {
            setIsLoading(false);
        }
    }, [projectId, fileId]);

    useEffect(() => {
        fetchEvents();
    }, [fetchEvents]);

    return { events, isLoading, error, refresh: fetchEvents };
} 