import { FaCog, FaPlay, FaStop } from 'react-icons/fa';
import { api } from '../../../services/api';
import { textButtonClassName } from '../../../pages/settings/components/shared/ActionButtons';
import type { ServicePanelCommonProps } from './types';
import { WORKSPACE_CONTROLS_COPY, serviceSummaryLine, statusToneClass, toolbarProviderOptionLabel } from './toolbarFormatters';

export function AsrToolbarPanel({
  service,
  setInFlight,
  onRefresh,
  onOpenSettings,
  showWorkspaceCopy = true,
}: ServicePanelCommonProps) {
  const activeProviderIsLocal = service.supportsLocalRuntimePower;
  const localProvider = service.providerOptions.find((provider) =>
    provider.providerKind.toLowerCase().includes('local')
  );
  const cloudModelOptions = service.providerOptions.filter(
    (provider) => provider.providerId !== localProvider?.providerId
  );

  const setProvider = async (providerId: string) => {
    setInFlight(true);
    try {
      const updated = await api.settings.services.updateActiveProvider(service.serviceId, providerId);
      if (updated.activeProviderId !== providerId) {
        console.error(
          `[toolbar][asr] provider switch mismatch: requested='${providerId}' actual='${updated.activeProviderId}'`
        );
      }
      await onRefresh();
    } finally {
      setInFlight(false);
    }
  };

  const setModel = async (modelRef: string) => {
    setInFlight(true);
    try {
      if (localProvider && service.activeProviderId !== localProvider.providerId) {
        const updated = await api.settings.services.updateActiveProvider(service.serviceId, localProvider.providerId);
        if (updated.activeProviderId !== localProvider.providerId) {
          console.error(
            `[toolbar][asr] provider switch mismatch: requested='${localProvider.providerId}' actual='${updated.activeProviderId}'`
          );
        }
      }
      await api.settings.localModels.selectActive(service.serviceId, modelRef);
      await onRefresh();
    } finally {
      setInFlight(false);
    }
  };

  const powerOn = async () => {
    const activeModel = service.localModelOptions.find((item) => item.isActive) ?? service.localModelOptions[0];
    if (!activeModel) return;
    setInFlight(true);
    try {
      await api.settings.localModels.load(service.serviceId, { model_path: activeModel.modelRef });
      await onRefresh();
    } finally {
      setInFlight(false);
    }
  };

  const powerOff = async () => {
    setInFlight(true);
    try {
      await api.settings.localModels.unload(service.serviceId);
      await onRefresh();
    } finally {
      setInFlight(false);
    }
  };

  return (
    <div className="space-y-2">
      {showWorkspaceCopy ? <div className="text-xs text-slate-500">{WORKSPACE_CONTROLS_COPY}</div> : null}
      <div className={`text-sm font-medium ${statusToneClass(service.status)}`}>{serviceSummaryLine(service)}</div>
      {service.blockers.length > 0 && (
        <div className="text-xs text-red-700">{service.blockers[0]}</div>
      )}

      <div className="space-y-1">
        {cloudModelOptions.map((provider) => {
          const isCurrentProvider = provider.providerId === service.activeProviderId;
          return (
            <button
              key={provider.providerId}
              type="button"
              className={`${textButtonClassName('neutral')} w-full justify-start text-left ${
                provider.canActivate ? '' : 'opacity-60'
              } ${isCurrentProvider ? 'ring-2 ring-emerald-400/60 bg-emerald-50 font-medium' : ''}`}
              disabled={!provider.canActivate}
              onClick={() => void setProvider(provider.providerId)}
              title={provider.canActivate ? undefined : provider.blockers[0] ?? 'Provider is blocked.'}
              role="option"
              aria-selected={isCurrentProvider}
            >
              {toolbarProviderOptionLabel(provider)}
              {isCurrentProvider ? ' ✓' : ''}
              {!provider.canActivate ? ` — ${provider.blockers[0] ?? 'blocked'}` : ''}
            </button>
          );
        })}
      </div>

      <div className="space-y-1 max-h-32 overflow-auto">
        {service.localModelOptions.map((model) => {
          const isCurrent = activeProviderIsLocal && model.isActive;
          return (
            <button
              key={`${model.modelRef}:${model.displayLabel}`}
              type="button"
              className={`${textButtonClassName('neutral')} w-full justify-start text-left ${
                model.isComplete ? '' : 'opacity-50'
              } ${isCurrent ? 'ring-2 ring-emerald-400/60 bg-emerald-50 font-medium' : ''}`}
              disabled={!model.isComplete}
              onClick={() => void setModel(model.modelRef)}
              role="option"
              aria-selected={isCurrent}
            >
              {model.displayLabel}
              {isCurrent && !model.displayLabel.includes('(active)') ? ' ✓' : ''}
            </button>
          );
        })}
      </div>

      {service.supportsLocalRuntimePower && (
        <div className="border-t pt-2 flex items-center gap-2">
          <span className="text-xs">Runtime</span>
          <button
            type="button"
            className="p-1.5 rounded border border-emerald-300 text-emerald-700"
            aria-label="Turn ASR runtime on"
            onClick={() => void powerOn()}
          >
            <FaPlay className="w-3.5 h-3.5" />
          </button>
          <button
            type="button"
            className="p-1.5 rounded border border-slate-300 text-slate-700"
            aria-label="Turn ASR runtime off"
            onClick={() => void powerOff()}
          >
            <FaStop className="w-3.5 h-3.5" />
          </button>
        </div>
      )}

      <button
        type="button"
        className="text-blue-600 text-xs inline-flex items-center gap-1 mt-1"
        onClick={onOpenSettings}
      >
        <FaCog className="w-3.5 h-3.5" />
        Open in Settings
      </button>
    </div>
  );
}
