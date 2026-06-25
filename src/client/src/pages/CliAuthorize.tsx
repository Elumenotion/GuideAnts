import { useState, useEffect } from 'react';
import { useSearchParams } from 'react-router-dom';
import { FaCheck, FaSpinner, FaTimes, FaTerminal } from 'react-icons/fa';
import { TextActionButton } from './settings/components/shared/ActionButtons';
import { getErrorMessage } from './settings/utils';
import { api } from '../services/api';

type PageState = 'idle' | 'submitting' | 'approved' | 'denied' | 'error';

const AUTO_CLOSE_MS = 1500;

export default function CliAuthorize() {
  const [searchParams] = useSearchParams();
  const session = searchParams.get('session');

  const [state, setState] = useState<PageState>('idle');
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (state !== 'approved' && state !== 'denied') return;
    const timer = setTimeout(() => window.close(), AUTO_CLOSE_MS);
    return () => clearTimeout(timer);
  }, [state]);

  const handleApprove = async () => {
    if (!session) return;
    setState('submitting');
    setError(null);
    try {
      await api.cli.approveSession(session);
      setState('approved');
    } catch (err: unknown) {
      const status = (err as { status?: number }).status;
      if (status === 404 || status === 410) {
        setError('This request is no longer valid. Return to your terminal and start over.');
      } else {
        setError(getErrorMessage(err, 'Failed to approve the request. Please try again.'));
      }
      setState('error');
    }
  };

  const handleDeny = async () => {
    if (session) {
      try { await api.cli.denySession(session); } catch { /* best-effort */ }
    }
    setState('denied');
  };

  const missingSession = !session;

  return (
    <div className="min-h-screen bg-gray-50 flex items-center justify-center px-4">
      <div className="w-full max-w-md">
        <div className="rounded border border-gray-200 bg-white p-6 shadow-sm text-center">
          <img src="/guide.png" alt="GuideAnts logo" className="mx-auto h-12 w-12" />

          {missingSession ? (
            <>
              <h1 className="mt-3 text-2xl font-bold text-gray-900">Invalid Link</h1>
              <p className="mt-2 text-sm text-gray-600">
                This authorization link is missing its session identifier.
              </p>
            </>
          ) : state === 'approved' ? (
            <>
              <div className="mt-4 flex justify-center">
                <FaCheck className="text-emerald-600 text-2xl" aria-hidden="true" />
              </div>
              <h1 className="mt-3 text-2xl font-bold text-gray-900">Approved</h1>
              <p className="mt-2 text-sm text-gray-600">
                Approved — this window will close automatically.
              </p>
            </>
          ) : state === 'denied' ? (
            <>
              <div className="mt-4 flex justify-center">
                <FaTimes className="text-gray-400 text-2xl" aria-hidden="true" />
              </div>
              <h1 className="mt-3 text-2xl font-bold text-gray-900">Request Denied</h1>
              <p className="mt-2 text-sm text-gray-600">
                Request denied — this window will close automatically.
              </p>
            </>
          ) : (
            <>
              <div className="mt-4 flex justify-center">
                <FaTerminal className="text-gray-700 text-2xl" aria-hidden="true" />
              </div>
              <h1 className="mt-3 text-2xl font-bold text-gray-900">
                Authorize command-line mount access?
              </h1>
              <p className="mt-2 text-sm text-gray-600">
                Approving will let the command-line installer running on this machine create a
                folder mount on your behalf. This approval is single-use and expires shortly.
              </p>

              {error ? (
                <div
                  className="mt-4 rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700"
                  role="alert"
                >
                  {error}
                </div>
              ) : null}

              <div className="mt-6 flex items-center justify-center gap-2">
                <TextActionButton
                  tone="primary"
                  icon={state === 'submitting' ? <FaSpinner className="animate-spin" /> : <FaCheck />}
                  disabled={state === 'submitting'}
                  onClick={() => void handleApprove()}
                >
                  {state === 'submitting' ? 'Approving...' : 'Approve'}
                </TextActionButton>
                <TextActionButton
                  tone="neutral"
                  icon={<FaTimes />}
                  disabled={state === 'submitting'}
                  onClick={() => void handleDeny()}
                >
                  Deny
                </TextActionButton>
              </div>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
