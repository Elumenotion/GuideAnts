import { ConfirmationDialog } from '../../../common/ConfirmationDialog';
import type { CreateFromSkillMappingResult } from './skillToolsetMapping';

interface CreateFromSkillDialogProps {
  isOpen: boolean;
  mapping: CreateFromSkillMappingResult | null;
  skillNames: string[];
  onConfirm: () => void;
  onCancel: () => void;
}

export function CreateFromSkillDialog({
  isOpen,
  mapping,
  skillNames,
  onConfirm,
  onCancel,
}: CreateFromSkillDialogProps) {
  if (!isOpen || !mapping) {
    return null;
  }

  const message = [
    `Create a new assistant from ${skillNames.length} skill(s): ${skillNames.join(', ')}.`,
    '',
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

  return (
    <ConfirmationDialog
      isOpen={isOpen}
      title="Create assistant from skill(s)"
      message={message}
      confirmText="Create assistant"
      cancelText="Cancel"
      confirmButtonClass="bg-blue-600 hover:bg-blue-700 text-white"
      onConfirm={onConfirm}
      onClose={onCancel}
    />
  );
}
