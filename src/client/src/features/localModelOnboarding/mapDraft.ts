import type { LocalAiModelDraft } from '../../components/home/addAiServicesWizard/types';
import type { AddModelWizardState } from '../../pages/settings/types';
import type { LocalModelOnboardingDraft } from './contracts';

export function mapSettingsAddModelStateToOnboardingDraft(
  state: AddModelWizardState
): LocalModelOnboardingDraft {
  return {
    installSource: state.llamaInstallSource,
    runtimeProfileId: state.runtimeProfileId,
    routerModelId: state.llamaRouterModelId,
    huggingFaceRepository: state.llamaHuggingFaceRepository,
    huggingFaceQuantIncludePattern: state.llamaHuggingFaceQuantIncludePattern,
    huggingFaceMmprojIncludePattern: state.llamaHuggingFaceMmprojIncludePattern,
    huggingFaceTargetDirectory: state.llamaHuggingFaceTargetDirectory,
    existingAliasRouterModelId: state.llamaExistingAliasRouterModelId,
    routerContextSize: state.llamaRouterContextSize,
    routerCacheRamMib: state.llamaRouterCacheRamMib,
    catalogModelId: state.catalogModelId,
    catalogDisplayName: state.catalogDisplayName,
    catalogDescription: state.catalogDescription,
    catalogDisplayOrder: state.catalogDisplayOrder,
    catalogIsActive: state.catalogIsActive,
  };
}

export function mapLocalAiModelDraftToOnboardingDraft(
  draft: LocalAiModelDraft
): LocalModelOnboardingDraft {
  return {
    installSource: draft.installSource,
    runtimeProfileId: draft.runtimeProfileId,
    routerModelId: draft.routerModelId,
    huggingFaceRepository: draft.huggingFaceRepository,
    huggingFaceQuantIncludePattern: draft.huggingFaceQuantIncludePattern,
    huggingFaceMmprojIncludePattern: draft.huggingFaceMmprojIncludePattern,
    huggingFaceTargetDirectory: draft.huggingFaceTargetDirectory,
    existingAliasRouterModelId: draft.existingAliasRouterModelId,
    routerContextSize: draft.routerContextSize,
    routerCacheRamMib: draft.routerCacheRamMib,
    catalogModelId: draft.catalogModelId,
    catalogDisplayName: draft.catalogDisplayName,
    catalogIsActive: true,
  };
}
