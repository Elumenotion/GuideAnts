import type { AssistantSkillDto, FileDto } from '../../../../types/guides';
import { sortSkillsByDisplayOrder } from './skillOrdering';
import { SkillCard } from './SkillCard';

interface SkillListProps {
  skills: AssistantSkillDto[];
  availableToolTypes: Set<string>;
  hasCodeInterpreterFiles: boolean;
  onSkillChange: (skillName: string, skill: AssistantSkillDto) => void;
  onSkillRemove: (skillName: string) => void;
  onMoveSkill: (skillName: string, direction: 'up' | 'down') => void;
  onPreviewFile: (skillName: string, file: FileDto) => void;
  onDownloadFile?: (fileId: string, fileName: string) => void;
  onCreateAssistantFromSkill?: (skillName: string) => void;
}

export function SkillList({
  skills,
  availableToolTypes,
  hasCodeInterpreterFiles,
  onSkillChange,
  onSkillRemove,
  onMoveSkill,
  onPreviewFile,
  onDownloadFile,
  onCreateAssistantFromSkill,
}: SkillListProps) {
  const sortedSkills = sortSkillsByDisplayOrder(skills);

  if (sortedSkills.length === 0) {
    return null;
  }

  return (
    <div className="space-y-4">
      {sortedSkills.map((skill, index) => (
        <SkillCard
          key={skill.name}
          skill={skill}
          availableToolTypes={availableToolTypes}
          hasCodeInterpreterFiles={hasCodeInterpreterFiles}
          canMoveUp={index > 0}
          canMoveDown={index < sortedSkills.length - 1}
          showOrdering={sortedSkills.length > 1}
          discoveryOrder={index + 1}
          onEnabledChange={(enabled) => onSkillChange(skill.name, { ...skill, enabled })}
          onMoveUp={() => onMoveSkill(skill.name, 'up')}
          onMoveDown={() => onMoveSkill(skill.name, 'down')}
          onRemove={() => onSkillRemove(skill.name)}
          onPreviewFile={(file) => onPreviewFile(skill.name, file)}
          onDownloadFile={onDownloadFile}
          onCreateAssistant={() => onCreateAssistantFromSkill?.(skill.name)}
        />
      ))}
    </div>
  );
}
