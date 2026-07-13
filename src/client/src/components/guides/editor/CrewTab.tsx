import { GuideCrewManager } from '../guideEditor/GuideCrewManager';

interface CrewTabProps {
  projectId: string;
  selectedAssistantIds: string[];
  crewMemberLimitById: Record<string, number | null | undefined>;
  crewMemberInvocationLimits: Record<string, number | undefined>;
  onChange: (ids: string[]) => void;
  onInvocationLimitChange: (assistantId: string, value: number | undefined) => void;
  onDirtyChange?: () => void;
}

export function CrewTab({
  projectId,
  selectedAssistantIds,
  crewMemberLimitById,
  crewMemberInvocationLimits,
  onChange,
  onInvocationLimitChange,
  onDirtyChange,
}: CrewTabProps) {
  return (
    <GuideCrewManager
      projectId={projectId}
      selectedAssistantIds={selectedAssistantIds}
      crewMemberLimitById={crewMemberLimitById}
      crewMemberInvocationLimits={crewMemberInvocationLimits}
      onChange={onChange}
      onInvocationLimitChange={onInvocationLimitChange}
      onDirtyChange={onDirtyChange}
    />
  );
}

