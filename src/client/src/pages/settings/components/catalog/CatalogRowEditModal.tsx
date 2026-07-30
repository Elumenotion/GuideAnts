import { useEffect, useRef, useState, type RefObject } from 'react';
import { api } from '../../../../services/api';
import { LlamaRuntimeInventoryItemDto, SettingsModelDto, SettingsRuntimeProfileDto } from '../../../../types/settings';
import { ActiveModelOperationState, CatalogEditState } from '../../types';
import {
  buildCatalogEditRequest,
  createCatalogEditStateFromModel,
  getErrorMessage,
  parseCanonicalLocalRuntimeJson,
} from '../../utils';
import { getCatalogProviderDisplayName } from '../../constants/displayLabels';
import { TextActionButton } from '../shared/ActionButtons';
import { SettingsModal } from '../shared/SettingsModal';
import { AnthropicEditForm } from './providers/AnthropicForm';
import { AzureOpenAiChatEditForm } from './providers/AzureOpenAiChatForm';
import { AzureOpenAiResponsesEditForm } from './providers/AzureOpenAiResponsesForm';
import { GoogleGeminiEditForm } from './providers/GoogleGeminiForm';
import { HuggingFaceInferenceEditForm } from './providers/HuggingFaceInferenceForm';
import { LlamaCppEditForm, type LlamaCppEditFormHandle } from './providers/LlamaCppForm';
import { OpenAiChatEditForm } from './providers/OpenAiChatForm';
import { OpenAiResponsesEditForm } from './providers/OpenAiResponsesForm';
import { OpenRouterEditForm } from './providers/OpenRouterForm';

interface CatalogRowEditModalProps {
  model: SettingsModelDto | null;
  orderedModels: SettingsModelDto[];
  profiles: SettingsRuntimeProfileDto[];
  profilesLoading: boolean;
  inventory?: LlamaRuntimeInventoryItemDto[];
  isOpen: boolean;
  onClose: () => void;
  onSaved: () => Promise<void>;
  onOperationStarted: (operation: ActiveModelOperationState) => void;
}

function renderEditForm(
  value: CatalogEditState,
  onChange: (updates: Partial<CatalogEditState>) => void,
  profiles: SettingsRuntimeProfileDto[],
  profilesLoading: boolean,
  inventory: LlamaRuntimeInventoryItemDto[] | undefined,
  onOperationStarted: (operation: ActiveModelOperationState) => void,
  onDetailChanged?: () => Promise<void>,
  llamaFormRef?: RefObject<LlamaCppEditFormHandle | null>,
) {
  const props = {
    value,
    onChange,
    profiles,
    profilesLoading,
    inventory,
  };
  switch (value.provider) {
    case 'openai-chat':
      return <OpenAiChatEditForm {...props} />;
    case 'openai-responses':
      return <OpenAiResponsesEditForm {...props} />;
    case 'azure-openai-chat':
      return <AzureOpenAiChatEditForm {...props} />;
    case 'azure-openai-responses':
      return <AzureOpenAiResponsesEditForm {...props} />;
    case 'anthropic':
      return <AnthropicEditForm {...props} />;
    case 'llama-cpp':
      return (
        <LlamaCppEditForm
          ref={llamaFormRef}
          {...props}
          onDetailChanged={onDetailChanged}
          onOperationStarted={onOperationStarted}
        />
      );
    case 'google-gemini-chat':
      return <GoogleGeminiEditForm {...props} />;
    case 'hf-inference-chat':
      return <HuggingFaceInferenceEditForm {...props} />;
    case 'openrouter-chat':
      return <OpenRouterEditForm {...props} />;
    default:
      return null;
  }
}

function buildLlamaRuntimeConfigJson(model: SettingsModelDto): string {
  const parsed = parseCanonicalLocalRuntimeJson(model.runtimeConfigJson);
  if (!parsed?.routerModelId) {
    throw new Error(`Model '${model.modelId}' is missing routerModelId in RuntimeConfigJson.`);
  }
  return JSON.stringify({ routerModelId: parsed.routerModelId });
}

