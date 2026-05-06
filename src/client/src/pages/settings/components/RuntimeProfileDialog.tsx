import { FaSave, FaSpinner } from 'react-icons/fa';
import { ProfileFormState } from '../types';
import { HIDDEN_CHAT_MODEL_PROVIDERS } from '../constants/connectionSections';
import { RuntimeProfileEditor } from './RuntimeProfileEditor';
import { TextActionButton } from './shared/ActionButtons';
import { SettingsModal } from './shared/SettingsModal';

const ALL_PROVIDERS: { id: string; label: string }[] = [
  { id: 'openai-chat', label: 'openai-chat' },
  { id: 'azure-openai-chat', label: 'azure-openai-chat' },
  { id: 'openai-responses', label: 'openai-responses' },
  { id: 'azure-openai-responses', label: 'azure-openai-responses' },
  { id: 'anthropic', label: 'anthropic' },
  { id: 'google-gemini-chat', label: 'google-gemini-chat' },
  { id: 'hf-inference-chat', label: 'hf-inference-chat' },
  { id: 'openrouter-chat', label: 'openrouter-chat' },
  { id: 'llama-cpp', label: 'llama-cpp' },
];

const VISIBLE_PROVIDERS = ALL_PROVIDERS.filter(({ id }) => !HIDDEN_CHAT_MODEL_PROVIDERS.has(id));

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

  function toggleProvider(providerId: string, checked: boolean) {
    const next = checked
      ? [...value.providers, providerId]
      : value.providers.filter((p) => p !== providerId);
    onChange('providers', next);
  }

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
          Runtime profiles define sampling parameters and thinking control for specific model providers.
          Selecting <span className="font-mono text-xs">llama-cpp</span> also enables thinking control
          and message normalization fields.
        </p>

        <div className="space-y-2">
          <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Providers</label>
          <div className="flex flex-wrap gap-x-4 gap-y-1">
            {VISIBLE_PROVIDERS.map(({ id, label }) => (
              <label key={id} className="inline-flex items-center gap-1.5 text-sm text-gray-700 select-none">
                <input
                  type="checkbox"
                  checked={value.providers.includes(id)}
                  onChange={(e) => toggleProvider(id, e.target.checked)}
                  className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                />
                <span className="font-mono text-xs">{label}</span>
              </label>
            ))}
          </div>
        </div>

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
