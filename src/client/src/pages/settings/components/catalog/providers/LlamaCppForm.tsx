import { forwardRef } from 'react';
import type { CatalogEditState } from '../../../types';
import {
  LlamaInstalledSummary,
  type LlamaInstalledSummaryHandle,
} from '../../../../../features/localModelOnboarding/installed/LlamaInstalledSummary';
import { CustomHfOnboardingForm } from '../../../../../features/localModelOnboarding/advanced/CustomHfOnboardingForm';
import { AttachAliasOnboardingForm } from '../../../../../features/localModelOnboarding/advanced/AttachAliasOnboardingForm';
import type { ProviderAddForm, ProviderEditForm } from './types';

export function LlamaCppAddForm({
  value,
  onChange,
  profiles,
  profilesLoading,
  inventory,
  inventoryError,
}: ProviderAddForm) {
  if (value.llamaInstallSource === 'existingAlias') {
    return (
      <AttachAliasOnboardingForm
        value={value}
        onChange={onChange}
        profiles={profiles}
        profilesLoading={profilesLoading}
        inventory={inventory}
        inventoryError={inventoryError}
      />
    );
  }

  return (
    <CustomHfOnboardingForm
      value={value}
      onChange={onChange}
      profiles={profiles}
      profilesLoading={profilesLoading}
      inventory={inventory}
    />
  );
}

export type LlamaCppEditFormHandle = LlamaInstalledSummaryHandle;

export const LlamaCppEditForm = forwardRef<
  LlamaCppEditFormHandle,
  Omit<ProviderEditForm, 'profiles' | 'profilesLoading' | 'inventory'> & {
    onDetailChanged?: () => Promise<void>;
  }
>(function LlamaCppEditForm({ value, onChange, onDetailChanged }, ref) {
  void onChange;
  return (
    <LlamaInstalledSummary
      ref={ref}
      modelId={value.modelId}
      onChanged={onDetailChanged}
    />
  );
});

export type { CatalogEditState };
