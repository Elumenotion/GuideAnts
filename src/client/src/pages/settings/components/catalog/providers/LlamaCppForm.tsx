import type { CatalogEditState } from '../../../types';
import { LlamaInstalledSummary } from '../../../../../features/localModelOnboarding/installed/LlamaInstalledSummary';
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

export function LlamaCppEditForm({
  value,
  onChange,
  onDetailChanged,
  sharedProfileModelCount,
}: Omit<ProviderEditForm, 'profiles' | 'profilesLoading' | 'inventory'> & {
  onDetailChanged?: () => Promise<void>;
  sharedProfileModelCount?: number;
}) {
  void onChange;
  return (
    <LlamaInstalledSummary
      modelId={value.modelId}
      sharedProfileModelCount={sharedProfileModelCount}
      onChanged={onDetailChanged}
    />
  );
}

export type { CatalogEditState };
