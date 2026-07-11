import { api } from '../../../services/api';
import { TextActionButton } from '../../../pages/settings/components/shared/ActionButtons';
import type { SettingsModelDto } from '../../../types/settings';

interface LlamaCuratedCompletionProps {
  catalogModel: SettingsModelDto | null;
  routerModelId: string;
  onSetDefault?: (catalogModelId: string) => Promise<void>;
  onViewInstalled?: (catalogModelId: string) => void;
  onClose?: () => void;
}

export function LlamaCuratedCompletion({
  catalogModel,
  routerModelId,
  onSetDefault,
  onViewInstalled,
  onClose,
}: LlamaCuratedCompletionProps) {
  const catalogModelId = catalogModel?.modelId ?? '';

  return (
    <div className="space-y-3">
      <div className="rounded border border-emerald-200 bg-emerald-50 px-3 py-3 text-sm text-emerald-900">
        <div className="font-semibold">Installation completed</div>
        {catalogModel ? (
          <div className="mt-1">
            <span className="font-mono">{catalogModel.modelId}</span> is ready in the catalog.
          </div>
        ) : (
          <div className="mt-1">Model <span className="font-mono">{routerModelId}</span> is ready.</div>
        )}
      </div>

      <div className="flex flex-wrap gap-2">
        <TextActionButton
          tone="primary"
          onClick={() => void api.settings.loadLlamaModel(routerModelId)}
          title="Load model into runtime now"
        >
          Load now
        </TextActionButton>
        {catalogModelId && onSetDefault ? (
          <TextActionButton
            tone="accent"
            onClick={() => void onSetDefault(catalogModelId)}
            title="Set as default chat model"
          >
            Use as default chat model
          </TextActionButton>
        ) : null}
        {catalogModelId && onViewInstalled ? (
          <TextActionButton
            tone="neutral"
            onClick={() => onViewInstalled(catalogModelId)}
            title="View installed model"
          >
            View installed model
          </TextActionButton>
        ) : null}
        {onClose ? (
          <TextActionButton tone="neutral" onClick={onClose} title="Close">
            Close
          </TextActionButton>
        ) : null}
      </div>
    </div>
  );
}
