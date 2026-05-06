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
          Runtime profiles define sampling parameters for models. Cloud profiles expose Temperature and Top P sliders
          in guide and assistant builders. Local (llama-cpp) profiles also configure thinking control and message normalization.
        </p>
        {!editing ? (
          <div className="space-y-2">
            <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Profile Kind</label>
            <select
              value={value.kind}
              onChange={(event) => onChange('kind', event.target.value as 'local' | 'cloud')}
              className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            >
              <option value="cloud">cloud — for OpenAI, Anthropic, Gemini, etc.</option>
              <option value="local">local — for llama-cpp models</option>
            </select>
          </div>
        ) : (
          <div className="rounded border border-gray-200 bg-gray-50 px-3 py-2 text-xs text-gray-700">
            Kind: <span className="font-mono font-medium">{value.kind}</span>
          </div>
        )}
        <RuntimeProfileEditor
          mode="full"
          value={value}
          onChange={onChange}
          onInsertTemplate={value.kind !== 'cloud' ? onInsertTemplate : undefined}
          disableIdentityFields={editing}
        />
      </div>
    </SettingsModal>
  );
}
