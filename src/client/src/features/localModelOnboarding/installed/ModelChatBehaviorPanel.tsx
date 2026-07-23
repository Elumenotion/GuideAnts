import { useCallback, useEffect, useState } from 'react';
import { api } from '../../../services/api';
import type { LlamaInstallationDetailDto } from '../../../types/settings';
import { getErrorMessage } from '../../../pages/settings/utils';
import {
  buildProfileUpdateRequest,
  createProfileFormFromContractShape,
} from '../../../pages/settings/utils';
import { ConfirmationDialog } from '../../../components/common/ConfirmationDialog';
import { RuntimeProfileEditor } from '../../../pages/settings/components/RuntimeProfileEditor';
import { TextActionButton } from '../../../pages/settings/components/shared/ActionButtons';
import type { ProfileFormState } from '../../../pages/settings/types';
import { RepairInstallationDialog } from './RepairInstallationDialog';
import { AdoptInstallationDialog } from './AdoptInstallationDialog';

export interface ModelChatBehaviorPanelProps {
  detail: LlamaInstallationDetailDto;
  onChanged?: () => Promise<void>;
}

function createProfileFormFromModel(detail: LlamaInstallationDetailDto): ProfileFormState {
  const model = detail.catalogModel;
  return createProfileFormFromContractShape({
    profileId: model.modelId,
    displayName: model.displayName,
    description: model.description ?? '',
    combineSystemAndDeveloperMessages: model.combineSystemAndDeveloperMessages,
    thoughtBlockPattern: model.thoughtBlockPattern ?? '',
    samplingParametersJson: model.samplingParametersJson,
    thinkingControlJson: model.thinkingControlJson,
    requestFieldsWhenToolsPresentJson: model.requestFieldsWhenToolsPresentJson,
    providers: ['llama-cpp'],
  });
}

function deriveReasoningChoicesJson(thinkingControlJson: string): string | undefined {
  try {
    const parsed = JSON.parse(thinkingControlJson) as { choiceActions?: Record<string, unknown> };
    const choices = Object.keys(parsed.choiceActions ?? {})
      .map((choice) => choice.trim())
      .filter((choice) => choice.length > 0);
    return choices.length === 0 ? undefined : JSON.stringify(choices);
  } catch {
    return undefined;
  }
}

function buildLocalRuntimeConfigJson(detail: LlamaInstallationDetailDto): string {
  return JSON.stringify({ routerModelId: detail.routerModelId });
}

