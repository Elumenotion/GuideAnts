import { ReactNode, useEffect, useState } from 'react';
import LoadingSpinner from './LoadingSpinner';
import { API_BASE_URL } from '../config/apiConfig';

interface StartupGateProps {
  children: ReactNode;
  enabled?: boolean;
  pollIntervalMs?: number;
}

type StartupStatus = 'starting' | 'ready';

async function probeStartup(signal: AbortSignal): Promise<StartupStatus> {
  try {
    const response = await fetch(`${API_BASE_URL}/startup`, {
      cache: 'no-store',
      headers: {
        Accept: 'application/json',
        'Cache-Control': 'no-cache',
      },
      signal,
    });

    if (!response.ok) {
      return 'starting';
    }

    const payload = await response.json().catch(() => null) as { status?: string } | null;
    return payload?.status === 'ready' || payload == null
      ? 'ready'
      : 'starting';
  } catch {
    return 'starting';
  }
}

export default function StartupGate({
  children,
  enabled = true,
  pollIntervalMs = 1000,
}: StartupGateProps) {
  const [status, setStatus] = useState<StartupStatus>(enabled ? 'starting' : 'ready');
  const [attempts, setAttempts] = useState(0);

  useEffect(() => {
    if (!enabled) {
      setStatus('ready');
      return undefined;
    }

    let disposed = false;
    let timerId: number | undefined;
    let controller: AbortController | undefined;

    const poll = async () => {
      controller?.abort();
      controller = new AbortController();

      const nextStatus = await probeStartup(controller.signal);
      if (disposed) {
        return;
      }

      if (nextStatus === 'ready') {
        setStatus('ready');
        return;
      }

      setAttempts((current) => current + 1);
      timerId = window.setTimeout(() => {
        void poll();
      }, pollIntervalMs);
    };

    void poll();

    return () => {
      disposed = true;
      controller?.abort();
      if (timerId !== undefined) {
        window.clearTimeout(timerId);
      }
    };
  }, [enabled, pollIntervalMs]);

  if (status === 'ready') {
    return <>{children}</>;
  }

  const message = attempts >= 10
    ? 'GuideAnts is starting up. Waiting for the database and settings to be ready...'
    : 'Starting GuideAnts...';

  return (
    <div className="h-full overflow-auto bg-gray-50 p-8">
      <LoadingSpinner message={message} />
    </div>
  );
}
