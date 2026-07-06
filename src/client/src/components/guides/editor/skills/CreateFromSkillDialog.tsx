import { useEffect, useMemo, useState } from 'react';
import { ConfirmationDialog } from '../../../common/ConfirmationDialog';
import type { CreateFromSkillSelection } from './createFromSkillHelpers';
import { mapSkillPrerequisites } from './skillToolsetMapping';

interface CreateFromSkillSkillOption {
  name: string;
  description: string;
  requiresToolsets: string[];
  requiresTools: string[];
}

interface CreateFromSkillDialogProps {
  isOpen: boolean;
  initialPrimarySkillName?: string;
  skills: CreateFromSkillSkillOption[];
  isConfirming?: boolean;
  onConfirm: (selection: CreateFromSkillSelection) => void;
  onCancel: () => void;
}

export function CreateFromSkillDialog({
  isOpen,
  initialPrimarySkillName,
  skills,
  isConfirming = false,
  onConfirm,
  onCancel,
}: CreateFromSkillDialogProps) {
  const [selectedNames, setSelectedNames] = useState<string[]>([]);
  const [primarySkillName, setPrimarySkillName] = useState('');

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    if (initialPrimarySkillName && skills.some((skill) => skill.name === initialPrimarySkillName)) {
      setSelectedNames([initialPrimarySkillName]);
      setPrimarySkillName(initialPrimarySkillName);
      return;
    }

    setSelectedNames(skills.map((skill) => skill.name));
    setPrimarySkillName(skills[0]?.name ?? '');
  }, [isOpen, initialPrimarySkillName, skills]);

  const mapping = useMemo(() => {
    const selected = skills.filter((skill) => selectedNames.includes(skill.name));
    const toolsets = selected.flatMap((skill) => skill.requiresToolsets);
    const tools = selected.flatMap((skill) => skill.requiresTools);
    return mapSkillPrerequisites(toolsets, tools);
  }, [skills, selectedNames]);

  if (!isOpen) {
    return null;
  }

  const toggleSkill = (skillName: string) => {
    setSelectedNames((current) => {
      if (current.includes(skillName)) {
        const next = current.filter((name) => name !== skillName);
        if (primarySkillName === skillName) {
          setPrimarySkillName(next[0] ?? '');
        }
        return next;
      }

      return [...current, skillName];
    });
  };

  const message = [
    'Choose which skills to attach to the new assistant and which skill seeds its name, description, and instructions from SKILL.md.',
    '',
    'Confirming will create the assistant immediately with copied skill files and mapped tools.',
    'The following capabilities will be added based on explicit prerequisite mapping:',
    ...mapping.mappings.map(
      (item) => `• ${item.requirement} → ${item.mappedCapability}: ${item.reason}`,
    ),
    mapping.needsCodeInterpreter
      ? '• A Code Interpreter placeholder file will be added for sandbox/terminal prerequisites.'
      : '',
  ]
    .filter(Boolean)
    .join('\n');

  const body = (
    <div className="mt-4 space-y-3 text-left">
      {skills.map((skill) => {
        const isSelected = selectedNames.includes(skill.name);
        return (
          <label
            key={skill.name}
            className="flex items-start gap-3 rounded-md border border-gray-200 p-3"
          >
            <input
              type="checkbox"
              className="mt-1"
              checked={isSelected}
              disabled={isConfirming}
              onChange={() => toggleSkill(skill.name)}
            />
            <span className="min-w-0 flex-1">
              <span className="block text-sm font-medium text-gray-900">{skill.name}</span>
              <span className="block text-xs text-gray-600">{skill.description}</span>
              <label className="mt-2 flex items-center gap-2 text-xs text-gray-700">
                <input
                  type="radio"
                  name="primary-skill"
                  checked={primarySkillName === skill.name}
                  disabled={!isSelected || isConfirming}
                  onChange={() => setPrimarySkillName(skill.name)}
                />
                Use as primary skill for assistant name, description, and instructions
              </label>
            </span>
          </label>
        );
      })}
    </div>
  );

  return (
    <ConfirmationDialog
      isOpen={isOpen}
      title="Create assistant from skill(s)"
      message={message}
      body={body}
      confirmText="Create assistant"
      cancelText="Cancel"
      confirmButtonClass="bg-blue-600 hover:bg-blue-700 text-white"
      isLoading={isConfirming}
      confirmDisabled={selectedNames.length === 0 || !primarySkillName}
      onConfirm={() => onConfirm({
        primarySkillName,
        selectedSkillNames: selectedNames,
      })}
      onClose={onCancel}
    />
  );
}
