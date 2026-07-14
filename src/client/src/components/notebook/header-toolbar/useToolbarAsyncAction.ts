import { useCallback, useState } from 'react';

export function formatToolbarActionError(error: unknown): string {
  if (error instanceof Error && error.message.trim()) {
    return error.message.trim();
  }
  if (typeof error === 'string' && error.trim()) {
    return error.trim();
  }
  return 'Action failed. Check the browser console for details.';
}

export function useToolbarAsyncAction(setInFlight: (value: boolean) => void) {
  const [error, setError] = useState<string | null>(null);

  const clearError = useCallback(() => {
    setError(null);
  }, []);

  const run = useCallback(
    async (action: () => Promise<void>) => {
      setError(null);
      setInFlight(true);
      try {
        await action();
      } catch (err) {
        const message = formatToolbarActionError(err);
        console.error('[toolbar]', err);
        setError(message);
      } finally {
        setInFlight(false);
      }
    },
    [setInFlight]
  );

  return { error, clearError, run };
}
