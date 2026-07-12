import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api } from '../../../../services/api';
import { formatLimitDisplay } from '../toolLimits/toolLimitDisplay';

interface CrewMemberLimitsRowProps {
  projectId: string;
  assistantId: string;
  assistantName: string;
  initialMaxToolCallsPerTurn?: number | null;
}

export function CrewMemberLimitsRow({
  projectId,
  assistantId,
  assistantName,
  initialMaxToolCallsPerTurn,
}: CrewMemberLimitsRowProps) {
  const navigate = useNavigate();
  const [loading, setLoading] = useState(initialMaxToolCallsPerTurn === undefined);
  const [maxToolCallsPerTurn, setMaxToolCallsPerTurn] = useState<number | null | undefined>(
    initialMaxToolCallsPerTurn,
  );

  useEffect(() => {
    if (initialMaxToolCallsPerTurn !== undefined) {
      setMaxToolCallsPerTurn(initialMaxToolCallsPerTurn);
      setLoading(false);
      return;
    }

    let cancelled = false;
    setLoading(true);

    const loadLimits = async () => {
      try {
        const details = await api.guides.assistants.get(assistantId, projectId);
        if (!cancelled) {
          setMaxToolCallsPerTurn(details.maxToolCallsPerTurn ?? null);
        }
      } catch (error) {
        if (!cancelled) {
          console.error('Failed to load crew member tool limits:', error);
          setMaxToolCallsPerTurn(null);
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    };

    void loadLimits();

    return () => {
      cancelled = true;
    };
  }, [assistantId, projectId, initialMaxToolCallsPerTurn]);

  const handleEditLimits = () => {
    navigate(`/projects/${projectId}/guides/assistant/${assistantId}?tab=tools&toolsSubTab=global`);
  };

  return (
    <div className="mt-2 flex flex-col gap-1 sm:flex-row sm:items-center sm:justify-between">
      <p className="text-xs text-gray-600">
        Tool calls per turn:{' '}
        {loading ? (
          <span className="text-gray-500">Loading…</span>
        ) : (
          <span className="font-medium text-gray-800">{formatLimitDisplay(maxToolCallsPerTurn)}</span>
        )}
      </p>
      <button
        type="button"
        onClick={handleEditLimits}
        className="text-xs text-blue-600 hover:text-blue-800 self-start sm:self-auto focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        aria-label={`Edit tool limits for ${assistantName}`}
      >
        Edit limits
      </button>
    </div>
  );
}
