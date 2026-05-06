import { useMemo, useState } from 'react';
import { AddModelWizardState, ProfileFormState } from '../../../types';
import { buildProfileCreateRequest, createEmptyProfileForm, getErrorMessage } from '../../../utils';
import { RuntimeProfileEditor } from '../../RuntimeProfileEditor';
import {
  LLAMA_MMPROJ_ROLE_ID,
  LLAMA_MODEL_ROLE_ID,
  RepositoryFilePicker,
  llamaCppClassifier,
} from '../../../editors/common';
import type { RolePickerSpec } from '../../../editors/common';
import { ProviderAddForm, ProviderEditForm } from './types';

const LLAMA_ROLES: RolePickerSpec[] = [
  {
    id: LLAMA_MODEL_ROLE_ID,
    label: 'Model file (GGUF)',
    hint: 'Exact filename from the repository. Sharded quants are not supported by llama-server.',
    required: true,
    manualPlaceholder: 'Qwen3.5-9B-Q5_K_M.gguf',
    placeholder: 'Select a GGUF file…',
  },
  {
    id: LLAMA_MMPROJ_ROLE_ID,
    label: 'Vision projector (mmproj)',
    hint: 'Leave blank for text-only models. Typically ends in mmproj-F16.gguf.',
    manualPlaceholder: 'mmproj-F16.gguf',
    placeholder: 'Select a projector file…',
  },
];

/**
 * Adapter that bridges the shared <c>RepositoryFilePicker</c> to the
 * llama-cpp Add Model wizard's flat form state. Keeps
 * <c>llamaHuggingFaceQuantIncludePattern</c> and
 * <c>llamaHuggingFaceMmprojIncludePattern</c> as the source of truth so the
 * wizard's Review step, persistence, and server payload are unchanged.
 */
function LlamaCppRepositoryPicker({
  value,
  onChange,
}: {
  value: AddModelWizardState;
  onChange: (updates: Partial<AddModelWizardState>) => void;
}) {
  const initialValues = useMemo<Record<string, string>>(
    () => ({
      [LLAMA_MODEL_ROLE_ID]: value.llamaHuggingFaceQuantIncludePattern,
      [LLAMA_MMPROJ_ROLE_ID]: value.llamaHuggingFaceMmprojIncludePattern,
    }),
    [value.llamaHuggingFaceQuantIncludePattern, value.llamaHuggingFaceMmprojIncludePattern],
  );

  return (
    <RepositoryFilePicker
      repository={value.llamaHuggingFaceRepository}
      onRepositoryChange={(next) => onChange({ llamaHuggingFaceRepository: next })}
      roles={LLAMA_ROLES}
      classify={llamaCppClassifier}
      initialValues={initialValues}
      onChange={(values) => {
        const nextQuant = values[LLAMA_MODEL_ROLE_ID] ?? '';
        const nextMmproj = values[LLAMA_MMPROJ_ROLE_ID] ?? '';
        const patch: Partial<AddModelWizardState> = {};
        if (nextQuant !== value.llamaHuggingFaceQuantIncludePattern) {
          patch.llamaHuggingFaceQuantIncludePattern = nextQuant;
        }
        if (nextMmproj !== value.llamaHuggingFaceMmprojIncludePattern) {
          patch.llamaHuggingFaceMmprojIncludePattern = nextMmproj;
        }
        if (Object.keys(patch).length > 0) {
          onChange(patch);
        }
      }}
      serviceOrigin="llamaCpp"
      repoInputHint="Paste the owner/repo shown at the top of the Hugging Face model page."
    />
  );
}

function RuntimeProfileSelect({
  value,
  onChange,
  profiles,
  profilesLoading,
}: {
  value: string;
  onChange: (value: string) => void;
  profiles: ProviderAddForm['profiles'];
  profilesLoading: boolean;
}) {
  return (
    <select
      value={value}
      onChange={(event) => onChange(event.target.value)}
      className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
      disabled={profilesLoading}
    >
      <option value="">{profilesLoading ? 'Loading profiles...' : 'Select runtime profile'}</option>
      {profiles.map((profile) => (
        <option key={profile.profileId} value={profile.profileId}>
          {profile.displayName} ({profile.profileId})
        </option>
      ))}
    </select>
  );
}

