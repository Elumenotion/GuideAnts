import { ProviderAddForm, ProviderEditForm } from './types';

function ProviderInfo() {
  return (
    <p className="text-sm text-gray-700">
      Uses settings from <span className="font-mono">HuggingFace</span> (token and router base URL).
    </p>
  );
}

export function HuggingFaceInferenceAddForm(_: ProviderAddForm) {
  return <ProviderInfo />;
}

export function HuggingFaceInferenceEditForm(_: ProviderEditForm) {
  return <ProviderInfo />;
}
