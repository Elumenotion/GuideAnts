import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent,
} from 'react';
import { api } from '../../../../services/api';
import type {
  HuggingFaceRepositoryFileDto,
  HuggingFaceRepositoryListingDto,
} from '../../../../types/settings';
import type {
  ClassifyFn,
  ClassifyPreviewFn,
  RoleCandidate,
  RoleClassification,
  RolePickerSpec,
  SnapshotPreview,
} from './repositoryPickerTypes';
import { formatBytes, normalizeHuggingFaceRepository } from './repositoryPickerUtils';

/**
 * Shared Hugging Face repository file picker used by every HF-backed
 * service. Renders:
 *   - a controlled <c>owner/repo</c> input with Browse/Refresh action
 *   - either per-role dropdowns (per-file mode) or a read-only snapshot
 *     preview (preview-only mode)
 *   - inline error with <c>role="alert"</c> so screen readers announce it
 *
 * The component intentionally has no service-specific heuristics. Callers
 * supply a {@link ClassifyFn} (per-file) or {@link ClassifyPreviewFn}
 * (preview-only) that turns the raw listing into the picker's view model.
 *
 * Cancellation: an <c>AbortController</c> is created per browse, and
 * pressing Esc while focus is inside the repo input aborts the in-flight
 * browse — the same pattern across llama-cpp, SD, ASR, TTS, and
 * Embeddings.
 */
export interface RepositoryFilePickerProps {
  /** Controlled <c>owner/repo</c> value. */
  repository: string;
  /** Emitted as the operator types or when a URL is normalized. */
  onRepositoryChange(next: string): void;
  /** Per-role specs. Required unless {@link previewOnly} is true. */
  roles?: RolePickerSpec[];
  /** Classifier that turns the repository listing into per-role dropdowns. */
  classify?: ClassifyFn;
  /** Emitted whenever one of the role selections changes. */
  onChange?: (values: Record<string, string>) => void;
  /** Initial per-role values when resuming an existing flow. */
  initialValues?: Record<string, string>;
  /** Allow operators to flip to free-text per-role inputs. Default true. */
  manualFallbackEnabled?: boolean;
  /** When true, renders a read-only snapshot preview instead of dropdowns. */
  previewOnly?: boolean;
  /** Classifier that produces the preview-only buckets. */
  classifyPreview?: ClassifyPreviewFn;
  /** Optional callback invoked after a successful browse. */
  onBrowseResolved?: (listing: HuggingFaceRepositoryListingDto) => void;
  /** Optional callback reflecting browse error state (null on success). */
  onBrowseError?: (error: { code: string | null; message: string } | null) => void;
  /** Sent as the <c>X-Service-Origin</c> header for telemetry. */
  serviceOrigin?: string;
  /** Label for the repo input. Defaults to "From Hugging Face". */
  repoInputLabel?: string;
  /** Placeholder for the repo input. */
  repoInputPlaceholder?: string;
  /** Optional hint rendered under the repo input. */
  repoInputHint?: string;
  /** Disable all inputs (e.g. when the parent is submitting). */
  disabled?: boolean;
}

interface BrowseError {
  code: string | null;
  message: string;
}

const DEFAULT_REPO_PLACEHOLDER = 'unsloth/Qwen3.6-35B-A3B-GGUF';
const DEFAULT_REPO_HINT = 'Paste owner/repo or a full huggingface.co model URL.';

function describeCandidate(candidate: RoleCandidate): string {
  const size = formatBytes(candidate.size ?? null);
  const pieces = [candidate.path, size];
  if (candidate.detail) {
    pieces.push(candidate.detail);
  }
  if (candidate.badges && candidate.badges.length > 0) {
    pieces.push(candidate.badges.join(', '));
  }
  const suffix = candidate.disabled && candidate.disabledReason ? ` (${candidate.disabledReason})` : '';
  return `${pieces.join(' · ')}${suffix}`;
}

