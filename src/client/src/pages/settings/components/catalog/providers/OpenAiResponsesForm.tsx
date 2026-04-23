import { ProviderAddForm, ProviderEditForm } from './types';

export function OpenAiResponsesAddForm(props: ProviderAddForm) {
  return (
    <label className="inline-flex items-center gap-2 text-sm text-gray-700">
      <input
        type="checkbox"
        checked={props.value.openAiReasoningEffortEnabled}
        onChange={(event) => props.onChange({ openAiReasoningEffortEnabled: event.target.checked })}
        className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
      />
      Enable reasoning effort choices
    </label>
  );
}

export function OpenAiResponsesEditForm(props: ProviderEditForm) {
  return (
    <label className="inline-flex items-center gap-2 text-sm text-gray-700">
      <input
        type="checkbox"
        checked={props.value.openAiReasoningEffortEnabled}
        onChange={(event) => props.onChange({ openAiReasoningEffortEnabled: event.target.checked })}
        className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
      />
      Enable reasoning effort choices
    </label>
  );
}
