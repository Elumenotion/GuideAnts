import type { ReactNode } from 'react';
import { FaSyncAlt } from 'react-icons/fa';
import type { LocalModelsUpstreamFailure } from '../../../../types/settings';
import { IconActionButton } from './ActionButtons';

/**
 * Shared framing for the "local service admin" sections of the non-chat
 * service editors. Renders a consistent heading and the non-product states
 * (hidden, loading, error) so each bespoke manager only has to implement
 * the product UI itself.
 */
export type LocalCapabilityPhase = 'hidden' | 'loading' | 'available' | 'error';

interface LocalCapabilityFrameProps {
  title: string;
  phase: LocalCapabilityPhase;
  errorMessage?: string;
  upstream?: LocalModelsUpstreamFailure;
  onRefresh?: () => void;
  children?: ReactNode;
}

export function LocalCapabilityFrame({
  title,
  phase,
  errorMessage,
  upstream,
  onRefresh,
  children,
}: LocalCapabilityFrameProps) {
  if (phase === 'hidden') {
    return null;
  }

  const heading = (
    <div className="flex items-center justify-between gap-3">
      <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">{title}</div>
      {onRefresh ? (
        <IconActionButton
          label="Refresh"
          tone="neutral"
          icon={<FaSyncAlt />}
          onClick={onRefresh}
          title="Refresh"
        />
      ) : null}
    </div>
  );

  if (phase === 'loading') {
    return (
      <div className="space-y-2 border-t border-gray-100 pt-4">
        {heading}
        <p className="text-xs text-gray-600">Contacting local service…</p>
      </div>
    );
  }

  if (phase === 'error') {
    const unavailable = classifyUnavailableFailure(messageOrFallback(errorMessage), upstream);
    return (
      <div className="space-y-2 border-t border-gray-100 pt-4">
        {heading}
        {unavailable ? (
          <div className="rounded border border-amber-200 bg-amber-50 p-3 text-xs text-amber-950">
            <p className="font-semibold">{unavailable.title}</p>
            <p className="mt-1">{unavailable.detail}</p>
          </div>
        ) : (
          <UpstreamFailureDetails message={errorMessage} upstream={upstream} />
        )}
      </div>
    );
  }

  return (
    <div className="space-y-3 border-t border-gray-100 pt-4">
      {heading}
      {children}
    </div>
  );
}

function messageOrFallback(message?: string): string {
  return message?.trim() || 'Request failed.';
}

function classifyUnavailableFailure(
  message: string,
  upstream?: LocalModelsUpstreamFailure,
): { title: string; detail: string } | null {
  const normalizedMessage = message.toLowerCase();
  const normalizedTarget = upstream?.upstreamTarget.toLowerCase() ?? '';

  if (normalizedMessage.includes('no local ') && normalizedMessage.includes('configured for this container yet')) {
    return {
      title: 'Local runtime unavailable',
      detail: message,
    };
  }

  if (normalizedMessage.includes('does not expose local model operations')) {
    return {
      title: 'Local runtime unavailable',
      detail: 'This container does not currently have the matching local service server configured.',
    };
  }

  if (upstream?.upstreamStatus === 0) {
    if (normalizedTarget.includes('127.0.0.1:9')) {
      return {
        title: 'Local runtime unavailable',
        detail: 'This container is using the placeholder local-runtime URL, so no local service server is configured yet.',
      };
    }

    return {
      title: 'Local runtime unavailable',
      detail: 'The local service server is not reachable right now.',
    };
  }

  return null;
}

/**
 * Render the full proxy chain for a local-models failure. No information is
 * dropped: the UI shows the message, the upstream target URL, the upstream
 * status line, the upstream content type, and the raw upstream body.
 */
function UpstreamFailureDetails({
  message,
  upstream,
}: {
  message?: string;
  upstream?: LocalModelsUpstreamFailure;
}) {
  return (
    <div className="space-y-2 rounded border border-red-200 bg-red-50 p-3 text-xs text-red-900">
      <p className="font-semibold">{message ?? 'Request failed.'}</p>
      {upstream ? (
        <dl className="grid grid-cols-[max-content_1fr] gap-x-3 gap-y-1 font-mono">
          <dt className="text-red-700">Upstream:</dt>
          <dd className="break-all">{upstream.upstreamTarget || '(unknown)'}</dd>
          <dt className="text-red-700">Status:</dt>
          <dd>
            {upstream.upstreamStatus} {upstream.upstreamStatusText}
          </dd>
          {upstream.upstreamContentType ? (
            <>
              <dt className="text-red-700">Content-Type:</dt>
              <dd className="break-all">{upstream.upstreamContentType}</dd>
            </>
          ) : null}
          {upstream.upstreamBody ? (
            <>
              <dt className="self-start text-red-700">Body:</dt>
              <dd>
                <pre className="max-h-48 overflow-auto whitespace-pre-wrap break-all rounded border border-red-300 bg-white p-2 text-[11px] text-red-900">
                  {upstream.upstreamBody}
                </pre>
              </dd>
            </>
          ) : null}
        </dl>
      ) : null}
    </div>
  );
}