export function LlamaCppAddForm({
  value,
  onChange,
  profiles,
  profilesLoading,
  inventory,
  inventoryError,
  onCreateRuntimeProfile,
  onCreateCustomRuntimeProfile,
}: ProviderAddForm) {
  const [showCustomProfileEditor, setShowCustomProfileEditor] = useState(false);
  const [customProfileForm, setCustomProfileForm] = useState<ProfileFormState>(() => createEmptyProfileForm());
  const [creatingCustomProfile, setCreatingCustomProfile] = useState(false);
  const [customProfileError, setCustomProfileError] = useState<string | null>(null);
  const existingAliasRows = inventory.filter((row) => row.catalogModelIds.length === 0 && row.hasModelFile && row.hasMmprojFile);
  const selectedAttachAlias = value.llamaExistingAliasRouterModelId.trim();
  const selectedAttachRow = existingAliasRows.find((row) => row.routerModelId === selectedAttachAlias);
  const chosenHfAlias = value.llamaRouterModelId.trim();
  const chosenAliasRow = inventory.find((row) => row.routerModelId === chosenHfAlias);
  const chosenAliasTaken = !!chosenAliasRow && chosenAliasRow.catalogModelIds.length > 0;
  const llamaRuntimeUnavailable =
    typeof inventoryError === 'string' &&
    (inventoryError.toLowerCase().includes('no local llama server')
      || inventoryError.includes('127.0.0.1:9')
      || inventoryError.toLowerCase().includes('connection refused'));
  const selectedProfile = profiles.find((profile) => profile.profileId === value.runtimeProfileId.trim());
  const derivedReasoningChoices = (() => {
    if (!selectedProfile?.thinkingControlJson) {
      return [];
    }
    try {
      const parsed = JSON.parse(selectedProfile.thinkingControlJson) as { choiceActions?: Record<string, unknown> };
      if (!parsed.choiceActions || typeof parsed.choiceActions !== 'object') {
        return [];
      }
      return Object.keys(parsed.choiceActions).filter((entry) => entry.trim().length > 0);
    } catch {
      return [];
    }
  })();

  const handleCreateCustomProfile = async () => {
    if (!onCreateCustomRuntimeProfile) {
      return;
    }
    setCreatingCustomProfile(true);
    setCustomProfileError(null);
    try {
      const request = buildProfileCreateRequest(customProfileForm);
      const created = await onCreateCustomRuntimeProfile(request);
      onChange({ runtimeProfileId: created.profileId });
      setShowCustomProfileEditor(false);
      setCustomProfileForm(createEmptyProfileForm());
    } catch (error) {
      setCustomProfileError(getErrorMessage(error, 'Failed to create runtime profile.'));
    } finally {
      setCreatingCustomProfile(false);
    }
  };

  return (
    <div className="space-y-4">
      {llamaRuntimeUnavailable ? (
        <div className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-900">
          No local llama server is configured for this container yet. Live alias adoption and runtime inventory stay
          unavailable until one is added.
        </div>
      ) : null}

      <div className="space-y-2">
        <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Install Source</label>
        <select
          value={value.llamaInstallSource}
          onChange={(event) => onChange({ llamaInstallSource: event.target.value as 'huggingface' | 'existingAlias' })}
          className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
        >
          <option value="huggingface">Install from Hugging Face</option>
          <option value="existingAlias">Attach existing alias</option>
        </select>
      </div>

      <div className="space-y-2">
        <div className="flex items-center justify-between gap-2">
          <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Runtime Profile</label>
          {onCreateRuntimeProfile ? (
            <div className="flex flex-wrap gap-1">
              <button
                type="button"
                onClick={() => void onCreateRuntimeProfile('qwen3_5')}
                className="rounded border border-gray-300 px-2 py-1 text-xs text-gray-700 hover:bg-gray-50"
              >
                Insert qwen3_5
              </button>
              <button
                type="button"
                onClick={() => void onCreateRuntimeProfile('qwen3_6')}
                className="rounded border border-gray-300 px-2 py-1 text-xs text-gray-700 hover:bg-gray-50"
              >
                Insert qwen3_6
              </button>
              <button
                type="button"
                onClick={() => void onCreateRuntimeProfile('gemma4')}
                className="rounded border border-gray-300 px-2 py-1 text-xs text-gray-700 hover:bg-gray-50"
              >
                Insert gemma4
              </button>
              {onCreateCustomRuntimeProfile ? (
                <button
                  type="button"
                  onClick={() => setShowCustomProfileEditor((previous) => !previous)}
                  className="rounded border border-gray-300 px-2 py-1 text-xs text-gray-700 hover:bg-gray-50"
                >
                  {showCustomProfileEditor ? 'Hide custom' : 'Create custom'}
                </button>
              ) : null}
            </div>
          ) : null}
        </div>
        <RuntimeProfileSelect
          value={value.runtimeProfileId}
          onChange={(nextValue) => onChange({ runtimeProfileId: nextValue })}
          profiles={profiles}
          profilesLoading={profilesLoading}
        />
        {value.runtimeProfileId.trim().length > 0 ? (
          <div className="rounded border border-gray-200 bg-gray-50 px-3 py-2 text-xs text-gray-700">
            {derivedReasoningChoices.length > 0 ? (
              <span>
                Reasoning choices exposed by this profile: <span className="font-mono">[{derivedReasoningChoices.join(', ')}]</span>
              </span>
            ) : (
              <span>This profile exposes no reasoning choices; the catalog row ReasoningChoicesJson will be null.</span>
            )}
          </div>
        ) : null}
      </div>

      <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
        <div className="space-y-1">
          <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Context size (tokens)</label>
          <input
            type="text"
            inputMode="numeric"
            value={value.llamaRouterContextSize}
            onChange={(event) => onChange({ llamaRouterContextSize: event.target.value })}
            className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            autoComplete="off"
            spellCheck={false}
            placeholder="(container default)"
          />
          <p className="text-[11px] text-gray-500">
            Optional. Per-alias <code className="font-mono">-c</code> in <code className="font-mono">router-models.ini</code>. Blank
            uses the container default (e.g. <code className="font-mono">GA_LLAMA_CTX_SIZE</code>).
          </p>
        </div>
        <div className="space-y-1">
          <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Prompt cache RAM (MiB)</label>
          <input
            type="text"
            inputMode="numeric"
            value={value.llamaRouterCacheRamMib}
            onChange={(event) => onChange({ llamaRouterCacheRamMib: event.target.value })}
            className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            autoComplete="off"
            spellCheck={false}
            placeholder="(container default)"
          />
          <p className="text-[11px] text-gray-500">
            Optional. Per-alias prompt cache size (MiB) for this router entry. Changing these may restart llama-server to take
            effect.
          </p>
        </div>
      </div>

      {showCustomProfileEditor && onCreateCustomRuntimeProfile ? (
        <div className="rounded border border-gray-200 bg-gray-50 p-3">
          <RuntimeProfileEditor
            mode="inline"
            value={customProfileForm}
            onChange={(key, nextValue) =>
              setCustomProfileForm((previous) => ({
                ...previous,
                [key]: nextValue,
              }))
            }
            onInsertTemplate={(template) => {
              const templates: Record<'qwen3_5' | 'qwen3_6' | 'gemma4', Partial<ProfileFormState>> = {
                qwen3_5: {
                  profileId: 'qwen3_5',
                  displayName: 'Qwen 3.5',
                },
                qwen3_6: {
                  profileId: 'qwen3_6',
                  displayName: 'Qwen 3.6',
                },
                gemma4: {
                  profileId: 'gemma4',
                  displayName: 'Gemma 4',
                },
              };
              const patch = templates[template];
              setCustomProfileForm((previous) => ({
                ...previous,
                ...patch,
              }));
            }}
            onSubmit={() => void handleCreateCustomProfile()}
            submitting={creatingCustomProfile}
            submitLabel="Create profile"
          />
          {customProfileError ? <p className="mt-2 text-xs text-red-700">{customProfileError}</p> : null}
        </div>
      ) : null}

      {value.llamaInstallSource === 'existingAlias' ? (
        <div className="space-y-2">
          <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Existing Alias</label>
          <select
            value={value.llamaExistingAliasRouterModelId}
            onChange={(event) =>
              onChange({
                llamaExistingAliasRouterModelId: event.target.value,
              })
            }
            className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            disabled={llamaRuntimeUnavailable}
          >
            <option value="">{llamaRuntimeUnavailable ? 'No live aliases available' : 'Select orphaned alias'}</option>
            {existingAliasRows.map((row) => (
              <option key={row.routerModelId} value={row.routerModelId}>
                {row.routerModelId}
              </option>
            ))}
          </select>
          {llamaRuntimeUnavailable ? (
            <p className="text-xs text-amber-700">Attach existing alias requires a configured local llama server.</p>
          ) : selectedAttachAlias && !selectedAttachRow ? (
            <p className="text-xs text-amber-700">Choose an alias with model/mmproj files and no catalog model bindings.</p>
          ) : null}
        </div>
      ) : (
        <>
          <div className="space-y-2">
            <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Router Alias</label>
            <input
              type="text"
              value={value.llamaRouterModelId}
              onChange={(event) => onChange({ llamaRouterModelId: event.target.value })}
              className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              autoComplete="off"
              spellCheck={false}
            />
            {chosenAliasTaken ? (
              <p className="text-xs text-amber-700">Alias already has catalog rows. Pick a different alias or use Attach existing alias.</p>
            ) : null}
          </div>

          <div className="space-y-3">
            <LlamaCppRepositoryPicker value={value} onChange={onChange} />

            <div className="space-y-2">
              <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Target Directory</label>
              <input
                type="text"
                value={value.llamaHuggingFaceTargetDirectory}
                onChange={(event) => onChange({ llamaHuggingFaceTargetDirectory: event.target.value })}
                className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                autoComplete="off"
                spellCheck={false}
              />
              <p className="text-[11px] text-gray-500">
                Folder under <span className="font-mono">/models-local/llama</span> where the files will be written. Typically matches the Router Alias.
              </p>
            </div>
          </div>
        </>
      )}
    </div>
  );
}

