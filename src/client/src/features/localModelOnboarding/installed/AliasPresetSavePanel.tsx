import { forwardRef, useEffect, useImperativeHandle, useMemo, useState } from 'react';
import { api } from '../../../services/api';
import type { LlamaRouterEntryDto } from '../../../types/settings';
import { AliasPresetEditor } from '../advanced/AliasPresetEditor';
import {
  buildEffectivePresetRecord,
  isManagedPresetKey,
  splitManagedPresetFromRecord,
  validateAliasPresetRows,
  type PresetKeyValue,
} from '../routerPreset';

function parseOptionalPositiveInt(raw: string): number | null {
  const trimmed = raw.trim();
  if (!trimmed) {
    return null;
  }
  const value = Number(trimmed);
  if (!Number.isInteger(value) || value <= 0) {
    return null;
  }
  return value;
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
}

export const AliasPresetSavePanel = forwardRef<AliasPresetSavePanelHandle, AliasPresetSavePanelProps>(
  function AliasPresetSavePanel({ alias, routerEntry, fallbackPreset = {} }, ref) {
    const authoritativePreset = useMemo(() => {
      if (routerEntry?.preset && Object.keys(routerEntry.preset).length > 0) {
        return routerEntry.preset;
      }
      return fallbackPreset;
    }, [fallbackPreset, routerEntry?.preset]);

    const [rows, setRows] = useState<PresetKeyValue[]>(() =>
      resolveManagedDrafts(authoritativePreset, routerEntry).rows,
    );
    const [ctxSizeDraft, setCtxSizeDraft] = useState(
      () => resolveManagedDrafts(authoritativePreset, routerEntry).ctxSizeDraft,
    );
    const [cacheRamDraft, setCacheRamDraft] = useState(
      () => resolveManagedDrafts(authoritativePreset, routerEntry).cacheRamDraft,
    );
    const [presetMode, setPresetMode] = useState<'replace' | 'merge'>('merge');

    useEffect(() => {
      const next = resolveManagedDrafts(authoritativePreset, routerEntry);
      setRows(next.rows);
      setCtxSizeDraft(next.ctxSizeDraft);
      setCacheRamDraft(next.cacheRamDraft);
    }, [alias, authoritativePreset, routerEntry]);

    const effectivePreset = useMemo(
      () => buildEffectivePresetRecord(rows, ctxSizeDraft, cacheRamDraft),
      [cacheRamDraft, ctxSizeDraft, rows],
    );
    const validationErrors = useMemo(() => validateAliasPresetRows(rows), [rows]);

    useImperativeHandle(
      ref,
      () => ({
        async saveRouterPreset() {
          if (!routerEntry) {
            throw new Error('Router entry is unavailable. Refresh and try again.');
          }
          if (validationErrors.length > 0) {
            throw new Error(validationErrors[0] ?? 'Fix preset validation errors before saving.');
          }

          await api.settings.putLlamaRouterEntry(alias, {
            alias,
            modelPath: routerEntry.modelPath,
            mmprojPath: routerEntry.mmprojPath ?? '',
            preset: effectivePreset,
            presetMode,
            contextSize: parseOptionalPositiveInt(ctxSizeDraft),
            cacheRamMib: parseOptionalPositiveInt(cacheRamDraft),
          });
        },
      }),
      [
        alias,
        cacheRamDraft,
        ctxSizeDraft,
        effectivePreset,
        presetMode,
        routerEntry,
        validationErrors,
      ],
    );

    return (
      <div data-testid="alias-preset-save-panel" className="space-y-4 rounded border border-gray-200 bg-white p-4">
        <div>
          <h3 className="text-sm font-semibold text-gray-900">Router preset</h3>
          <p className="mt-1 text-xs text-gray-600">
            llama-server switches for <span className="font-mono">{alias}</span>. Use Save below to apply this
            model&apos;s preset to the router.
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
            />
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
            />
          </label>
        </div>

        <AliasPresetEditor
          rows={rows}
          onChange={(nextRows) =>
            setRows(nextRows.filter((row) => !isManagedPresetKey(row.key)))
          }
          alias={alias}
          previewPreset={effectivePreset}
          presetMode={presetMode}
          onPresetModeChange={setPresetMode}
        />

        {!routerEntry ? (
          <p className="text-xs text-amber-800">Live router entry not loaded — refresh catalog edit.</p>
        ) : null}
      </div>
    );
  },
);
