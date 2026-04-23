import { FaCog, FaPlay, FaStop } from 'react-icons/fa';
import { api } from '../../../services/api';
import { textButtonClassName } from '../../../pages/settings/components/shared/ActionButtons';
import type { ServicePanelCommonProps } from './types';
import { WORKSPACE_CONTROLS_COPY, serviceSummaryLine, statusToneClass } from './toolbarFormatters';

export function TtsToolbarPanel({
  service,
  setInFlight,
  onRefresh,
  onOpenSettings,
  showWorkspaceCopy = true,
}: ServicePanelCommonProps) {
  const activeProviderIsLocal = service.supportsLocalRuntimePower;

  const setProvider = async (providerId: string) => {
    setInFlight(true);
    try {
      const updated = await api.settings.services.updateActiveProvider(service.serviceId, providerId);
      if (updated.activeProviderId !== providerId) {
        console.error(
          `[toolbar][tts] provider switch mismatch: requested='${providerId}' actual='${updated.activeProviderId}'`
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
      <div className={`text-sm ${statusToneClass(service.status)}`}>{serviceSummaryLine(service)}</div>
      {service.blockers.length > 0 && (
        <div className="text-xs text-red-700">{service.blockers[0]}</div>
      )}

      <div className="space-y-1">
        {service.providerOptions.map((provider) => (
          <button
            key={provider.providerId}
            type="button"
            className={`${textButtonClassName('neutral')} w-full justify-start text-left`}
            onClick={() => void setProvider(provider.providerId)}
            role="option"
            aria-selected={provider.providerId === service.activeProviderId}
          >
            {provider.displayName} ({provider.providerKind})
            {provider.providerId === service.activeProviderId ? ' (current)' : ''}
          </button>
        ))}
      </div>

      <div className="space-y-1 max-h-32 overflow-auto">
        {service.localModelOptions.map((model) => (
          <button
            key={`${model.modelRef}:${model.displayLabel}`}
            type="button"
            className={`${textButtonClassName('neutral')} w-full justify-start text-left ${
              model.isComplete ? '' : 'opacity-50'
            }`}
            disabled={!model.isComplete || !activeProviderIsLocal}
            onClick={() => void setModel(model.modelRef)}
            role="option"
            aria-selected={model.isActive}
          >
            {model.displayLabel}
            {model.isActive && !model.displayLabel.includes('(active)') ? ' (active)' : ''}
          </button>
        ))}
      </div>

      {service.supportsLocalRuntimePower && (
        <div className="border-t pt-2 flex items-center gap-2">
          <span className="text-xs">Runtime</span>
          <button
            type="button"
            className="p-1.5 rounded border border-emerald-300 text-emerald-700"
            aria-label="Turn TTS runtime on"
            onClick={() => void powerOn()}
          >
            <FaPlay className="w-3.5 h-3.5" />
          </button>
          <button
            type="button"
            className="p-1.5 rounded border border-slate-300 text-slate-700"
            aria-label="Turn TTS runtime off"
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