export function LlamaCppEditForm({ value, onChange, profiles, inventory }: Omit<ProviderEditForm, 'profilesLoading'> & { profilesLoading?: boolean }) {
  const [showAdvanced, setShowAdvanced] = useState(false);
  const routerAlias = value.localRuntimeRouterModelId.trim();
  const inventoryRow = inventory?.find((row) => row.routerModelId === routerAlias);
  const otherCatalogRowsOnAlias = inventoryRow?.catalogModelIds.filter((id) => id !== value.modelId) ?? [];
  const selectedProfile = profiles.find((profile) => profile.profileId === value.runtimeProfileId.trim());
  const derivedReasoningChoices = (() => {
    if (!selectedProfile?.thinkingControlJson) {
      return [];
    }
    try {
      const parsed = JSON.parse(selectedProfile.thinkingControlJson) as { choiceActions?: Record<string, unknown> };
      if (!parsed.choiceActions || typeof parsed.choiceActions !== 'object') {
        return [];
      }
      return Object.keys(parsed.choiceActions).filter((entry) => entry.trim().length > 0);
    } catch {
      return [];
    }
  })();

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
        <div className="space-y-1">
          <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Router Alias</label>
          <input
            type="text"
            value={value.localRuntimeRouterModelId}
            disabled
            className="w-full rounded border border-gray-300 bg-gray-100 px-3 py-2 font-mono text-sm text-gray-600"
            title="Router alias is identity. Delete + re-add the catalog row to rebind to a different alias."
          />
          <p className="text-[11px] text-gray-500">Identity. Delete + re-add to rebind.</p>
        </div>
      </div>

      <div className="rounded border border-gray-200 bg-gray-50 px-3 py-2 text-xs text-gray-700">
        <div className="mb-1 font-medium uppercase tracking-wide text-gray-600">Backing artifacts (live inventory)</div>
        {inventoryRow ? (
          <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 font-mono">
            <dt className="text-gray-500">runtime state</dt>
            <dd className="text-gray-900">{inventoryRow.runtimeState}</dd>
            <dt className="text-gray-500">GGUF</dt>
            <dd className={inventoryRow.hasModelFile ? 'text-gray-900' : 'text-red-700'}>
              {inventoryRow.modelPath ?? '(missing)'}{inventoryRow.hasModelFile ? '' : ' — not on disk'}
            </dd>
            <dt className="text-gray-500">mmproj</dt>
            <dd className={inventoryRow.hasMmprojFile ? 'text-gray-900' : 'text-red-700'}>
              {inventoryRow.mmprojPath ?? '(missing)'}{inventoryRow.hasMmprojFile ? '' : ' — not on disk'}
            </dd>
            {otherCatalogRowsOnAlias.length > 0 ? (
              <>
                <dt className="text-gray-500">also used by</dt>
                <dd className="text-gray-900">{otherCatalogRowsOnAlias.join(', ')}</dd>
              </>
            ) : null}
            <dt className="text-gray-500">notebook refs</dt>
            <dd className="text-gray-900">{inventoryRow.notebookReferenceCount}</dd>
            {inventoryRow.routerContextSize != null ? (
              <>
                <dt className="text-gray-500">context (router ini)</dt>
                <dd className="text-gray-900">{inventoryRow.routerContextSize}</dd>
              </>
            ) : null}
            {inventoryRow.routerCacheRamMib != null ? (
              <>
                <dt className="text-gray-500">cache RAM MiB (router ini)</dt>
                <dd className="text-gray-900">{inventoryRow.routerCacheRamMib}</dd>
              </>
            ) : null}
          </dl>
        ) : (
          <p className="text-amber-700">
            No live router entry for alias <span className="font-mono">{routerAlias || '(unset)'}</span>. The catalog row
            points at an alias that no longer exists.
          </p>
        )}
      </div>

      {value.runtimeProfileId.trim().length > 0 ? (
        <div className="rounded border border-gray-200 bg-gray-50 px-3 py-2 text-xs text-gray-700">
          {derivedReasoningChoices.length > 0 ? (
            <span>
              Reasoning choices exposed by this profile:{' '}
              <span className="font-mono">[{derivedReasoningChoices.join(', ')}]</span>
            </span>
          ) : (
            <span>This profile exposes no reasoning choices; the catalog row ReasoningChoicesJson will be null.</span>
          )}
        </div>
      ) : null}

      <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
        <div className="space-y-1">
          <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Context size (tokens)</label>
          <input
            type="text"
            inputMode="numeric"
            value={value.localRuntimeRouterContextSize}
            onChange={(event) => onChange({ localRuntimeRouterContextSize: event.target.value })}
            className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            autoComplete="off"
            spellCheck={false}
            placeholder="(container default)"
          />
          <p className="text-[11px] text-gray-500">
            Saved in RuntimeConfigJson and synced to <code className="font-mono">router-models.ini</code>. Clear the field to
            remove the override.
          </p>
        </div>
        <div className="space-y-1">
          <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Prompt cache RAM (MiB)</label>
          <input
            type="text"
            inputMode="numeric"
            value={value.localRuntimeRouterCacheRamMib}
            onChange={(event) => onChange({ localRuntimeRouterCacheRamMib: event.target.value })}
            className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            autoComplete="off"
            spellCheck={false}
            placeholder="(container default)"
          />
          <p className="text-[11px] text-gray-500">Per-alias prompt cache size. Changing this may restart llama-server.</p>
        </div>
      </div>

      <div className="border-t border-gray-200 pt-3">
        <button
          type="button"
          className="text-xs font-medium text-blue-700 hover:underline"
          onClick={() => setShowAdvanced((previous) => !previous)}
        >
          {showAdvanced ? 'Hide advanced' : 'Show advanced (load params, parallel tool calls)'}
        </button>
        {showAdvanced ? (
          <div className="mt-3 space-y-3">
            <div className="space-y-2">
              <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Load params JSON</label>
              <textarea
                value={value.localRuntimeLoadParamsJson}
                onChange={(event) => onChange({ localRuntimeLoadParamsJson: event.target.value })}
                rows={5}
                className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-xs text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                spellCheck={false}
                placeholder={`{\n  "model": "${routerAlias || '<alias>'}"\n}`}
              />
              <p className="text-[11px] text-gray-500">
                Forwarded verbatim to <code className="font-mono">llama-server</code> on load. Blank → derived default{' '}
                <code className="font-mono">{`{"model": "<alias>"}`}</code>.
              </p>
            </div>
            <label className="inline-flex items-center gap-2 text-sm text-gray-700">
              <input
                type="checkbox"
                checked={value.localRuntimeParallelToolCalls}
                onChange={(event) => onChange({ localRuntimeParallelToolCalls: event.target.checked })}
                className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
              />
              Allow parallel tool calls
            </label>
          </div>
        ) : null}
      </div>
    </div>
  );
}
