import { ProviderAddForm, ProviderEditForm } from './types';

function ProviderInfo() {
  return (
    <div className="space-y-2 text-sm text-gray-700">
      <p>
        Uses settings from <span className="font-mono">OpenRouter</span> (
        <span className="font-mono">ApiKey</span>, optional <span className="font-mono">BaseUrl</span>,{' '}
        <span className="font-mono">HttpReferer</span>, and <span className="font-mono">AppTitle</span>).
      </p>
      <p>
        Add a chat model id such as <span className="font-mono">minimax/minimax-m3</span>. OpenRouter image routing uses one model id for
        both generation and edit flows.
      </p>
    </div>
  );
}

export function OpenRouterAddForm(_: ProviderAddForm) {
  return <ProviderInfo />;
}

export function OpenRouterEditForm(_: ProviderEditForm) {
  return <ProviderInfo />;
}
