import { useEffect, useMemo, useState } from 'react';
import { api } from '../../../services/api';
import type { LlamaRouterEntryDto } from '../../../types/settings';
import { getErrorMessage } from '../../../pages/settings/utils';
import { TextActionButton } from '../../../pages/settings/components/shared/ActionButtons';
import { AliasPresetEditor } from '../advanced/AliasPresetEditor';
import {
  presetRecordFromRows,
  presetRowsFromRecord,
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

function upsertPresetRow(rows: PresetKeyValue[], key: string, value: string): PresetKeyValue[] {
  const trimmedKey = key.trim();
  const next = rows.filter((row) => row.key.trim().toLowerCase() !== trimmedKey.toLowerCase());
  if (!value.trim()) {
    return next;
  }
  return [...next, { key: trimmedKey, value: value.trim() }];
}

function presetRowValue(rows: PresetKeyValue[], key: string): string {
  const match = rows.find((row) => row.key.trim().toLowerCase() === key.toLowerCase());
  return match?.value ?? '';
}

export interface AliasPresetSavePanelProps {
  alias: string;
  routerEntry: LlamaRouterEntryDto | null;
  /** Fallback when router entry is unavailable (e.g. provenance snapshot). */
  fallbackPreset?: Record<string, string>;
  onSaved?: () => Promise<void>;
}

export function AliasPresetSavePanel({
  alias,
  routerEntry,
  fallbackPreset = {},
  onSaved,
}: AliasPresetSavePanelProps) {
  const authoritativePreset = useMemo(() => {
    if (routerEntry?.preset && Object.keys(routerEntry.preset).length > 0) {
      return routerEntry.preset;
    }
    return fallbackPreset;
  }, [fallbackPreset, routerEntry?.preset]);

  const [rows, setRows] = useState<PresetKeyValue[]>(() => presetRowsFromRecord(authoritativePreset));
  const [presetMode, setPresetMode] = useState<'replace' | 'merge'>('merge');
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saveMessage, setSaveMessage] = useState<string | null>(null);

  useEffect(() => {
    setRows(presetRowsFromRecord(authoritativePreset));
    setSaveError(null);
    setSaveMessage(null);
  }, [alias, authoritativePreset]);

  const ctxSize = presetRowValue(rows, 'ctx-size') || (routerEntry?.contextSize?.toString() ?? '');
  const cacheRam = presetRowValue(rows, 'cache-ram') || (routerEntry?.cacheRamMib?.toString() ?? '');
  const validationErrors = useMemo(() => validateAliasPresetRows(rows), [rows]);
  const canSave = !!routerEntry && validationErrors.length === 0 && !saving;

  const save = async () => {
    if (!routerEntry) {
      setSaveError('Router entry is unavailable. Refresh and try again.');
      return;
    }
    if (validationErrors.length > 0) {
      setSaveError(validationErrors[0] ?? 'Fix preset validation errors before saving.');
      return;
    }

    setSaving(true);
    setSaveError(null);
    setSaveMessage(null);
    try {
      const preset = presetRecordFromRows(rows);
      await api.settings.putLlamaRouterEntry(alias, {
        alias,
        modelPath: routerEntry.modelPath,
        mmprojPath: routerEntry.mmprojPath ?? '',
        preset,
        presetMode,
        contextSize: parseOptionalPositiveInt(ctxSize),
        cacheRamMib: parseOptionalPositiveInt(cacheRam),
      });
      setSaveMessage('Router preset saved. Loaded models may restart to apply changes.');
      await onSaved?.();
    } catch (error) {
      setSaveError(getErrorMessage(error, 'Failed to save router preset.'));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div data-testid="alias-preset-save-panel" className="space-y-4 rounded border border-gray-200 bg-white p-4">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Router preset</h3>
        <p className="mt-1 text-xs text-gray-600">
          llama-server switches for <span className="font-mono">{alias}</span>. Save applies this model&apos;s preset to
          the router.
        </p>
      </div>

      <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
        <label className="space-y-1 text-sm text-gray-700">
          <span className="text-xs font-medium uppercase tracking-wide text-gray-600">Context size (tokens)</span>
          <input
            type="text"
            inputMode="numeric"
            value={ctxSize}
            onChange={(event) => setRows((previous) => upsertPresetRow(previous, 'ctx-size', event.target.value))}
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
            value={cacheRam}
            onChange={(event) => setRows((previous) => upsertPresetRow(previous, 'cache-ram', event.target.value))}
            className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm"
            placeholder="optional"
            spellCheck={false}
          />
        </label>
      </div>

      <AliasPresetEditor
        rows={rows}
        onChange={setRows}
        alias={alias}
        presetMode={presetMode}
        onPresetModeChange={setPresetMode}
      />

      <div className="flex flex-wrap items-center gap-2">
        <TextActionButton tone="primary" onClick={() => void save()} disabled={!canSave} title="Save router preset">
          Save router preset
        </TextActionButton>
        {!routerEntry ? (
          <span className="text-xs text-amber-800">Live router entry not loaded — refresh catalog edit.</span>
        ) : null}
      </div>

      {saveMessage ? <p className="text-sm text-emerald-800">{saveMessage}</p> : null}
      {saveError ? <p className="text-sm text-red-700">{saveError}</p> : null}
    </div>
  );
}
