import { useRef, useState } from 'react';
import { FaDownload, FaEdit, FaPlus, FaRedo, FaSpinner, FaTrash, FaUpload } from 'react-icons/fa';
import LoadingSpinner from '../../../components/LoadingSpinner';
import { SettingsRuntimeProfileDto } from '../../../types/settings';
import { ProfileFormState } from '../types';
import { exportRuntimeProfile, formatDateTime, getErrorMessage, importRuntimeProfile } from '../utils';
import { IconActionButton, TextActionButton } from './shared/ActionButtons';
import { RuntimeProfileDialog } from './RuntimeProfileDialog';

interface ProfilesTabProps {
  profileDialogOpen: boolean;
  editingProfileId: string | null;
  profileForm: ProfileFormState;
  profileSaving: boolean;
  profilesLoading: boolean;
  profilesError: string | null;
  profiles: SettingsRuntimeProfileDto[];
  deletingProfileId: string | null;
  onProfileFormChange: <K extends keyof ProfileFormState>(key: K, value: ProfileFormState[K]) => void;
  onOpenCreateProfile: () => void;
  onImportProfile: (form: ProfileFormState) => void;
  onResetProfileForm: () => void;
  onSaveProfile: () => void;
  onRetryLoadProfiles: () => void;
  onEditProfile: (profile: SettingsRuntimeProfileDto) => void;
  onRequestDeleteProfile: (profileId: string) => void;
  onInsertTemplate: (template: 'qwen3_5' | 'qwen3_6' | 'gemma4') => void;
}

export function ProfilesTab({
  profileDialogOpen,
  editingProfileId,
  profileForm,
  profileSaving,
  profilesLoading,
  profilesError,
  profiles,
  deletingProfileId,
  onProfileFormChange,
  onOpenCreateProfile,
  onImportProfile,
  onResetProfileForm,
  onSaveProfile,
  onRetryLoadProfiles,
  onEditProfile,
  onRequestDeleteProfile,
  onInsertTemplate,
}: ProfilesTabProps) {
  const importInputRef = useRef<HTMLInputElement | null>(null);
  const [importError, setImportError] = useState<string | null>(null);

  const handleExportProfile = (profile: SettingsRuntimeProfileDto) => {
    const json = exportRuntimeProfile(profile);
    const blob = new Blob([json], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `runtime-profile-${profile.profileId}.json`;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  };

  const handleImportSelection = async (file: File | null) => {
    if (!file) {
      return;
    }
    try {
      const text = await file.text();
      const form = importRuntimeProfile(text);
      setImportError(null);
      onImportProfile(form);
    } catch (error) {
      setImportError(getErrorMessage(error, 'Failed to import runtime profile.'));
    }
  };

  return (
    <>
      <section className="rounded-lg border border-gray-200 bg-white">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-gray-200 px-6 py-4">
          <div>
            <h2 className="text-base font-semibold text-gray-900">Runtime Profiles</h2>
            <p className="mt-1 text-sm text-gray-600">
              Runtime profiles define sampling parameters and behavior for llama-cpp models.
            </p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <input
              ref={importInputRef}
              type="file"
              accept=".json,application/json"
              className="hidden"
              onChange={(event) => {
                void handleImportSelection(event.target.files?.[0] ?? null);
                event.currentTarget.value = '';
              }}
            />
            <TextActionButton
              tone="neutral"
              icon={<FaUpload />}
              onClick={() => importInputRef.current?.click()}
              title="Import runtime profile JSON"
            >
              Import
            </TextActionButton>
            <TextActionButton
              tone="primary"
              icon={<FaPlus />}
              onClick={() => {
                setImportError(null);
                onOpenCreateProfile();
              }}
              title="Open Add Runtime Profile dialog"
            >
              Add Profile
            </TextActionButton>
          </div>
        </div>
        {importError ? (
          <div className="px-6 pt-4">
            <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{importError}</div>
          </div>
        ) : null}

        {profilesLoading ? (
          <div className="px-6 py-8">
            <LoadingSpinner message="Loading runtime profiles..." />
          </div>
        ) : profilesError ? (
          <div className="px-6 py-4">
            <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{profilesError}</div>
            <div className="mt-3">
              <TextActionButton tone="neutral" icon={<FaRedo />} onClick={onRetryLoadProfiles} title="Retry loading profiles.">
                Retry
              </TextActionButton>
            </div>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-gray-200">
              <thead className="bg-gray-50">
                <tr>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Profile ID</th>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Display Name</th>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Updated</th>
                  <th className="px-4 py-3 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Actions</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200 bg-white">
                {profiles.map((profile) => (
                  <tr key={profile.profileId}>
                    <td className="whitespace-nowrap px-4 py-3 text-sm font-mono text-gray-900">{profile.profileId}</td>
                    <td className="px-4 py-3 text-sm text-gray-900">{profile.displayName}</td>
                    <td className="px-4 py-3 text-sm text-gray-700">{formatDateTime(profile.updated ?? profile.created)}</td>
                    <td className="px-4 py-3 text-right text-sm">
                      <div
                        className="flex items-center justify-end gap-1.5"
                        role="group"
                        aria-label={`Actions for profile ${profile.displayName}`}
                      >
                        <IconActionButton
                          label="Export"
                          tone="neutral"
                          icon={<FaDownload />}
                          onClick={() => handleExportProfile(profile)}
                          title={`Export profile ${profile.displayName}`}
                        />
                        <IconActionButton
                          label="Edit"
                          tone="info"
                          icon={<FaEdit />}
                          onClick={() => onEditProfile(profile)}
                          title={`Edit profile ${profile.displayName}`}
                        />
                        <IconActionButton
                          label="Delete"
                          tone="danger"
                          icon={deletingProfileId === profile.profileId ? <FaSpinner className="animate-spin" /> : <FaTrash />}
                          disabled={deletingProfileId === profile.profileId}
                          onClick={() => onRequestDeleteProfile(profile.profileId)}
                          title={`Delete profile ${profile.displayName}`}
                        />
                      </div>
                    </td>
                  </tr>
                ))}
                {profiles.length === 0 && (
                  <tr>
                    <td colSpan={4} className="px-4 py-8 text-center text-sm text-gray-600">
                      No runtime profiles configured yet.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <RuntimeProfileDialog
        isOpen={profileDialogOpen}
        editingProfileId={editingProfileId}
        value={profileForm}
        submitting={profileSaving}
        onChange={onProfileFormChange}
        onInsertTemplate={onInsertTemplate}
        onClose={onResetProfileForm}
        onSubmit={onSaveProfile}
      />
    </>
  );
}
