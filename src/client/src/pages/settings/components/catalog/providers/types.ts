import { AddModelWizardState, CatalogEditState } from '../../../types';
import { CreateRuntimeProfileRequest, LlamaRuntimeInventoryItemDto, SettingsRuntimeProfileDto } from '../../../../../types/settings';

/**
 * Props shared by every provider-specific "edit" form used inside
 * `CatalogRowEditModal`. These components are plain form renderers chosen
 * by a `switch (provider)` — not GoF Strategy objects. See the naming note in
 * `docs/add-model-wizard-and-catalog-redesign.md` D1 Step 3.
 */
export interface ProviderEditForm {
  value: CatalogEditState;
  onChange: (updates: Partial<CatalogEditState>) => void;
  profiles: SettingsRuntimeProfileDto[];
  profilesLoading: boolean;
  /**
   * Live Llama runtime inventory (only supplied for `llama-cpp` edit). Used to
   * show read-only backing details (GGUF/mmproj paths, runtime state, other
   * catalog rows using the same alias) so the operator knows exactly which
   * install they are editing.
   */
  inventory?: LlamaRuntimeInventoryItemDto[];
}

/**
 * Props shared by every provider-specific "add" form used inside
 * `AddModelWizard` Step 3. Same dispatch rationale as `ProviderEditForm`.
 */
export interface ProviderAddForm {
  value: AddModelWizardState;
  onChange: (updates: Partial<AddModelWizardState>) => void;
  profiles: SettingsRuntimeProfileDto[];
  profilesLoading: boolean;
  inventory: LlamaRuntimeInventoryItemDto[];
  inventoryError?: string | null;
  onCreateRuntimeProfile?: (
    template: 'qwen3_5' | 'qwen3_6' | 'gemma4',
    suggestedProfileId?: string
  ) => Promise<void>;
  onCreateCustomRuntimeProfile?: (request: CreateRuntimeProfileRequest) => Promise<SettingsRuntimeProfileDto>;
}
