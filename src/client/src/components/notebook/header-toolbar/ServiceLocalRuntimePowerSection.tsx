import { FaCheck, FaPlay, FaSpinner, FaStop } from 'react-icons/fa';
import { api } from '../../../services/api';
import type { NotebookToolbarServiceDto } from '../../../types/notebookToolbar';
import { ToolbarActionError } from './ToolbarActionError';
import { useToolbarAsyncAction } from './useToolbarAsyncAction';

interface ServiceLocalRuntimePowerSectionProps {
  service: NotebookToolbarServiceDto;
  setInFlight: (value: boolean) => void;
  onRefresh: () => Promise<void>;
  resourceLabel?: string;
}

export function ServiceLocalRuntimePowerSection({
  service,
  setInFlight,
  onRefresh,
  resourceLabel = 'Local model',
}: ServiceLocalRuntimePowerSectionProps) {
  const { error, run } = useToolbarAsyncAction(setInFlight);
  const hasPendingOp =
    Boolean(service.inProgressState)
    && service.inProgressState !== 'ready'
    && service.inProgressState !== 'failed';

  const activeModelRef =
    service.localModelOptions.find((model) => model.isActive)?.modelRef
    ?? service.selection?.resourceId
    ?? undefined;

  const loadButtonLabel = hasPendingOp
    ? 'Loading...'
    : service.localRuntimeOn
      ? 'Loaded'
      : 'Load model';

  const powerOn = () => {
    void run(async () => {
      if (!activeModelRef) {
        throw new Error(`Select a ${resourceLabel.toLowerCase()} before loading.`);
      }

      if (service.serviceId === 'ImageGeneration') {
        await api.settings.localModels.selectActive(service.serviceId, activeModelRef);
      } else {
        await api.settings.localModels.load(service.serviceId, { model_path: activeModelRef });
      }

      await onRefresh();
    });
  };

  const powerOff = () => {
    void run(async () => {
      await api.settings.localModels.unload(service.serviceId);
      await onRefresh();
    });
  };

  if (!service.supportsLocalRuntimePower) {
    return null;
  }

  return (
    <div className="mt-2 space-y-1 border-t pt-2">
      <div className="flex items-center gap-2">
        <span className="text-xs text-slate-700">{resourceLabel}</span>
        <button
          type="button"
          className={`inline-flex items-center gap-1 rounded border px-2 py-1 text-xs font-medium ${
            service.localRuntimeOn
              ? 'border-emerald-200 bg-emerald-50 text-emerald-700'
              : 'border-emerald-300 bg-white text-emerald-700 hover:bg-emerald-50'
          } disabled:cursor-not-allowed disabled:opacity-70`}
          aria-label={
            service.localRuntimeOn
              ? `Selected local ${resourceLabel.toLowerCase()} is loaded`
              : `Load selected local ${resourceLabel.toLowerCase()}`
          }
          title={
            service.localRuntimeOn
              ? `Selected local ${resourceLabel.toLowerCase()} is loaded`
              : `Load selected local ${resourceLabel.toLowerCase()}`
          }
          disabled={hasPendingOp || service.localRuntimeOn}
          onClick={powerOn}
        >
          {hasPendingOp ? (
            <FaSpinner className="h-3.5 w-3.5 animate-spin" />
          ) : service.localRuntimeOn ? (
            <FaCheck className="h-3.5 w-3.5" />
          ) : (
            <FaPlay className="h-3.5 w-3.5" />
          )}
          {loadButtonLabel}
        </button>
        {service.localRuntimeOn ? (
          <button
            type="button"
            className="inline-flex items-center gap-1 rounded border border-slate-300 bg-white px-2 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-70"
            aria-label={`Unload selected local ${resourceLabel.toLowerCase()}`}
            title={`Unload selected local ${resourceLabel.toLowerCase()}`}
            disabled={hasPendingOp}
            onClick={powerOff}
          >
            <FaStop className="h-3.5 w-3.5" />
            Unload
          </button>
        ) : null}
      </div>
      <ToolbarActionError message={error} />
    </div>
  );
}
