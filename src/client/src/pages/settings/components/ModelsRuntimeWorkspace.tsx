import { useEffect, useState } from 'react';
import { LlamaRuntimeInventoryItemDto, SettingsModelDto, SettingsRuntimeProfileDto } from '../../../types/settings';
import { ActiveAddOperationState, ModelsRuntimeSubTab, ProfileFormState } from '../types';
import { LocalLlamaRuntimeTab } from './LocalLlamaRuntimeTab';
import { ModelsTab } from './ModelsTab';
import { ProfilesTab } from './ProfilesTab';

interface ModelsRuntimeWorkspaceProps {
  initialSubTab?: ModelsRuntimeSubTab;
  focusedAlias?: string;
  focusedModelId?: string;
  llamaInventory: LlamaRuntimeInventoryItemDto[];
  llamaInventoryLoading: boolean;
  llamaInventoryRefreshing: boolean;
  llamaInventoryError: string | null;
  onRefreshLlamaInventory: () => void;
  onLoadLlamaModel: (routerModelId: string) => Promise<void>;
  onRequestUnloadLlamaRouter: (routerModelId: string, notebookReferenceCount: number) => void;
  onRequestDeleteLlamaRouter: (
    routerModelId: string,
    catalogModelIds: string[],
    notebookReferenceCount: number
  ) => void;
  profilesLoading: boolean;
  profiles: SettingsRuntimeProfileDto[];
  modelsLoading: boolean;
  modelsError: string | null;
  orderedModels: SettingsModelDto[];
  deletingModelId: string | null;
  onRetryLoadModels: () => void;
  onRequestDeleteModel: (modelId: string) => void;
  onCatalogEdited: () => Promise<void>;
  onOpenAddModelWizard: (providerPreselect?: string) => void;
  activeAddOperation: ActiveAddOperationState | null;
  profileDialogOpen: boolean;
  editingProfileId: string | null;
  profileForm: ProfileFormState;
  profileSaving: boolean;
  profilesError: string | null;
  deletingProfileId: string | null;
  onProfileFormChange: <K extends keyof ProfileFormState>(key: K, value: ProfileFormState[K]) => void;
  onOpenCreateProfile: () => void;
  onImportProfile: (form: ProfileFormState) => void;
  onResetProfileForm: () => void;
  onSaveProfile: () => void;
  onRetryLoadProfiles: () => void;
  onEditProfile: (profile: SettingsRuntimeProfileDto) => void;
  onRequestDeleteProfile: (profileId: string) => void;
  onInsertRuntimeProfileTemplate: (template: 'qwen3_5' | 'qwen3_6' | 'gemma4') => void;
}

const subTabs: Array<{ key: ModelsRuntimeSubTab; label: string }> = [
  { key: 'catalog', label: 'Catalog' },
  { key: 'profiles', label: 'Runtime Profiles' },
  { key: 'local-llama', label: 'Local Llama Runtime' },
];

export function ModelsRuntimeWorkspace(props: ModelsRuntimeWorkspaceProps) {
  const [subTab, setSubTab] = useState<ModelsRuntimeSubTab>(props.initialSubTab ?? 'catalog');

  useEffect(() => {
    if (props.initialSubTab) {
      setSubTab(props.initialSubTab);
      return;
    }
    // Phase F (R-6.*): if a deep-link landed on this workspace with a focus
    // target but no explicit subTab, switch to the tab that actually owns that
    // target so the user sees the highlight without another click.
    if (props.focusedAlias) {
      setSubTab('local-llama');
    } else if (props.focusedModelId) {
      setSubTab('catalog');
    }
  }, [props.initialSubTab, props.focusedAlias, props.focusedModelId]);

  return (
    <div className="space-y-6">
      <div className="border-b border-gray-200">
        <nav className="flex flex-wrap gap-4" aria-label="Models and runtime sections">
          {subTabs.map((tab) => (
            <button
              key={tab.key}
              type="button"
              onClick={() => setSubTab(tab.key)}
              className={`border-b-2 px-1 py-2 text-sm font-medium ${
                subTab === tab.key
                  ? 'border-blue-500 text-blue-600'
                  : 'border-transparent text-gray-500 hover:border-gray-300 hover:text-gray-700'
              }`}
            >
              {tab.label}
            </button>
          ))}
        </nav>
      </div>

      {subTab === 'catalog' && (
        <ModelsTab
          modelsLoading={props.modelsLoading}
          modelsError={props.modelsError}
          orderedModels={props.orderedModels}
          deletingModelId={props.deletingModelId}
          llamaInventory={props.llamaInventory}
          llamaInventoryLoading={props.llamaInventoryLoading}
          focusedModelId={props.focusedModelId}
          onRetryLoadModels={props.onRetryLoadModels}
          onRequestDeleteModel={props.onRequestDeleteModel}
          profiles={props.profiles}
          profilesLoading={props.profilesLoading}
          onCatalogEdited={props.onCatalogEdited}
          onOpenAddModel={props.onOpenAddModelWizard}
          activeAddOperation={props.activeAddOperation}
        />
      )}

      {subTab === 'profiles' && (
        <ProfilesTab
          profileDialogOpen={props.profileDialogOpen}
          editingProfileId={props.editingProfileId}
          profileForm={props.profileForm}
          profileSaving={props.profileSaving}
          profilesLoading={props.profilesLoading}
          profilesError={props.profilesError}
          profiles={props.profiles}
          deletingProfileId={props.deletingProfileId}
          onProfileFormChange={props.onProfileFormChange}
          onOpenCreateProfile={props.onOpenCreateProfile}
          onImportProfile={props.onImportProfile}
          onResetProfileForm={props.onResetProfileForm}
          onSaveProfile={props.onSaveProfile}
          onRetryLoadProfiles={props.onRetryLoadProfiles}
          onEditProfile={props.onEditProfile}
          onRequestDeleteProfile={props.onRequestDeleteProfile}
          onInsertTemplate={props.onInsertRuntimeProfileTemplate}
        />
      )}

      {subTab === 'local-llama' && (
        <LocalLlamaRuntimeTab
          inventory={props.llamaInventory}
          inventoryLoading={props.llamaInventoryLoading}
          inventoryRefreshing={props.llamaInventoryRefreshing}
          inventoryError={props.llamaInventoryError}
          onRefresh={props.onRefreshLlamaInventory}
          onLoad={props.onLoadLlamaModel}
          onRequestUnload={props.onRequestUnloadLlamaRouter}
          onRequestDelete={props.onRequestDeleteLlamaRouter}
          onOpenAddModelWizard={(providerPreselect) => props.onOpenAddModelWizard(providerPreselect)}
          focusedAlias={props.focusedAlias}
        />
      )}
    </div>
  );
}
