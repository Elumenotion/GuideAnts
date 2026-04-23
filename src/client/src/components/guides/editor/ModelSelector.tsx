import { ModelDto } from '../../../types/guides';

interface ModelSelectorProps {
  models: ModelDto[];
  loading: boolean;
  selectedModelId?: string;
  /** `entity`: Guide/assistant — first option uses global default (empty id). `default`: Settings default model only. */
  selectionMode?: 'entity' | 'default';
  onChange: (modelId?: string, model?: ModelDto) => void;
}

export function ModelSelector({
  models,
  loading,
  selectedModelId,
  selectionMode = 'entity',
  onChange,
}: ModelSelectorProps) {
  const activeModels = models.filter((m) => m.isActive);
  const inactiveModels = models.filter((m) => !m.isActive);

  return (
    <div className="space-y-4">
      <h2 className="text-lg font-semibold text-gray-900">Model Selection</h2>

      <div>
        <label htmlFor="model-select" className="block text-sm font-medium text-gray-700 mb-1">
          AI Model
        </label>
        {loading ? (
          <div className="text-sm text-gray-500">Loading models...</div>
        ) : (
          <select
            id="model-select"
            value={selectedModelId || ''}
            onChange={(e) => {
              const modelId = e.target.value || undefined;
              const model = modelId ? models.find((m) => m.modelId === modelId) : undefined;
              onChange(modelId, model);
            }}
            className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            data-tour-id="guide.config.model.select"
          >
            {selectionMode === 'entity' ? (
              <option value="">Use Default Model (from Settings)</option>
            ) : (
              <option value="">Select a catalog model…</option>
            )}

            {activeModels.length > 0 && (
              <optgroup label="Active Models">
                {activeModels.map((model) => (
                  <option key={model.modelId} value={model.modelId}>
                    {model.displayName}
                    {model.description ? ` - ${model.description}` : ''}
                  </option>
                ))}
              </optgroup>
            )}

            {inactiveModels.length > 0 && (
              <optgroup label="Inactive Models">
                {inactiveModels.map((model) => (
                  <option key={model.modelId} value={model.modelId}>
                    {model.displayName}
                    {model.description ? ` - ${model.description}` : ''}
                  </option>
                ))}
              </optgroup>
            )}
          </select>
        )}
        <p className="mt-1 text-xs text-gray-500">
          {selectionMode === 'entity'
            ? 'The AI model for this guide or assistant. Choose “Use Default” to follow Settings → Overview → Default Chat Model.'
            : 'Catalog model id used as the instance-wide default for chat when entities are set to use the default.'}
        </p>
      </div>
    </div>
  );
}