export function ModelChatBehaviorPanel({ detail, onChanged }: ModelChatBehaviorPanelProps) {
  const [profileForm, setProfileForm] = useState<ProfileFormState | null>(null);
  const [profileLoading, setProfileLoading] = useState(true);
  const [profileError, setProfileError] = useState<string | null>(null);
  const [documentSaving, setDocumentSaving] = useState(false);
  const [documentMessage, setDocumentMessage] = useState<string | null>(null);
  const [documentError, setDocumentError] = useState<string | null>(null);
  const [repairOpen, setRepairOpen] = useState(false);
  const [adoptOpen, setAdoptOpen] = useState(false);
  const [saveConfirmOpen, setSaveConfirmOpen] = useState(false);

  const isCurated = !!detail.catalogId;

  const loadProfile = useCallback(async () => {
    setProfileLoading(true);
    setProfileError(null);
    try {
      setProfileForm(createProfileFormFromModel(detail));
    } catch (error) {
      setProfileError(getErrorMessage(error, 'Failed to load model chat behavior.'));
      setProfileForm(null);
    } finally {
      setProfileLoading(false);
    }
  }, [detail]);

  useEffect(() => {
    void loadProfile();
  }, [loadProfile]);

  const handleProfileFormChange = <K extends keyof ProfileFormState>(key: K, value: ProfileFormState[K]) => {
    setProfileForm((previous) => (previous ? { ...previous, [key]: value } : previous));
  };

  const saveProfileDocument = async () => {
    if (!profileForm) {
      return;
    }

    setDocumentSaving(true);
    setDocumentError(null);
    setDocumentMessage(null);
    try {
      const payload = buildProfileUpdateRequest(profileForm);
      await api.settings.updateModel(detail.modelId, {
        modelId: detail.modelId,
        displayName: detail.catalogModel.displayName,
        provider: detail.catalogModel.provider,
        description: detail.catalogModel.description,
        reasoningChoicesJson: deriveReasoningChoicesJson(payload.thinkingControlJson),
        runtimeConfigJson: buildLocalRuntimeConfigJson(detail),
        combineSystemAndDeveloperMessages: payload.combineSystemAndDeveloperMessages,
        thoughtBlockPattern: payload.thoughtBlockPattern ?? '',
        samplingParametersJson: payload.samplingParametersJson,
        thinkingControlJson: payload.thinkingControlJson,
        requestFieldsWhenToolsPresentJson: payload.requestFieldsWhenToolsPresentJson ?? '{}',
        isActive: detail.catalogModel.isActive,
        displayOrder: detail.catalogModel.displayOrder,
      });
      setDocumentMessage('Model chat behavior saved.');
      await onChanged?.();
    } catch (error) {
      setDocumentError(getErrorMessage(error, 'Failed to save model chat behavior.'));
    } finally {
      setDocumentSaving(false);
    }
  };

  const requestSaveProfileDocument = () => {
    if (!profileForm) {
      return;
    }
    setSaveConfirmOpen(true);
  };

  const handleLifecycleCompleted = async () => {
    await loadProfile();
    await onChanged?.();
  };

  return (
    <div data-testid="model-chat-behavior-panel" className="space-y-4 rounded border border-gray-200 bg-white p-4">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Chat behavior</h3>
        <p className="mt-1 text-xs text-gray-600">
          Model-owned chat behavior for this installation. Re-apply curator defaults with Repair or Adopt when the
          manifest changes.
        </p>
      </div>

      <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs text-gray-700">
        <dt className="text-gray-500">Model</dt>
        <dd className="font-mono">{detail.modelId}</dd>
        <dt className="text-gray-500">Display name</dt>
        <dd>{profileLoading ? 'Loading…' : profileForm?.displayName ?? detail.catalogModel.displayName}</dd>
      </dl>

      {profileError ? <p className="text-sm text-amber-800">{profileError}</p> : null}

      <div className="flex flex-wrap gap-2">
        <TextActionButton tone="neutral" onClick={() => setRepairOpen(true)} title="Repair from recorded source">
          Repair
        </TextActionButton>
        {isCurated ? (
          <TextActionButton tone="neutral" onClick={() => setAdoptOpen(true)} title="Adopt curated recipe defaults">
            Adopt curated
          </TextActionButton>
        ) : null}
      </div>

      <details className="rounded border border-gray-200 bg-gray-50 px-3 py-2">
        <summary className="cursor-pointer text-sm font-medium text-gray-800">Edit chat behavior (advanced)</summary>
        <div className="mt-3 space-y-3">
          {profileForm ? (
            <RuntimeProfileEditor
              mode="inline"
              value={profileForm}
              onChange={handleProfileFormChange}
              disableIdentityFields
              onSubmit={requestSaveProfileDocument}
              submitting={documentSaving}
              submitLabel="Save model behavior"
            />
          ) : (
            <p className="text-sm text-gray-600">Chat behavior unavailable.</p>
          )}
          {documentMessage ? <p className="text-sm text-emerald-800">{documentMessage}</p> : null}
          {documentError ? <p className="text-sm text-red-700">{documentError}</p> : null}
        </div>
      </details>

      <RepairInstallationDialog
        isOpen={repairOpen}
        detail={detail}
        onClose={() => setRepairOpen(false)}
        onCompleted={async () => {
          setRepairOpen(false);
          await handleLifecycleCompleted();
        }}
      />
      <AdoptInstallationDialog
        isOpen={adoptOpen}
        detail={detail}
        onClose={() => setAdoptOpen(false)}
        onCompleted={async () => {
          setAdoptOpen(false);
          await handleLifecycleCompleted();
        }}
      />
      <ConfirmationDialog
        isOpen={saveConfirmOpen}
        onClose={() => setSaveConfirmOpen(false)}
        onConfirm={() => {
          setSaveConfirmOpen(false);
          void saveProfileDocument();
        }}
        title="Save model behavior"
        message="Save chat behavior on this model row."
        confirmText="Save model behavior"
        confirmButtonClass="bg-blue-600 hover:bg-blue-700 text-white"
        isLoading={documentSaving}
      />
    </div>
  );
}
