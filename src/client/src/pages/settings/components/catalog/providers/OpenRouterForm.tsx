import { ProviderAddForm, ProviderEditForm } from './types';

function ProviderInfo() {
  return (
    <p className="text-sm text-gray-700">
      Uses settings from <span className="font-mono">OpenRouter</span> (API key and optional base URL metadata).
    </p>
  );
}

export function OpenRouterAddForm(_: ProviderAddForm) {
  return <ProviderInfo />;
}

export function OpenRouterEditForm(_: ProviderEditForm) {
  return <ProviderInfo />;
}
