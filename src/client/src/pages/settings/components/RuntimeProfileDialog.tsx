import { FaSave, FaSpinner } from 'react-icons/fa';
import { ProfileFormState } from '../types';
import { RuntimeProfileEditor } from './RuntimeProfileEditor';
import { TextActionButton } from './shared/ActionButtons';
import { SettingsModal } from './shared/SettingsModal';

interface RuntimeProfileDialogProps {
  isOpen: boolean;
  editingProfileId: string | null;
  value: ProfileFormState;
  submitting: boolean;
  onChange: <K extends keyof ProfileFormState>(key: K, value: ProfileFormState[K]) => void;
  onInsertTemplate: (template: 'qwen3_5' | 'qwen3_6' | 'gemma4') => void;
  onClose: () => void;
  onSubmit: () => void;
}

export function RuntimeProfileDialog({
  isOpen,
  editingProfileId,
  value,
  submitting,
  onChange,
  onInsertTemplate,
  onClose,
  onSubmit,
}: RuntimeProfileDialogProps) {
  const editing = editingProfileId !== null;

  return (
    <SettingsModal
      isOpen={isOpen}
      title={editing ? `Edit Runtime Profile: ${editingProfileId}` : 'Add Runtime Profile'}
      onClose={onClose}
      maxWidthClass="max-w-4xl"
      disableDismiss={submitting}
      footer={
        <>
          <TextActionButton tone="neutral" onClick={onClose} disabled={submitting} title="Cancel runtime profile changes">
            Cancel
          </TextActionButton>
          <TextActionButton
            tone="primary"
            icon={submitting ? <FaSpinner className="animate-spin" /> : <FaSave />}
            disabled={submitting}
            onClick={onSubmit}
            title={editing ? 'Save runtime profile changes' : 'Create runtime profile'}
          >
            {editing ? 'Save changes' : 'Create profile'}
          </TextActionButton>
        </>
      }
    >
      <div className="space-y-4">
        <p className="text-sm text-gray-600">
          Runtime profiles define sampling parameters and behavior for llama-cpp models.
        </p>
        <RuntimeProfileEditor
          mode="full"
          value={value}
          onChange={onChange}
          onInsertTemplate={onInsertTemplate}
          disableIdentityFields={editing}
        />
      </div>
    </SettingsModal>
  );
}
