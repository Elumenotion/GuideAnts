import { forwardRef, useCallback, useEffect, useImperativeHandle, useMemo, useState } from 'react';
import { api } from '../../../services/api';
import type { LlamaRouterEntryDto } from '../../../types/settings';
import { AliasPresetEditor } from '../advanced/AliasPresetEditor';
import {
  buildEffectivePresetRecord,
  isManagedPresetKey,
  splitManagedPresetFromRecord,
  validateAliasPresetRows,
  withStablePresetRowIds,
  type PresetKeyValue,
} from '../routerPreset';

function buildManagedPreviewExtras(ctxSizeDraft: string, cacheRamDraft: string): Record<string, string> {
  const extras: Record<string, string> = {};
  const ctxSize = ctxSizeDraft.trim();
  const cacheRam = cacheRamDraft.trim();
  if (ctxSize) {
    extras['ctx-size'] = ctxSize;
  }
  if (cacheRam) {
    extras['cache-ram'] = cacheRam;
  }
  return extras;
}

function serverDraftKey(
  alias: string,
  preset: Record<string, string>,
  routerEntry: LlamaRouterEntryDto | null,
): string {
  return JSON.stringify({
    alias,
    preset,
    contextSize: routerEntry?.contextSize ?? null,
    cacheRamMib: routerEntry?.cacheRamMib ?? null,
  });
}

/**
 * Parse a managed field (ctx-size / cache-ram).
 * Blank means "no key" (null -> container default). Any non-blank value must be
 * a whole integer: -1 is a valid sentinel in llama-server, so negative values
 * are legal here. Garbage is reported to the caller instead of being silently
 * dropped on save.
 */
function parseManagedIntField(
  raw: string,
  fieldName: string,
): { value: number | null; error: string | null } {
  const trimmed = raw.trim();
  if (!trimmed) {
    return { value: null, error: null };
  }
  const value = Number(trimmed);
  if (!Number.isInteger(value)) {
    return { value: null, error: `${fieldName} must be an integer. -1 means the llama-server default; leave blank to remove the key.` };
  }
  return { value, error: null };
}

function resolveManagedDrafts(
  preset: Record<string, string>,
  routerEntry: LlamaRouterEntryDto | null,
): { ctxSizeDraft: string; cacheRamDraft: string; rows: PresetKeyValue[] } {
  const split = splitManagedPresetFromRecord(preset);
  return {
    ctxSizeDraft: split.ctxSize || (routerEntry?.contextSize?.toString() ?? ''),
    cacheRamDraft: split.cacheRam || (routerEntry?.cacheRamMib?.toString() ?? ''),
    rows: split.rows,
  };
}

export interface AliasPresetSavePanelHandle {
  saveRouterPreset: () => Promise<void>;
}

export interface AliasPresetSavePanelProps {
  alias: string;
  routerEntry: LlamaRouterEntryDto | null;
  /** Fallback when router entry is unavailable (e.g. provenance snapshot). */
  fallbackPreset?: Record<string, string>;
  fallbackModelPath?: string;
  fallbackMmprojPath?: string;
}

/**
 * Catalog-row editor for one alias preset.
 *
 * Save always uses presetMode=replace: the rows shown are the full desired
 * extras map. Merge mode is intentionally not offered here — it preserves
 * removed keys and makes Delete appear to fail after reload.
 */
