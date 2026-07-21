import type { LocalAiModelDraft } from '../../components/home/addAiServicesWizard/types';
import type { AddModelWizardState } from '../../pages/settings/types';
import type { LocalModelOnboardingDraft } from './contracts';
import { stripPresetRowMetadata } from './routerPreset';

export function mapSettingsAddModelStateToOnboardingDraft(
  state: AddModelWizardState
): LocalModelOnboardingDraft {
  return {
    installSource: state.llamaInstallSource,
    runtimeProfileId: state.runtimeProfileId,
    routerModelId: state.llamaRouterModelId,
    huggingFaceRepository: state.llamaHuggingFaceRepository,
    huggingFaceResolvedRevision: state.llamaHuggingFaceResolvedRevision,
    huggingFaceArtifactGroupId: state.llamaHuggingFaceArtifactGroupId,
    huggingFaceModelFiles: state.llamaHuggingFaceModelFiles,
    huggingFaceMmprojFiles: state.llamaHuggingFaceMmprojFiles,
    huggingFaceTargetDirectory: state.llamaHuggingFaceTargetDirectory,
    huggingFaceRouterPresetRows: stripPresetRowMetadata(state.llamaHuggingFaceRouterPresetRows),
    huggingFacePresetMode: state.llamaHuggingFacePresetMode,
    existingAliasRouterModelId: state.llamaExistingAliasRouterModelId,
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
    huggingFaceResolvedRevision: draft.huggingFaceResolvedRevision,
    huggingFaceArtifactGroupId: draft.huggingFaceArtifactGroupId,
    huggingFaceModelFiles: draft.huggingFaceModelFiles,
    huggingFaceMmprojFiles: draft.huggingFaceMmprojFiles,
    huggingFaceTargetDirectory: draft.huggingFaceTargetDirectory,
    huggingFaceRouterPresetRows: stripPresetRowMetadata(draft.huggingFaceRouterPresetRows),
    huggingFacePresetMode: draft.huggingFacePresetMode,
    existingAliasRouterModelId: draft.existingAliasRouterModelId,
    catalogModelId: draft.catalogModelId,
    catalogDisplayName: draft.catalogDisplayName,
    catalogIsActive: true,
  };
}