export function RepositoryFilePicker({
  repository,
  onRepositoryChange,
  roles,
  classify,
  onChange,
  initialValues,
  manualFallbackEnabled = true,
  previewOnly = false,
  classifyPreview,
  onBrowseResolved,
  onBrowseError,
  serviceOrigin,
  repoInputLabel = '🤗 From Hugging Face',
  repoInputPlaceholder = DEFAULT_REPO_PLACEHOLDER,
  repoInputHint = DEFAULT_REPO_HINT,
  disabled = false,
}: RepositoryFilePickerProps) {
  const [listing, setListing] = useState<HuggingFaceRepositoryListingDto | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setErrorState] = useState<BrowseError | null>(null);
  const [manualMode, setManualMode] = useState(false);
  const [values, setValues] = useState<Record<string, string>>(() => ({ ...(initialValues ?? {}) }));

  const abortRef = useRef<AbortController | null>(null);
  const onBrowseErrorRef = useRef(onBrowseError);
  const onBrowseResolvedRef = useRef(onBrowseResolved);
  const onChangeRef = useRef(onChange);

  useEffect(() => {
    onBrowseErrorRef.current = onBrowseError;
    onBrowseResolvedRef.current = onBrowseResolved;
    onChangeRef.current = onChange;
  }, [onBrowseError, onBrowseResolved, onChange]);

  const setError = useCallback((next: BrowseError | null) => {
    setErrorState(next);
    onBrowseErrorRef.current?.(next);
  }, []);

  // Re-seed values when initialValues changes identity (e.g. parent rehydrated
  // from sessionStorage). Shallow compare keys/values so steady-state renders
  // do not reset operator edits.
  useEffect(() => {
    if (!initialValues) {
      return;
    }
    setValues((previous) => {
      const nextKeys = Object.keys(initialValues);
      const sameLength = nextKeys.length === Object.keys(previous).length;
      const sameEntries = sameLength && nextKeys.every((key) => previous[key] === initialValues[key]);
      if (sameEntries) {
        return previous;
      }
      return { ...initialValues };
    });
  }, [initialValues]);

  useEffect(() => {
    return () => {
      abortRef.current?.abort();
      abortRef.current = null;
    };
  }, []);

  const emitChange = useCallback((next: Record<string, string>) => {
    setValues(next);
    onChangeRef.current?.(next);
  }, []);

  const browse = useCallback(async () => {
    if (disabled) {
      return;
    }
    const normalized = normalizeHuggingFaceRepository(repository);
    if (!normalized) {
      setError({
        code: 'REPO_INVALID',
        message: 'Enter a Hugging Face repository in the form "owner/repo" first.',
      });
      return;
    }
    if (normalized !== repository.trim()) {
      // Reflect the normalized owner/repo form back into the parent so the
      // repo input shows the canonical slug after pasting a URL.
      onRepositoryChange(normalized);
    }
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;
    setLoading(true);
    setError(null);
    try {
      const result = await api.settings.browseHuggingFaceRepository(normalized, {
        serviceOrigin,
        signal: controller.signal,
      });
      if (controller.signal.aborted) {
        return;
      }
      setListing(result);
      onBrowseResolvedRef.current?.(result);
      // Apply classifier auto-selects only when the operator has not already
      // made a choice for that role. This keeps "Refresh file list" idempotent.
      if (!previewOnly && classify && roles && roles.length > 0) {
        const classification = classify(result.files, { repository: normalized, listing: result });
        const next = { ...values };
        let mutated = false;
        for (const role of roles) {
          const current = next[role.id] ?? '';
          const auto = classification[role.id]?.autoSelect ?? null;
          if (!current.trim() && auto) {
            next[role.id] = auto;
            mutated = true;
          }
        }
        if (mutated) {
          emitChange(next);
        }
      }
    } catch (e) {
      if (controller.signal.aborted) {
        return;
      }
      const status = (e as { status?: number }).status;
      const code = (e as { code?: string }).code ?? null;
      const message = (e as { message?: string }).message ?? 'Failed to list repository files.';
      setError({ code, message });
      if (
        !previewOnly
        && manualFallbackEnabled
        && (code === 'REPO_TOKEN_MISSING' || code === 'REPO_TOKEN_INSUFFICIENT' || status === 401 || status === 403)
      ) {
        setManualMode(true);
      }
    } finally {
      if (abortRef.current === controller) {
        abortRef.current = null;
      }
      setLoading(false);
    }
  }, [
    classify,
    disabled,
    emitChange,
    manualFallbackEnabled,
    previewOnly,
    repository,
    roles,
    serviceOrigin,
    setError,
    values,
    onRepositoryChange,
  ]);

  const handleRepoKey = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      void browse();
      return;
    }
    if (event.key === 'Escape' && abortRef.current) {
      event.preventDefault();
      abortRef.current.abort();
      abortRef.current = null;
      setLoading(false);
    }
  };

  const classification = useMemo<Record<string, RoleClassification>>(() => {
    if (!listing || previewOnly || !classify || !roles) {
      return {};
    }
    return classify(listing.files, { repository: listing.repository, listing });
  }, [listing, previewOnly, classify, roles]);

  const preview: SnapshotPreview | null = useMemo(() => {
    if (!listing || !previewOnly || !classifyPreview) {
      return null;
    }
    return classifyPreview(listing.files, { repository: listing.repository, listing });
  }, [listing, previewOnly, classifyPreview]);

  const setRoleValue = useCallback(
    (roleId: string, next: string) => {
      emitChange({ ...values, [roleId]: next });
    },
    [emitChange, values],
  );

  const hasRoles = !previewOnly && roles && roles.length > 0;

  return (
    <div className="space-y-3 rounded border border-gray-200 bg-gray-50 p-3">
      <div className="space-y-1">
        <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">
          {repoInputLabel}
        </label>
        <input
          type="text"
          value={repository}
          onChange={(event) => onRepositoryChange(event.target.value)}
          onKeyDown={handleRepoKey}
          placeholder={repoInputPlaceholder}
          disabled={disabled}
          autoComplete="off"
          spellCheck={false}
          className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 disabled:bg-gray-100"
        />
        {repoInputHint ? <p className="text-[11px] text-gray-500">{repoInputHint}</p> : null}
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <button
          type="button"
          onClick={() => void browse()}
          disabled={disabled || loading || !repository.trim()}
          className="rounded bg-blue-600 px-3 py-1.5 text-xs font-medium text-white disabled:cursor-not-allowed disabled:bg-gray-300"
        >
          {loading ? 'Listing files…' : listing ? 'Refresh file list' : 'Browse repository'}
        </button>
        {loading ? (
          <span className="text-[11px] text-gray-500">Press Esc to cancel.</span>
        ) : null}
        {listing ? (
          <span className="text-xs text-gray-600">
            Found {listing.files.length} files in{' '}
            <a
              href={listing.modelCardUrl ?? `https://huggingface.co/${listing.repository}`}
              target="_blank"
              rel="noreferrer"
              className="font-mono text-blue-700 hover:underline"
            >
              {listing.repository}
            </a>
            {listing.tokenUsed ? ' · HF token applied' : null}
          </span>
        ) : null}
        {hasRoles && manualFallbackEnabled ? (
          <button
            type="button"
            onClick={() => setManualMode((previous) => !previous)}
            disabled={disabled}
            className="ml-auto text-xs text-blue-700 hover:underline disabled:text-gray-400"
          >
            {manualMode ? 'Use file picker' : 'Enter filename manually'}
          </button>
        ) : null}
      </div>

      {error ? (
        <div role="alert" className="rounded border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-800">
          <div className="font-medium">{error.message}</div>
          {error.code === 'REPO_TOKEN_MISSING' ? (
            <div className="mt-1 text-red-700">
              Configure a Hugging Face token under <span className="font-mono">Settings → Hugging Face</span> and
              try again. Public repos do not need a token.
            </div>
          ) : null}
          {error.code === 'REPO_TOKEN_INSUFFICIENT' ? (
            <div className="mt-1 text-red-700">
              The configured token is valid but does not have access to this repo. Accept the terms on the Hugging
              Face model page or rotate the token.
            </div>
          ) : null}
          {error.code === 'REPO_NOT_FOUND' ? (
            <div className="mt-1 text-red-700">
              Double-check the owner/name. Example: <span className="font-mono">{DEFAULT_REPO_PLACEHOLDER}</span>.
            </div>
          ) : null}
        </div>
      ) : null}

      {previewOnly ? (
        <SnapshotPreviewTable listing={listing} preview={preview} />
      ) : hasRoles ? (
        manualMode ? (
          <RoleManualGrid
            roles={roles!}
            values={values}
            onChange={setRoleValue}
            disabled={disabled}
          />
        ) : listing ? (
          <RoleDropdownGrid
            roles={roles!}
            classification={classification}
            values={values}
            onChange={setRoleValue}
            disabled={disabled}
          />
        ) : (
          <p className="text-xs text-gray-600">
            Paste a Hugging Face repository above and click Browse to pick files.
          </p>
        )
      ) : null}
    </div>
  );
}

