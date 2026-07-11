import type {
  AddModelErrorDto,
  LlamaCatalogDefinitionDto,
  LlamaCatalogQuantsResponseDto,
  LlamaCatalogResponseDto,
  LlamaOperationStatusDto,
  SettingsModelDto,
} from '../../../types/settings';

export type LocalModelOnboardingMode = 'curated' | 'custom' | 'existingAlias';

export type CuratedOnboardingStep = 'model' | 'quant' | 'review' | 'progress' | 'completed';

export interface CuratedOnboardingState {
  mode: LocalModelOnboardingMode;
  step: CuratedOnboardingStep;
  catalogLoading: boolean;
  catalogError: string | null;
  catalogResponse: LlamaCatalogResponseDto | null;
  searchQuery: string;
  selectedDefinitionId: string | null;
  quantsLoading: boolean;
  quantsError: string | null;
  quantsResponse: LlamaCatalogQuantsResponseDto | null;
  selectedQuantId: string | null;
  selectionInvalidated: boolean;
  submitting: boolean;
  submitError: AddModelErrorDto | null;
  operationId: string | null;
  operation: LlamaOperationStatusDto | null;
  completedCatalogModel: SettingsModelDto | null;
  completedRouterModelId: string | null;
}

export interface CuratedOnboardingActions {
  setMode: (mode: LocalModelOnboardingMode) => void;
  setSearchQuery: (query: string) => void;
  selectModel: (definitionId: string) => void;
  selectQuant: (quantId: string) => void;
  refreshQuants: () => Promise<void>;
  goToStep: (step: CuratedOnboardingStep) => void;
  goBack: () => void;
  canContinue: () => boolean;
  submit: () => Promise<void>;
  retrySubmit: () => Promise<void>;
  loadCatalog: () => Promise<void>;
  applyOperationUpdate: (operation: LlamaOperationStatusDto) => void;
}

export function getSelectedDefinition(
  state: CuratedOnboardingState
): LlamaCatalogDefinitionDto | null {
  if (!state.catalogResponse || !state.selectedDefinitionId) {
    return null;
  }
  return state.catalogResponse.models.find((m) => m.id === state.selectedDefinitionId) ?? null;
}

export function getSelectedQuant(state: CuratedOnboardingState) {
  if (!state.quantsResponse || !state.selectedQuantId) {
    return null;
  }
  return state.quantsResponse.quants.find((q) => q.id === state.selectedQuantId) ?? null;
}

export function filterCatalogModels(
  models: LlamaCatalogDefinitionDto[],
  searchQuery: string
): LlamaCatalogDefinitionDto[] {
  const query = searchQuery.trim().toLowerCase();
  if (!query) {
    return models;
  }
  return models.filter((model) => {
    const haystack = [
      model.id,
      model.display.name,
      model.display.description,
      model.source.repository,
      ...model.display.labels,
      model.display.license,
    ]
      .join(' ')
      .toLowerCase();
    return haystack.includes(query);
  });
}

export function isQuantRecommended(
  quantLabel: string,
  recommendedLabels: string[] | null | undefined
): boolean {
  if (!recommendedLabels?.length) {
    return false;
  }
  const normalized = quantLabel.trim().toUpperCase();
  return recommendedLabels.some((label) => label.trim().toUpperCase() === normalized);
}