export const AliasPresetSavePanel = forwardRef<AliasPresetSavePanelHandle, AliasPresetSavePanelProps>(
  function AliasPresetSavePanel({
    alias,
    routerEntry,
    fallbackPreset = {},
    fallbackModelPath = '',
    fallbackMmprojPath = '',
  }, ref) {
    const authoritativePreset = useMemo(() => {
      return routerEntry?.preset && Object.keys(routerEntry.preset).length > 0
        ? routerEntry.preset
        : fallbackPreset;
    }, [fallbackPreset, routerEntry?.preset]);

    const [rows, setRows] = useState<PresetKeyValue[]>(() =>
      withStablePresetRowIds(resolveManagedDrafts(authoritativePreset, routerEntry).rows),
    );
    const [ctxSizeDraft, setCtxSizeDraft] = useState(
      () => resolveManagedDrafts(authoritativePreset, routerEntry).ctxSizeDraft,
    );
    const [cacheRamDraft, setCacheRamDraft] = useState(
      () => resolveManagedDrafts(authoritativePreset, routerEntry).cacheRamDraft,
    );

    const authoritativeDraftKey = useMemo(
      () => serverDraftKey(alias, authoritativePreset, routerEntry),
      [alias, authoritativePreset, routerEntry],
    );

    useEffect(() => {
      const next = resolveManagedDrafts(authoritativePreset, routerEntry);
      setRows(withStablePresetRowIds(next.rows));
      setCtxSizeDraft(next.ctxSizeDraft);
      setCacheRamDraft(next.cacheRamDraft);
    }, [authoritativeDraftKey]);

    const effectivePreset = useMemo(
      () => buildEffectivePresetRecord(rows, ctxSizeDraft, cacheRamDraft),
      [cacheRamDraft, ctxSizeDraft, rows],
    );
    const managedPreviewExtras = useMemo(
      () => buildManagedPreviewExtras(ctxSizeDraft, cacheRamDraft),
      [cacheRamDraft, ctxSizeDraft],
    );
    const validationErrors = useMemo(() => validateAliasPresetRows(rows), [rows]);
    const ctxSizeError = useMemo(
      () => parseManagedIntField(ctxSizeDraft, 'Context size').error,
      [ctxSizeDraft],
    );
    const cacheRamError = useMemo(
      () => parseManagedIntField(cacheRamDraft, 'Prompt cache RAM').error,
      [cacheRamDraft],
    );

    const handlePresetRowsChange = useCallback((nextRows: PresetKeyValue[]) => {
      setRows(withStablePresetRowIds(nextRows.filter((row) => !isManagedPresetKey(row.key))));
    }, []);

    useImperativeHandle(
      ref,
      () => ({
        async saveRouterPreset() {
          const modelPath = routerEntry?.modelPath?.trim() || fallbackModelPath.trim();
          if (!modelPath) {
            throw new Error('Router entry is unavailable. Refresh and try again.');
          }
          if (validationErrors.length > 0) {
            throw new Error(validationErrors[0] ?? 'Fix preset validation errors before saving.');
          }
          const ctxSize = parseManagedIntField(ctxSizeDraft, 'Context size');
          const cacheRam = parseManagedIntField(cacheRamDraft, 'Prompt cache RAM');
          if (ctxSize.error || cacheRam.error) {
            throw new Error(ctxSize.error ?? cacheRam.error ?? 'Fix preset validation errors before saving.');
          }

          await api.settings.putLlamaRouterEntry(alias, {
            alias,
            modelPath,
            mmprojPath: routerEntry?.mmprojPath ?? fallbackMmprojPath,
            preset: effectivePreset,
            // WYSIWYG: removed rows must leave the INI, not survive via merge.
            presetMode: 'replace',
            contextSize: ctxSize.value,
            cacheRamMib: cacheRam.value,
          });
        },
      }),
      [alias, cacheRamDraft, ctxSizeDraft, effectivePreset, fallbackMmprojPath, fallbackModelPath, routerEntry, validationErrors, ctxSizeError, cacheRamError],
    );

    return (
      <div data-testid="alias-preset-save-panel" className="space-y-4 rounded border border-gray-200 bg-white p-4">
        <div>
          <h3 className="text-sm font-semibold text-gray-900">Router preset</h3>
          <p className="mt-1 text-xs text-gray-600">
            Model-specific llama-server switches for <span className="font-mono">{alias}</span>. Save replaces
            this alias&apos;s extras with the rows below (removed keys are deleted).
          </p>
        </div>

        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          <label className="space-y-1 text-sm text-gray-700">
            <span className="text-xs font-medium uppercase tracking-wide text-gray-600">Context size (tokens)</span>
            <input
              type="text"
              inputMode="numeric"
              value={ctxSizeDraft}
              onChange={(event) => setCtxSizeDraft(event.target.value)}
              className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm"
              placeholder="e.g. 131072"
              spellCheck={false}
              aria-invalid={ctxSizeError ? true : undefined}
            />
            {ctxSizeError ? <p className="text-xs text-red-700">{ctxSizeError}</p> : null}
          </label>
          <label className="space-y-1 text-sm text-gray-700">
            <span className="text-xs font-medium uppercase tracking-wide text-gray-600">Prompt cache RAM (MiB)</span>
            <input
              type="text"
              inputMode="numeric"
              value={cacheRamDraft}
              onChange={(event) => setCacheRamDraft(event.target.value)}
              className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm"
              placeholder="optional"
              spellCheck={false}
              aria-invalid={cacheRamError ? true : undefined}
            />
            {cacheRamError ? <p className="text-xs text-red-700">{cacheRamError}</p> : null}
          </label>
        </div>

        <AliasPresetEditor
          rows={rows}
          onChange={handlePresetRowsChange}
          alias={alias}
          previewPreset={managedPreviewExtras}
        />

        {!routerEntry ? (
          <p className="text-xs text-amber-800">
            Live router entry not loaded — editing the last saved snapshot. Save still writes the INI.
          </p>
        ) : null}
      </div>
    );
  },
);