function RoleManualGrid({
  roles,
  values,
  onChange,
  disabled,
}: {
  roles: RolePickerSpec[];
  values: Record<string, string>;
  onChange(roleId: string, next: string): void;
  disabled: boolean;
}) {
  return (
    <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
      {roles.map((role) => (
        <div key={role.id} className="space-y-1">
          <label className="block text-xs font-medium uppercase tracking-wide text-gray-600" htmlFor={`role-manual-${role.id}`}>
            {role.label}
          </label>
          <input
            id={`role-manual-${role.id}`}
            type="text"
            value={values[role.id] ?? ''}
            onChange={(event) => onChange(role.id, event.target.value)}
            placeholder={role.manualPlaceholder}
            disabled={disabled}
            autoComplete="off"
            spellCheck={false}
            className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 disabled:bg-gray-100"
          />
          {role.hint ? <p className="text-[11px] text-gray-500">{role.hint}</p> : null}
        </div>
      ))}
    </div>
  );
}

function RoleDropdownGrid({
  roles,
  classification,
  values,
  onChange,
  disabled,
}: {
  roles: RolePickerSpec[];
  classification: Record<string, RoleClassification>;
  values: Record<string, string>;
  onChange(roleId: string, next: string): void;
  disabled: boolean;
}) {
  const gridClass = roles.length === 1 ? 'grid grid-cols-1 gap-3' : 'grid grid-cols-1 gap-3 md:grid-cols-2';
  return (
    <div className={gridClass}>
      {roles.map((role) => {
        const result = classification[role.id];
        const candidates = result?.candidates ?? [];
        const selected = values[role.id] ?? '';
        const candidateMatch = candidates.find((c) => c.path === selected);
        return (
          <div key={role.id} className="space-y-1">
            <label className="block text-xs font-medium uppercase tracking-wide text-gray-600" htmlFor={`role-select-${role.id}`}>
              {role.label}
            </label>
            <select
              id={`role-select-${role.id}`}
              value={selected}
              onChange={(event) => onChange(role.id, event.target.value)}
              disabled={disabled || candidates.length === 0}
              className="w-full rounded border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 disabled:bg-gray-100 disabled:text-gray-500"
            >
              <option value="">
                {candidates.length === 0 ? 'No candidate files' : role.placeholder ?? 'Select a file…'}
              </option>
              {/*
               * Preserve whatever the parent passed in even when the listing
               * no longer contains it (e.g. manual value from a previous run)
               * so the selection is not silently dropped on re-render.
               */}
              {selected && !candidateMatch ? (
                <option value={selected}>{selected} (current selection)</option>
              ) : null}
              {candidates.map((candidate) => (
                <option key={candidate.path} value={candidate.path} disabled={candidate.disabled}>
                  {describeCandidate(candidate)}
                </option>
              ))}
            </select>
            {role.hint ? <p className="text-[11px] text-gray-500">{role.hint}</p> : null}
            {result?.emptyMessage && candidates.length === 0 ? (
              <p className="text-[11px] text-amber-700">{result.emptyMessage}</p>
            ) : null}
          </div>
        );
      })}
    </div>
  );
}

