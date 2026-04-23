import { GuideCrewManager } from '../guideEditor/GuideCrewManager';

interface CrewTabProps {
  selectedAssistantIds: string[];
  onChange: (ids: string[]) => void;
}

export function CrewTab({ selectedAssistantIds, onChange }: CrewTabProps) {
  return <GuideCrewManager selectedAssistantIds={selectedAssistantIds} onChange={onChange} />;
}

