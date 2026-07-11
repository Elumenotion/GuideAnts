import type { ReactNode } from 'react';
import type { CreateRuntimeProfileRequest, LlamaRuntimeInventoryItemDto, SettingsRuntimeProfileDto } from '../../../types/settings';
import type { AddModelWizardState } from '../../../pages/settings/types';
import { LlamaCppAddForm } from '../../../pages/settings/components/catalog/providers/LlamaCppForm';
import { LlamaCuratedOnboardingFlow } from './LlamaCuratedOnboardingFlow';
import { OnboardingModeSelector } from '../advanced/OnboardingModeSelector';
import type { LocalModelOnboardingMode } from './types';

export interface LlamaLocalModelOnboardingPanelProps {
  mode: LocalModelOnboardingMode;
  onModeChange: (mode: LocalModelOnboardingMode) => void;
  onboardingUi: 'settings' | 'wizard';
  settingsValue?: AddModelWizardState;
  onSettingsChange?: (updates: Partial<AddModelWizardState>) => void;
  profiles: SettingsRuntimeProfileDto[];
  profilesLoading: boolean;
  inventory: LlamaRuntimeInventoryItemDto[];
  inventoryError?: string | null;
  onCreateRuntimeProfile?: (template: 'qwen3_5' | 'qwen3_6' | 'gemma4') => Promise<void>;
  onCreateCustomRuntimeProfile?: (request: CreateRuntimeProfileRequest) => Promise<SettingsRuntimeProfileDto>;
  advancedForm?: ReactNode;
  onCuratedStepChange?: (step: string) => void;
  onCuratedOperationStarted?: (operationId: string, meta: {
    catalogModelId: string;
    catalogDisplayName: string;
    routerModelId: string;
  }) => void;
  onCuratedCompleted?: (result: { catalogModelId: string; routerModelId: string }) => void;
  onSetDefault?: (catalogModelId: string) => Promise<void>;
  onViewInstalled?: (catalogModelId: string) => void;
  onClose?: () => void;
  renderCuratedFooter?: Parameters<typeof LlamaCuratedOnboardingFlow>[0]['renderFooter'];
}

export function LlamaLocalModelOnboardingPanel({
  mode,
  onModeChange,
  onboardingUi,
  settingsValue,
  onSettingsChange,
  profiles,
  profilesLoading,
  inventory,
  inventoryError,
  advancedForm,
  onCuratedStepChange,
  onCuratedOperationStarted,
  onCuratedCompleted,
  onSetDefault,
  onViewInstalled,
  onClose,
  renderCuratedFooter,
}: LlamaLocalModelOnboardingPanelProps) {
  return (
    <div className="space-y-4">
      {mode === 'curated' ? (
        <LlamaCuratedOnboardingFlow
          onboardingUi={onboardingUi}
          onStepChange={onCuratedStepChange}
          onOperationStarted={onCuratedOperationStarted}
          onCompleted={(result) =>
            onCuratedCompleted?.({
              catalogModelId: result.catalogModel?.modelId ?? '',
              routerModelId: result.routerModelId,
            })
          }
          onSetDefault={onSetDefault}
          onViewInstalled={onViewInstalled}
          onClose={onClose}
          renderFooter={renderCuratedFooter}
        />
      ) : null}

      {mode !== 'curated' && onboardingUi === 'settings' && settingsValue && onSettingsChange ? (
        <LlamaCppAddForm
          value={{
            ...settingsValue,
            llamaInstallSource: mode === 'existingAlias' ? 'existingAlias' : 'huggingface',
          }}
          onChange={(updates) => onSettingsChange(updates)}
          profiles={profiles}
          profilesLoading={profilesLoading}
          inventory={inventory}
          inventoryError={inventoryError}
        />
      ) : null}

      {mode !== 'curated' && onboardingUi === 'wizard' && advancedForm ? advancedForm : null}

      <details className="rounded border border-gray-200 bg-gray-50 px-3 py-2">
        <summary className="cursor-pointer text-sm font-medium text-gray-800">
          Custom Hugging Face or attach existing alias
        </summary>
        <div className="mt-3 space-y-3">
          <p className="text-xs text-gray-600">
            Use Custom Hugging Face or Attach existing alias only when the curated catalog does not cover your model.
          </p>
          <OnboardingModeSelector mode={mode} onChange={onModeChange} />
        </div>
      </details>
    </div>
  );
}

