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
  sharedProfileModelCount?: number;
  onChanged?: () => Promise<void>;
}

export function ModelChatBehaviorPanel({
  detail,
  sharedProfileModelCount = 1,
  onChanged,
}: ModelChatBehaviorPanelProps) {
  const [profileSummary, setProfileSummary] = useState<{ displayName: string; profileId: string } | null>(null);
  const [profileForm, setProfileForm] = useState<ProfileFormState | null>(null);
  const [profileLoading, setProfileLoading] = useState(true);
  const [profileError, setProfileError] = useState<string | null>(null);
  const [documentSaving, setDocumentSaving] = useState(false);
  const [documentMessage, setDocumentMessage] = useState<string | null>(null);
  const [documentError, setDocumentError] = useState<string | null>(null);
  const [repairOpen, setRepairOpen] = useState(false);
  const [adoptOpen, setAdoptOpen] = useState(false);
  const [sharedProfileSaveConfirmOpen, setSharedProfileSaveConfirmOpen] = useState(false);

  const isCurated = !!detail.catalogId;

  const loadProfile = useCallback(async () => {
    setProfileLoading(true);
    setProfileError(null);
    try {
      const profile = await api.settings.getRuntimeProfile(detail.runtimeProfileId);
      setProfileSummary({ displayName: profile.displayName, profileId: profile.profileId });
      setProfileForm(createProfileFormFromContractShape(profile));
    } catch (error) {
      setProfileError(getErrorMessage(error, 'Failed to load runtime profile.'));
      setProfileSummary({ displayName: detail.runtimeProfileId, profileId: detail.runtimeProfileId });
      setProfileForm(null);
    } finally {
      setProfileLoading(false);
    }
  }, [detail.runtimeProfileId]);

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
      await api.settings.updateRuntimeProfile(detail.runtimeProfileId, payload);
      setDocumentMessage('Profile document saved.');
      await onChanged?.();
    } catch (error) {
      setDocumentError(getErrorMessage(error, 'Failed to save profile document.'));
    } finally {
      setDocumentSaving(false);
    }
  };

  const requestSaveProfileDocument = () => {
    if (!profileForm) {
      return;
    }
    if (sharedProfileModelCount > 1) {
      setSharedProfileSaveConfirmOpen(true);
      return;
    }
    void saveProfileDocument();
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
          Recipe-bound runtime profile for this model. Re-apply curator defaults with Repair or Adopt when the manifest
          changes.
        </p>
      </div>

      <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs text-gray-700">
        <dt className="text-gray-500">Runtime profile</dt>
        <dd className="font-mono">{detail.runtimeProfileId}</dd>
        <dt className="text-gray-500">Display name</dt>
        <dd>{profileLoading ? 'Loading…' : profileSummary?.displayName ?? '—'}</dd>
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
        <summary className="cursor-pointer text-sm font-medium text-gray-800">Edit profile document (advanced)</summary>
        <div className="mt-3 space-y-3">
          {sharedProfileModelCount > 1 ? (
            <p className="text-xs text-amber-800">
              This updates chat behavior for all models using profile {detail.runtimeProfileId}.
            </p>
          ) : null}
          {profileForm ? (
            <RuntimeProfileEditor
              mode="inline"
              value={profileForm}
              onChange={handleProfileFormChange}
              disableIdentityFields
              onSubmit={requestSaveProfileDocument}
              submitting={documentSaving}
              submitLabel="Save profile document"
            />
          ) : (
            <p className="text-sm text-gray-600">Profile document unavailable.</p>
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
        isOpen={sharedProfileSaveConfirmOpen}
        onClose={() => setSharedProfileSaveConfirmOpen(false)}
        onConfirm={() => {
          setSharedProfileSaveConfirmOpen(false);
          void saveProfileDocument();
        }}
        title="Update shared profile"
        message={`This updates chat behavior for all models using profile ${detail.runtimeProfileId}.`}
        confirmText="Save profile document"
        confirmButtonClass="bg-blue-600 hover:bg-blue-700 text-white"
        isLoading={documentSaving}
      />
    </div>
  );
}