function SnapshotPreviewTable({
  listing,
  preview,
}: {
  listing: HuggingFaceRepositoryListingDto | null;
  preview: SnapshotPreview | null;
}) {
  if (!listing) {
    return (
      <p className="text-xs text-gray-600">
        Browse a repository above to preview its contents before download.
      </p>
    );
  }
  if (!preview) {
    return (
      <p className="text-xs text-gray-600">
        Browse succeeded but no preview classifier was supplied. {listing.files.length} files in{' '}
        <span className="font-mono">{listing.repository}</span>.
      </p>
    );
  }

  const totalSize = preview.totalSizeBytes ?? listing.files.reduce((acc, f) => acc + (f.size ?? 0), 0);
  const rows: Array<{
    path: string;
    size?: number | null;
    badges: string[];
    bucketLabel: string;
    file: HuggingFaceRepositoryFileDto;
  }> = [];
  const fileByPath = new Map(listing.files.map((f) => [f.path, f]));
  for (const bucket of preview.buckets) {
    for (const file of bucket.files) {
      const baseFile = fileByPath.get(file.path);
      if (!baseFile) continue;
      rows.push({
        path: file.path,
        size: file.size ?? baseFile.size ?? null,
        badges: [bucket.label, ...(file.badges ?? [])],
        bucketLabel: bucket.label,
        file: baseFile,
      });
    }
  }

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-2 text-xs text-gray-700">
        <span>
          Total size: <span className="font-mono">{formatBytes(totalSize)}</span>
        </span>
        {preview.buckets.map((bucket) => (
          <span key={bucket.id} className="rounded bg-gray-100 px-2 py-0.5">
            {bucket.label}: {bucket.files.length}
          </span>
        ))}
      </div>
      {preview.warnings && preview.warnings.length > 0 ? (
        <div className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
          {preview.warnings.map((warning, index) => (
            <div key={index}>{warning}</div>
          ))}
        </div>
      ) : null}
      {listing.gated ? (
        <div className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
          Gated repository — downloads require an accepted license on Hugging Face.
        </div>
      ) : null}
      <div className="max-h-64 overflow-auto rounded border border-gray-200">
        <table className="w-full text-xs">
          <thead className="bg-gray-50 text-left uppercase tracking-wide text-gray-600">
            <tr>
              <th className="px-2 py-1">File</th>
              <th className="px-2 py-1 text-right">Size</th>
              <th className="px-2 py-1">Badges</th>
            </tr>
          </thead>
          <tbody>
            {rows.length === 0 ? (
              <tr>
                <td colSpan={3} className="px-2 py-2 text-gray-500">
                  This repository is empty.
                </td>
              </tr>
            ) : null}
            {rows.map((row) => (
              <tr key={row.path} className="border-t border-gray-100">
                <td className="break-all px-2 py-1 font-mono text-gray-900">
                  <a
                    href={`https://huggingface.co/${listing.repository}/blob/main/${row.path}`}
                    target="_blank"
                    rel="noreferrer"
                    className="hover:underline"
                  >
                    {row.path}
                  </a>
                </td>
                <td className="whitespace-nowrap px-2 py-1 text-right text-gray-700">{formatBytes(row.size)}</td>
                <td className="px-2 py-1 text-gray-700">
                  {row.badges.map((badge, index) => (
                    <span key={index} className="mr-1 inline-flex items-center rounded bg-gray-100 px-1.5 py-0.5">
                      {badge}
                    </span>
                  ))}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
