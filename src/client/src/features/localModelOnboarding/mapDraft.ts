import type { LocalAiModelDraft } from '../../components/home/addAiServicesWizard/types';
import type { AddModelWizardState } from '../../pages/settings/types';
import type { LocalModelOnboardingDraft } from './contracts';
import { stripPresetRowMetadata } from './routerPreset';

const EMPTY_CHAT_BEHAVIOR = {
  samplingParametersJson: '{}',
  reasoningChoicesJson: '',
  thinkingControlJson: '{}',
  requestFieldsWhenToolsPresentJson: '{}',
  combineSystemAndDeveloperMessages: true,
  thoughtBlockPattern: '',
} as const;

export function mapSettingsAddModelStateToOnboardingDraft(
  state: AddModelWizardState
): LocalModelOnboardingDraft {
  return {
    installSource: state.llamaInstallSource,
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
    samplingParametersJson: state.samplingParametersJson,
    reasoningChoicesJson: state.reasoningChoicesJson,
    thinkingControlJson: state.thinkingControlJson,
    requestFieldsWhenToolsPresentJson: state.requestFieldsWhenToolsPresentJson,
    combineSystemAndDeveloperMessages: state.combineSystemAndDeveloperMessages,
    thoughtBlockPattern: state.thoughtBlockPattern,
  };
}

export function mapLocalAiModelDraftToOnboardingDraft(
  draft: LocalAiModelDraft
): LocalModelOnboardingDraft {
  return {
    installSource: draft.installSource,
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
    samplingParametersJson: draft.samplingParametersJson ?? EMPTY_CHAT_BEHAVIOR.samplingParametersJson,
    reasoningChoicesJson: draft.reasoningChoicesJson ?? EMPTY_CHAT_BEHAVIOR.reasoningChoicesJson,
    thinkingControlJson: draft.thinkingControlJson ?? EMPTY_CHAT_BEHAVIOR.thinkingControlJson,
    requestFieldsWhenToolsPresentJson:
      draft.requestFieldsWhenToolsPresentJson ?? EMPTY_CHAT_BEHAVIOR.requestFieldsWhenToolsPresentJson,
    combineSystemAndDeveloperMessages:
      draft.combineSystemAndDeveloperMessages ?? EMPTY_CHAT_BEHAVIOR.combineSystemAndDeveloperMessages,
    thoughtBlockPattern: draft.thoughtBlockPattern ?? EMPTY_CHAT_BEHAVIOR.thoughtBlockPattern,
  };
}
