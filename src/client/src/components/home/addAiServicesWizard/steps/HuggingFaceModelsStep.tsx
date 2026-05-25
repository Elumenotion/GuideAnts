import { HUGGINGFACE_DEFAULT_CHAT_MODEL_ID } from '../constants';
import type { ExistingHuggingFaceModel, HuggingFaceModelDraft } from '../types';

interface HuggingFaceModelsStepProps {
  existingModels: ExistingHuggingFaceModel[];
  draftModels: HuggingFaceModelDraft[];
  draftModelId: string;
  setDraftAsGlobalDefault: boolean;
  lockDraftAsGlobalDefault: boolean;
  addError: string | null;
  validationError: string | null;
  onDraftModelIdChange: (next: string) => void;
  onSetDraftAsGlobalDefaultChange: (next: boolean) => void;
  onAddModel: () => void;
  onRemoveDraftModel: (localId: string) => void;
}

export function HuggingFaceModelsStep({
  existingModels,
  draftModels,
  draftModelId,
  setDraftAsGlobalDefault,
  lockDraftAsGlobalDefault,
  addError,
  validationError,
  onDraftModelIdChange,
  onSetDraftAsGlobalDefaultChange,
  onAddModel,
  onRemoveDraftModel,
}: HuggingFaceModelsStepProps) {
  return (
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Hugging Face chat models (required)</h3>
        <p className="mt-1 text-sm text-gray-600">
          Add one or more Hugging Face chat model ids. This path uses provider <span className="font-mono">hf-inference-chat</span>.
        </p>
      </div>

      <div className="grid grid-cols-1 gap-3 rounded border border-gray-200 bg-gray-50 p-3 md:grid-cols-[minmax(0,1fr)_auto]">
        <div className="space-y-1">
          <label className="block text-xs font-semibold uppercase tracking-wide text-gray-600" htmlFor="wizard-hf-model-id">
            Model
          </label>
          <input
            id="wizard-hf-model-id"
            value={draftModelId}
            onChange={(event) => onDraftModelIdChange(event.target.value)}
            placeholder={HUGGINGFACE_DEFAULT_CHAT_MODEL_ID}
            className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
          />
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
        <div className="md:col-span-2">
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
        <p className="text-xs font-semibold uppercase tracking-wide text-gray-600">Configured Hugging Face models</p>
        {existingModels.length === 0 && draftModels.length === 0 ? (
          <p className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900">
            Add at least one Hugging Face model to continue.
          </p>
        ) : (
          <div className="space-y-2">
            {existingModels.map((model) => (
              <div key={`existing:${model.modelId}`} className="flex items-center justify-between rounded border border-gray-200 px-3 py-2">
                <div className="flex items-center gap-2">
                  <span className="font-mono text-sm text-gray-900">{model.modelId}</span>
                  <span className="rounded-full border border-gray-300 bg-gray-50 px-2 py-0.5 text-xs text-gray-700">
                    hf-inference-chat
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
                    hf-inference-chat
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