export function CatalogRowEditModal({
  model,
  orderedModels,
  profiles,
  profilesLoading,
  inventory,
  isOpen,
  onClose,
  onSaved,
  onOperationStarted,
}: CatalogRowEditModalProps) {
  void orderedModels;
  const [value, setValue] = useState<CatalogEditState | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const llamaFormRef = useRef<LlamaCppEditFormHandle>(null);

  useEffect(() => {
    if (!isOpen || !model) {
      setValue(null);
      setError(null);
      setSaving(false);
      return;
    }
    setValue(createCatalogEditStateFromModel(model));
    setError(null);
    setSaving(false);
  }, [isOpen, model]);

  const submit = async () => {
    if (!value || !model) {
      return;
    }
    setSaving(true);
    setError(null);
    try {
      if (value.provider === 'llama-cpp') {
        await llamaFormRef.current?.saveRouterPreset();
      }
      const selectedProfile = profiles.find((profile) => profile.profileId === value.runtimeProfileId.trim());
      const request = buildCatalogEditRequest(value, {
        runtimeConfigJson:
          value.provider === 'llama-cpp' ? buildLlamaRuntimeConfigJson(model) : model.runtimeConfigJson ?? undefined,
        profileThinkingControlJson: value.provider === 'llama-cpp' ? undefined : selectedProfile?.thinkingControlJson,
        preserveModelBehavior:
          value.provider === 'llama-cpp'
            ? {
                combineSystemAndDeveloperMessages: model.combineSystemAndDeveloperMessages,
                thoughtBlockPattern: model.thoughtBlockPattern,
                samplingParametersJson: model.samplingParametersJson,
                thinkingControlJson: model.thinkingControlJson,
                requestFieldsWhenToolsPresentJson: model.requestFieldsWhenToolsPresentJson,
                reasoningChoicesJson: model.reasoningChoicesJson,
              }
            : undefined,
      });
      await api.settings.updateModel(model.modelId, request);
      await onSaved();
      onClose();
    } catch (submitError) {
      setError(getErrorMessage(submitError, 'Failed to save catalog row.'));
    } finally {
      setSaving(false);
    }
  };

  return (
    <SettingsModal
      isOpen={isOpen}
      title={model ? `Edit Catalog Row: ${model.modelId}` : 'Edit Catalog Row'}
      onClose={onClose}
      maxWidthClass="max-w-2xl"
      disableDismiss={saving}
      footer={
        <>
          <TextActionButton tone="neutral" onClick={onClose} disabled={saving} title="Cancel edit">
            Cancel
          </TextActionButton>
          <TextActionButton tone="primary" onClick={() => void submit()} disabled={saving} title="Save edit">
            Save
          </TextActionButton>
        </>
      }
    >
      {!value ? null : (
        <div className="space-y-4">
          <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
            <div className="space-y-2">
              <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Model ID</label>
              <input
                type="text"
                value={value.modelId}
                disabled
                className="w-full rounded border border-gray-300 bg-gray-100 px-3 py-2 font-mono text-sm text-gray-600"
              />
            </div>
            <div className="space-y-2">
              <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Provider</label>
              <input
                type="text"
                value={getCatalogProviderDisplayName(value.provider)}
                disabled
                className="w-full rounded border border-gray-300 bg-gray-100 px-3 py-2 font-mono text-sm text-gray-600"
              />
            </div>
            <div className="space-y-2">
              <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Display Name</label>
              <input
                type="text"
                value={value.displayName}
                onChange={(event) => setValue((previous) => (previous ? { ...previous, displayName: event.target.value } : previous))}
                className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </div>
            <div className="space-y-2">
              <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Display Order</label>
              <input
                type="number"
                value={value.displayOrder}
                onChange={(event) => setValue((previous) => (previous ? { ...previous, displayOrder: event.target.value } : previous))}
                className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </div>
            <div className="space-y-2 md:col-span-2">
              <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Description</label>
              <textarea
                value={value.description}
                onChange={(event) => setValue((previous) => (previous ? { ...previous, description: event.target.value } : previous))}
                rows={2}
                className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </div>
            <label className="inline-flex items-center gap-2 text-sm text-gray-700 md:col-span-2">
              <input
                type="checkbox"
                checked={value.isActive}
                onChange={(event) => setValue((previous) => (previous ? { ...previous, isActive: event.target.checked } : previous))}
                className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
              />
              Active
            </label>
          </div>

          <div className="border-t border-gray-200 pt-3">
            {renderEditForm(
              value,
              (updates) => setValue((previous) => (previous ? { ...previous, ...updates } : previous)),
              profiles,
              profilesLoading,
              inventory,
              onOperationStarted,
              onSaved,
              llamaFormRef,
            )}
          </div>

          {value.provider !== 'llama-cpp' ? (
            <div className="space-y-2">
              <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Runtime Profile</label>
              <select
                value={value.runtimeProfileId}
                onChange={(event) => setValue((previous) => (previous ? { ...previous, runtimeProfileId: event.target.value } : previous))}
                disabled={profilesLoading}
                className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              >
                <option value="">
                  {profilesLoading
                    ? 'Loading profiles...'
                    : profiles.filter((p) => p.providers.includes(value.provider)).length === 0
                    ? `No profiles defined for ${value.provider}`
                    : 'Select runtime profile'}
                </option>
                {profiles
                  .filter((p) => p.providers.includes(value.provider))
                  .map((profile) => (
                    <option key={profile.profileId} value={profile.profileId}>
                      {profile.displayName} ({profile.profileId})
                    </option>
                  ))}
              </select>
            </div>
          ) : null}

          {error ? <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{error}</div> : null}
        </div>
      )}
    </SettingsModal>
  );
}
