import { useMemo } from 'react';
import { FaSave, FaSpinner } from 'react-icons/fa';
import { TextActionButton } from '../../components/shared/ActionButtons';
import { OperationalDependencyRow } from '../../components/shared/OperationalDependencyRow';
import { ProviderFieldsSection } from '../../components/shared/ProviderFieldsSection';
import { ProviderSelector } from '../../components/shared/ProviderSelector';
import { ServiceEditorShell } from '../../components/shared/ServiceEditorShell';
import { useServiceEditorController } from '../../state/useServiceEditorController';
import { ImageBundleManager } from './ImageBundleManager';
import {
  allowedSizesForAzureProfile,
  azureProfileDisplayLabel,
  inferAzureImageProfile,
  localSdAllowedSizes,
} from './imageGenerationUi';

export function ImageGenerationEditor() {
  const {
    state,
    loading,
    error,
    saving,
    fieldErrors,
    draft,
    selectedProvider,
    persistedActiveLabel,
    editingProviderLabel,
    providerOptions,
    save,
    clearFieldError,
  } = useServiceEditorController('ImageGeneration');

  const deploymentDraft = String(draft.activeDraft['Deployment'] ?? '').trim();
  const azureProfile = useMemo(() => inferAzureImageProfile(deploymentDraft), [deploymentDraft]);
  const azureSizes = useMemo(() => allowedSizesForAzureProfile(azureProfile), [azureProfile]);

  if (loading) {
    return <div className="text-sm text-gray-600">Loading Image Generation settings…</div>;
  }

  if (!state || !selectedProvider) {
    return <div className="rounded border border-red-200 bg-red-50 p-3 text-sm text-red-700">{error ?? 'Not found.'}</div>;
  }

  const isLocal = selectedProvider.providerKind !== 'Cloud';

  return (
    <ServiceEditorShell
      serviceName="Image Generation"
      activeProviderLabel={persistedActiveLabel}
      editingProviderLabel={editingProviderLabel}
      readinessStatus={state.readiness.status}
      readinessSummary={state.readiness.blockers.length > 0 ? state.readiness.blockers.join(' | ') : 'Ready'}
      providerSelector={
        <div className="space-y-2">
          <ProviderSelector
            value={draft.activeProviderId}
            options={providerOptions}
            onChange={(id) => draft.switchProvider(id)}
          />
          <p className="text-xs text-gray-500">
            Select a provider, adjust settings, then click <span className="font-medium">Save and activate provider</span>.
          </p>
        </div>
      }
      providerSettings={
        <div className="space-y-6">
          {isLocal ? <ImageBundleManager enabled={isLocal} /> : null}

          <ProviderFieldsSection
            provider={selectedProvider}
            draft={draft.activeDraft}
            fieldErrors={fieldErrors}
            onPatch={(patch) => draft.patchActiveDraft(patch)}
            onClearFieldError={clearFieldError}
          />

          {!isLocal ? (
            <div className="space-y-2 border-t border-gray-100 pt-4">
              <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">Generation profile</div>
              <p className="text-sm text-gray-700">
                Inferred profile: <span className="font-medium">{azureProfileDisplayLabel(azureProfile)}</span> (from{' '}
                <span className="font-mono text-xs">Deployment</span> name).
              </p>
              <p className="text-xs text-gray-600">
                Allowed sizes for notebook image tools with this profile: {azureSizes.join(', ')}. Edit flows use the same
                provider constraints; edit requests use <span className="font-mono">Edit Model Deployment</span>.
              </p>
              <p className="text-xs text-gray-600">
                Cloud image generation does not honor an output-format field in the request payload for this stack — use PNG
                unless the deployment documentation specifies otherwise.
              </p>
            </div>
          ) : null}

          {isLocal ? (
            <div className="space-y-2 border-t border-gray-100 pt-4">
              <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">Generation profile</div>
              <p className="text-sm text-gray-700">
                Local Stable Diffusion uses flux-style sizes: {localSdAllowedSizes.join(', ')}.
              </p>
              <p className="text-xs text-gray-600">
                Default output format is saved above and applied to local txt2img/img2img calls (see server image pipeline).
              </p>
            </div>
          ) : null}

          {selectedProvider.runtimeDependencies.length > 0 ? (
            <div className="space-y-2 border-t border-gray-100 pt-4">
              <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">Operational Dependencies</div>
              {selectedProvider.runtimeDependencies.map((dependency) => (
                <OperationalDependencyRow
                  key={dependency.key}
                  keyName={dependency.key}
                  displayName={dependency.displayName}
                  hasValue={dependency.hasValue}
                  currentValue={dependency.currentValue}
                  changeHint={dependency.changeHint}
                />
              ))}
            </div>
          ) : null}

          <div className="border-t border-gray-100 pt-4">
            <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">Runtime tuning (container)</div>
            <p className="mt-1 text-sm text-gray-700">
              Advanced Stable Diffusion engine variables (<span className="font-mono">GA_SD_*</span>) are configured on the
              guideants-ai / SD container environment, not in Application Settings. Change them in compose or deployment
              manifests when you need to adjust steps, CFG, sampling, warmup, or model paths on disk.
            </p>
          </div>
        </div>
      }
      actions={
        <>
          {error ? <span className="mr-3 text-xs text-red-700">{error}</span> : null}
          <TextActionButton
            tone="primary"
            icon={saving ? <FaSpinner className="animate-spin" /> : <FaSave />}
            disabled={saving}
            onClick={() => void save()}
            title="Save and activate provider."
          >
            Save
          </TextActionButton>
        </>
      }
    />
  );
}
