import { useState, useEffect, useRef, useCallback } from 'react';
import { api } from '../services/api';
import { NotebookConversationDto } from '../types/notebook';

interface UseConversationListPollingProps {
  projectId: string;
  notebookId: string;
  enabled?: boolean;
  pollInterval?: number; // milliseconds, default 30s
}

export function useConversationListPolling({
  projectId,
  notebookId,
  enabled = true,
  pollInterval = 30000
}: UseConversationListPollingProps) {
  const [conversations, setConversations] = useState<NotebookConversationDto[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);
  
  const intervalRef = useRef<NodeJS.Timeout | null>(null);
  const abortControllerRef = useRef<AbortController | null>(null);

  const fetchConversations = useCallback(async (signal?: AbortSignal) => {
    try {
      setIsLoading(true);
      setError(null);
      
      //console.log(`📋 Polling conversations for notebook ${notebookId}...`);
      
      // Check if request was aborted before making API call
      if (signal?.aborted) {
        return;
      }
      
      const conversations = await api.projects.notebooks.conversations.getAll(
        projectId, 
        notebookId
      );
      
      // Check if request was aborted after API call
      if (!signal?.aborted) {
        setConversations(conversations || []);
        setLastUpdated(new Date());
        //console.log(`📋 Conversations list updated: ${conversations?.length || 0} items`);
      }
    } catch (err) {
      if (!signal?.aborted) {
        const errorMessage = err instanceof Error ? err.message : 'Failed to fetch conversations';
        setError(errorMessage);
        //console.error('📋 Conversation list polling error:', err);
      }
    } finally {
      if (!signal?.aborted) {
        setIsLoading(false);
      }
    }
  }, [projectId, notebookId]);

  // Start/stop polling based on enabled flag
  useEffect(() => {
    if (!enabled || !projectId || !notebookId) {
      //console.log('📋 Conversation polling disabled or missing IDs');
      return;
    }

    //console.log(`📋 Starting conversation polling (interval: ${pollInterval}ms)`);

    // Initial fetch
    const controller = new AbortController();
    abortControllerRef.current = controller;
    fetchConversations(controller.signal);

    // Set up polling interval
    intervalRef.current = setInterval(() => {
      const newController = new AbortController();
      abortControllerRef.current = newController;
      fetchConversations(newController.signal);
    }, pollInterval);

    // Cleanup function
    return () => {
      //console.log('📋 Stopping conversation polling');
      if (intervalRef.current) {
        clearInterval(intervalRef.current);
        intervalRef.current = null;
      }
      if (abortControllerRef.current) {
        abortControllerRef.current.abort();
        abortControllerRef.current = null;
      }
    };
  }, [enabled, projectId, notebookId, pollInterval, fetchConversations]);

  // Manual refresh function
  const refresh = useCallback(() => {
    //console.log('📋 Manual refresh requested');
    if (abortControllerRef.current) {
      abortControllerRef.current.abort();
    }
    const controller = new AbortController();
    abortControllerRef.current = controller;
    fetchConversations(controller.signal);
  }, [fetchConversations]);

  return {
    conversations,
    isLoading,
    error,
    lastUpdated,
    refresh
  };
}