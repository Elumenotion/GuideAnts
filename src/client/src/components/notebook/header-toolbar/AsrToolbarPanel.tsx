import { FaCog } from 'react-icons/fa';
import { api } from '../../../services/api';
import { textButtonClassName } from '../../../pages/settings/components/shared/ActionButtons';
import type { ServicePanelCommonProps } from './types';
import { WORKSPACE_CONTROLS_COPY, serviceStatusHeadline, statusToneClass, toolbarProviderOptionLabel } from './toolbarFormatters';
import { ServiceLocalRuntimePowerSection } from './ServiceLocalRuntimePowerSection';
import { ToolbarActionError } from './ToolbarActionError';
import { useToolbarAsyncAction } from './useToolbarAsyncAction';

export function AsrToolbarPanel({
  service,
  setInFlight,
  onRefresh,
  onOpenSettings,
  showWorkspaceCopy = true,
}: ServicePanelCommonProps) {
  const { error, run } = useToolbarAsyncAction(setInFlight);
  const activeProviderIsLocal = service.supportsLocalRuntimePower;
  const localProvider = service.providerOptions.find((provider) =>
    provider.providerKind.toLowerCase().includes('local')
  );
  const cloudModelOptions = service.providerOptions.filter(
    (provider) => provider.providerId !== localProvider?.providerId
  );

  const setProvider = (providerId: string) => {
    void run(async () => {
      const updated = await api.settings.services.updateActiveProvider(service.serviceId, providerId);
      if (updated.activeProviderId !== providerId) {
        throw new Error(
          `Provider switch failed: requested '${providerId}' but active is '${updated.activeProviderId}'.`
        );
      }
      await onRefresh();
    });
  };

  const setModel = (modelRef: string) => {
    void run(async () => {
      if (localProvider && service.activeProviderId !== localProvider.providerId) {
        const updated = await api.settings.services.updateActiveProvider(service.serviceId, localProvider.providerId);
        if (updated.activeProviderId !== localProvider.providerId) {
          throw new Error(
            `Provider switch failed: requested '${localProvider.providerId}' but active is '${updated.activeProviderId}'.`
          );
        }
      }
      await api.settings.localModels.selectActive(service.serviceId, modelRef);
      await onRefresh();
    });
  };

  return (
    <div className="space-y-2">
      {showWorkspaceCopy ? <div className="text-xs text-slate-500">{WORKSPACE_CONTROLS_COPY}</div> : null}
      <div className={`text-sm font-medium ${statusToneClass(service.status)}`}>{serviceStatusHeadline(service)}</div>
      {service.blockers.length > 0 && (
        <div className="text-xs text-red-700">{service.blockers[0]}</div>
      )}
      <ToolbarActionError message={error} />

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
              onClick={() => setProvider(provider.providerId)}
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
              onClick={() => setModel(model.modelRef)}
              title={model.isComplete ? undefined : 'Download is incomplete. Finish the download in Settings.'}
              role="option"
              aria-selected={isCurrent}
            >
              {model.displayLabel}
              {isCurrent && !model.displayLabel.includes('(active)') ? ' ✓' : ''}
              {!model.isComplete ? ' (incomplete)' : ''}
            </button>
          );
        })}
      </div>

      <ServiceLocalRuntimePowerSection
        service={service}
        setInFlight={setInFlight}
        onRefresh={onRefresh}
      />

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
