import type { ExistingFoundryModel, FoundryModelDraft, FoundryModelProviderLabel } from '../types';

interface ModelsStepProps {
  existingModels: ExistingFoundryModel[];
  draftModels: FoundryModelDraft[];
  draftModelId: string;
  draftProvider: FoundryModelProviderLabel;
  addError: string | null;
  validationError: string | null;
  onDraftModelIdChange: (next: string) => void;
  onDraftProviderChange: (next: FoundryModelProviderLabel) => void;
  onAddModel: () => void;
  onRemoveDraftModel: (localId: string) => void;
}

function ProviderPill({ value }: { value: FoundryModelProviderLabel }) {
  return (
    <span className="rounded-full border border-gray-300 bg-gray-50 px-2 py-0.5 text-xs text-gray-700">
      {value}
    </span>
  );
}

export function ModelsStep({
  existingModels,
  draftModels,
  draftModelId,
  draftProvider,
  addError,
  validationError,
  onDraftModelIdChange,
  onDraftProviderChange,
  onAddModel,
  onRemoveDraftModel,
}: ModelsStepProps) {
  return (
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Models (required)</h3>
        <p className="mt-1 text-sm text-gray-600">
          Add one or more deployed models. Each model has its own provider type: Completions or Responses.
        </p>
      </div>

      <div className="grid grid-cols-1 gap-3 rounded border border-gray-200 bg-gray-50 p-3 md:grid-cols-[minmax(0,1fr)_180px_auto]">
        <div className="space-y-1">
          <label className="block text-xs font-semibold uppercase tracking-wide text-gray-600" htmlFor="wizard-model-id">
            Model
          </label>
          <input
            id="wizard-model-id"
            value={draftModelId}
            onChange={(event) => onDraftModelIdChange(event.target.value)}
            placeholder="gpt-4o"
            className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
          />
        </div>
        <div className="space-y-1">
          <label className="block text-xs font-semibold uppercase tracking-wide text-gray-600" htmlFor="wizard-model-provider">
            Provider
          </label>
          <select
            id="wizard-model-provider"
            value={draftProvider}
            onChange={(event) => onDraftProviderChange(event.target.value as FoundryModelProviderLabel)}
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
      </div>

      {addError ? <p className="text-xs text-red-700">{addError}</p> : null}

      <div className="space-y-2">
        <p className="text-xs font-semibold uppercase tracking-wide text-gray-600">Configured models</p>
        {existingModels.length === 0 && draftModels.length === 0 ? (
          <p className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900">
            Add at least one model to continue.
          </p>
        ) : (
          <div className="space-y-2">
            {existingModels.map((model) => (
              <div key={`existing:${model.modelId}:${model.provider}`} className="flex items-center justify-between rounded border border-gray-200 px-3 py-2">
                <div className="flex items-center gap-2">
                  <span className="font-mono text-sm text-gray-900">{model.modelId}</span>
                  <ProviderPill value={model.provider} />
                </div>
                <span className="text-xs text-gray-500">Already configured</span>
              </div>
            ))}
            {draftModels.map((model) => (
              <div key={model.localId} className="flex items-center justify-between rounded border border-blue-200 bg-blue-50 px-3 py-2">
                <div className="flex items-center gap-2">
                  <span className="font-mono text-sm text-gray-900">{model.modelId}</span>
                  <ProviderPill value={model.provider} />
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

