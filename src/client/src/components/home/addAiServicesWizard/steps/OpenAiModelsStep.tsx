import type { ExistingOpenAiModel, OpenAiModelDraft, OpenAiModelProviderLabel } from '../types';

interface OpenAiModelsStepProps {
  existingModels: ExistingOpenAiModel[];
  draftModels: OpenAiModelDraft[];
  draftModelId: string;
  draftProvider: OpenAiModelProviderLabel;
  setDraftAsGlobalDefault: boolean;
  lockDraftAsGlobalDefault: boolean;
  addError: string | null;
  validationError: string | null;
  onDraftModelIdChange: (next: string) => void;
  onDraftProviderChange: (next: OpenAiModelProviderLabel) => void;
  onSetDraftAsGlobalDefaultChange: (next: boolean) => void;
  onAddModel: () => void;
  onRemoveDraftModel: (localId: string) => void;
}

export function OpenAiModelsStep({
  existingModels,
  draftModels,
  draftModelId,
  draftProvider,
  setDraftAsGlobalDefault,
  lockDraftAsGlobalDefault,
  addError,
  validationError,
  onDraftModelIdChange,
  onDraftProviderChange,
  onSetDraftAsGlobalDefaultChange,
  onAddModel,
  onRemoveDraftModel,
}: OpenAiModelsStepProps) {
  return (
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">OpenAI models (required)</h3>
        <p className="mt-1 text-sm text-gray-600">
          Add one or more OpenAI chat models. Choose Completions (<span className="font-mono">openai-chat</span>)
          or Responses (<span className="font-mono">openai-responses</span>) per model.
        </p>
      </div>

      <div className="grid grid-cols-1 gap-3 rounded border border-gray-200 bg-gray-50 p-3 md:grid-cols-[minmax(0,1fr)_auto_auto]">
        <div className="space-y-1">
          <label className="block text-xs font-semibold uppercase tracking-wide text-gray-600" htmlFor="wizard-openai-model-id">
            Model
          </label>
          <input
            id="wizard-openai-model-id"
            value={draftModelId}
            onChange={(event) => onDraftModelIdChange(event.target.value)}
            placeholder="gpt-4.1-nano"
            className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
          />
        </div>
        <div className="space-y-1">
          <label className="block text-xs font-semibold uppercase tracking-wide text-gray-600" htmlFor="wizard-openai-model-provider">
            Provider
          </label>
          <select
            id="wizard-openai-model-provider"
            value={draftProvider}
            onChange={(event) => onDraftProviderChange(event.target.value as OpenAiModelProviderLabel)}
            className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
          >
            <option value="Completions">Completions</option>
            <option value="Responses">Responses</option>
          </select>
        </div>
        <div className="flex items-end">
          <button
            type="button"
            onClick={onAddModel}
            className="w-full rounded bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700"
          >
            Add model
          </button>
        </div>
        <div className="md:col-span-3">
          <label className="inline-flex items-center gap-2 text-xs text-gray-700">
            <input
              type="checkbox"
              className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
              checked={setDraftAsGlobalDefault}
              disabled={lockDraftAsGlobalDefault}
              onChange={(event) => onSetDraftAsGlobalDefaultChange(event.target.checked)}
            />
            Set this model as the global default chat model
          </label>
          {lockDraftAsGlobalDefault ? (
            <p className="mt-1 text-xs text-gray-500">
              The first configured model is always set as the global default.
            </p>
          ) : null}
        </div>
      </div>

      {addError ? <p className="text-xs text-red-700">{addError}</p> : null}

      <div className="space-y-2">
        <p className="text-xs font-semibold uppercase tracking-wide text-gray-600">Configured OpenAI models</p>
        {existingModels.length === 0 && draftModels.length === 0 ? (
          <p className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900">
            Add at least one OpenAI model to continue.
          </p>
        ) : (
          <div className="space-y-2">
            {existingModels.map((model) => (
              <div key={`existing:${model.modelId}:${model.provider}`} className="flex items-center justify-between rounded border border-gray-200 px-3 py-2">
                <div className="flex items-center gap-2">
                  <span className="font-mono text-sm text-gray-900">{model.modelId}</span>
                  <span className="rounded-full border border-gray-300 bg-gray-50 px-2 py-0.5 text-xs text-gray-700">
                    {model.provider}
                  </span>
                </div>
                <span className="text-xs text-gray-500">Already configured</span>
              </div>
            ))}
            {draftModels.map((model) => (
              <div key={model.localId} className="flex items-center justify-between rounded border border-blue-200 bg-blue-50 px-3 py-2">
                <div className="flex items-center gap-2">
                  <span className="font-mono text-sm text-gray-900">{model.modelId}</span>
                  <span className="rounded-full border border-gray-300 bg-gray-50 px-2 py-0.5 text-xs text-gray-700">
                    {model.provider}
                  </span>
                  {model.setAsGlobalDefault ? (
                    <span className="rounded-full border border-emerald-300 bg-emerald-100 px-2 py-0.5 text-xs text-emerald-800">
                      Global default
                    </span>
                  ) : null}
                </div>
                <button
                  type="button"
                  onClick={() => onRemoveDraftModel(model.localId)}
                  className="text-xs font-medium text-blue-700 hover:text-blue-900"
                >
                  Remove
                </button>
              </div>
            ))}
          </div>
        )}
        {validationError ? <p className="text-xs text-red-700">{validationError}</p> : null}
      </div>
    </div>
  );
}
