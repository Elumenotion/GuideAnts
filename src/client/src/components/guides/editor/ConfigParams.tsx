import { ModelDto } from '../../../types/guides';
import { getReasoningChoicesForModel } from '../../chat-model/reasoning';

interface ConfigParamsProps {
  model?: ModelDto;
  temperature?: number | null;
  topP?: number | null;
  reasoningEffort?: string;
  samplingOverrides?: Record<string, number>;
  onTemperatureChange: (value: number) => void;
  onTopPChange: (value: number) => void;
  onReasoningEffortChange: (value: string) => void;
  onSamplingParameterChange?: (key: string, value: number) => void;
}

export function ConfigParams({
  model,
  temperature = 1.0,
  topP = 1.0,
  reasoningEffort,
  samplingOverrides,
  onTemperatureChange,
  onTopPChange,
  onReasoningEffortChange,
  onSamplingParameterChange,
}: ConfigParamsProps) {
  const samplingPolicy = model?.samplingParameterPolicy ?? [];
  const reasoningChoices = getReasoningChoicesForModel(model);
  const defaultReasoningChoice = model?.defaultReasoningChoice;

  const hasReasoningChoices = reasoningChoices.length > 0;
  const selectedReasoningEffort = reasoningEffort && reasoningChoices.includes(reasoningEffort)
    ? reasoningEffort
    : defaultReasoningChoice ?? reasoningChoices[0] ?? '';
  const reasoningChoiceLocked = reasoningChoices.length <= 1;

  if (samplingPolicy.length === 0 && !hasReasoningChoices) {
    return null;
  }

  const getParamValue = (key: string): number => {
    const policyDefault = samplingPolicy.find(p => p.key === key)?.recommendedDefault ?? 0;
    if (key === 'temperature') return temperature ?? policyDefault;
    if (key === 'top_p') return topP ?? policyDefault;
    if (samplingOverrides && key in samplingOverrides) return samplingOverrides[key];
    return policyDefault;
  };

  const handleParamChange = (key: string, value: number) => {
    if (key === 'temperature') onTemperatureChange(value);
    else if (key === 'top_p') onTopPChange(value);
    else onSamplingParameterChange?.(key, value);
  };

  const formatValue = (value: number, step: number): string => {
    if (step >= 1) return value.toFixed(0);
    if (step >= 0.1) return value.toFixed(1);
    return value.toFixed(2);
  };

  const formatReasoningChoice = (choice: string): string =>
    choice.length > 0 ? choice.charAt(0).toUpperCase() + choice.slice(1) : choice;

  return (
    <div className="space-y-4">
      <h2 className="text-lg font-semibold text-gray-900">Configuration Parameters</h2>

      <div className="space-y-4">
        {samplingPolicy.length > 0 ? (
          samplingPolicy.map((param) => (
            <div key={param.key}>
              <div className="flex justify-between items-center mb-2">
                <label htmlFor={`param-${param.key}`} className="text-sm font-medium text-gray-700">
                  {param.label}
                </label>
                <span className="text-sm text-gray-600">
                  {formatValue(getParamValue(param.key), param.step)}
                </span>
              </div>
              <input
                id={`param-${param.key}`}
                type="range"
                min={param.min}
                max={param.max}
                step={param.step}
                value={getParamValue(param.key)}
                onChange={(e) => handleParamChange(param.key, parseFloat(e.target.value))}
                className="w-full h-2 bg-gray-200 rounded-lg appearance-none cursor-pointer slider"
                data-tour-id={`guide.config.${param.key}.input`}
              />
              <p className="mt-1 text-xs text-gray-500">
                {param.description}
              </p>
            </div>
          ))
        ) : null}

        {hasReasoningChoices && (
          <div>
            <label htmlFor="reasoning-effort" className="block text-sm font-medium text-gray-700 mb-1">
              Reasoning Effort
            </label>
            <select
              id="reasoning-effort"
              value={selectedReasoningEffort}
              onChange={(e) => onReasoningEffortChange(e.target.value)}
              disabled={reasoningChoiceLocked}
              className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-gray-100 disabled:cursor-not-allowed"
              data-tour-id="guide.config.reasoningEffort.select"
            >
              {reasoningChoices.map((choice) => (
                <option key={choice} value={choice}>
                  {formatReasoningChoice(choice)}
                </option>
              ))}
            </select>
            <p className="mt-1 text-xs text-gray-500">
              Select a model-supported reasoning mode.
            </p>
          </div>
        )}
      </div>
    </div>
  );
}
