import { ProviderAddForm, ProviderEditForm } from './types';

function ProviderInfo() {
  return (
    <p className="text-sm text-gray-700">
      Uses settings from <span className="font-mono">GoogleGeminiApi</span> (API key auth).
    </p>
  );
}

export function GoogleGeminiAddForm(_: ProviderAddForm) {
  return <ProviderInfo />;
}

export function GoogleGeminiEditForm(_: ProviderEditForm) {
  return <ProviderInfo />;
}
