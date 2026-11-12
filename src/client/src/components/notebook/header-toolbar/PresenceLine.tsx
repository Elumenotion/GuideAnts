import type { EndpointPresence } from '../../../types/notebookToolbar';

/**
 * Plane C presence line (F-2): shown, never awaited, never a spinner. An Unknown
 * (never observed) endpoint renders "not yet observed · Refresh" — a status, not an
 * error; Reachable/Unreachable render with their observation timestamp.
 */
export function PresenceLine({ presence }: { presence: EndpointPresence | null | undefined }) {
  if (!presence) return null;

  const observed = presence.observedUtc
    ? ` · observed ${new Date(presence.observedUtc).toLocaleTimeString()}`
    : '';
  if (presence.availability === 'Unknown') {
    return (
      <div className="text-xs text-slate-500" data-testid="presence-line">
        not yet observed · Refresh
      </div>
    );
  }
  if (presence.availability === 'Unreachable') {
    return (
      <div className="text-xs text-amber-700" data-testid="presence-line">
        endpoint unreachable{observed}
        {presence.error ? ` — ${presence.error}` : ''}
      </div>
    );
  }
  return (
    <div className="text-xs text-slate-500" data-testid="presence-line">
      endpoint reachable{observed}
    </div>
  );
}
