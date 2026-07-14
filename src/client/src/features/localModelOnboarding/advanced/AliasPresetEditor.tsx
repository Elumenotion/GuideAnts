import { useMemo } from 'react';
import type { PresetKeyValue } from '../routerPreset';
import {
  buildAliasIniPreview,
  presetRecordFromRows,
  validateAliasPresetRows,
} from '../routerPreset';

export interface AliasPresetEditorProps {
  rows: PresetKeyValue[];
  onChange: (rows: PresetKeyValue[]) => void;
  alias: string;
  /** Merged into INI preview only (e.g. ctx-size edited in dedicated fields). */
  previewPreset?: Record<string, string>;
  readOnly?: boolean;
  presetMode?: 'replace' | 'merge';
  onPresetModeChange?: (mode: 'replace' | 'merge') => void;
}

export function AliasPresetEditor({
  rows,
  onChange,
  alias,
  previewPreset,
  readOnly = false,
  presetMode = 'replace',
  onPresetModeChange,
}: AliasPresetEditorProps) {
  const validationErrors = useMemo(() => validateAliasPresetRows(rows), [rows]);
  const preview = useMemo(
    () => buildAliasIniPreview(alias, { ...presetRecordFromRows(rows), ...(previewPreset ?? {}) }),
    [alias, previewPreset, rows],
  );

  const updateRow = (index: number, patch: Partial<PresetKeyValue>) => {
    onChange(rows.map((row, i) => (i === index ? { ...row, ...patch } : row)));
  };

  const addRow = () => onChange([...rows, { key: '', value: '' }]);
  const removeRow = (index: number) => onChange(rows.filter((_, i) => i !== index));

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="text-xs font-medium uppercase tracking-wide text-gray-600">Alias preset</div>
        {!readOnly && onPresetModeChange ? (
          <div className="flex items-center gap-2 text-xs text-gray-700">
            <span>Write mode</span>
            <select
              value={presetMode}
              onChange={(event) => onPresetModeChange(event.target.value as 'replace' | 'merge')}
              className="rounded border border-gray-300 px-2 py-1 text-xs"
            >
              <option value="replace">Replace extras</option>
              <option value="merge">Merge extras</option>
            </select>
          </div>
        ) : null}
      </div>

      {rows.length === 0 && readOnly ? (
        <p className="text-xs text-gray-500">No alias preset entries.</p>
      ) : null}

      <div className="space-y-2">
        {rows.map((row, index) => (
          <div key={`${index}-${row.key}`} className="flex flex-wrap items-start gap-2">
            <input
              type="text"
              value={row.key}
              disabled={readOnly}
              onChange={(event) => updateRow(index, { key: event.target.value })}
              placeholder="ctx-size"
              className="min-w-[8rem] flex-1 rounded border border-gray-300 px-2 py-1.5 font-mono text-xs"
              spellCheck={false}
            />
            <input
              type="text"
              value={row.value}
              disabled={readOnly}
              onChange={(event) => updateRow(index, { value: event.target.value })}
              placeholder="value"
              className="min-w-[8rem] flex-[2] rounded border border-gray-300 px-2 py-1.5 font-mono text-xs"
              spellCheck={false}
            />
            {!readOnly ? (
              <button
                type="button"
                onClick={() => removeRow(index)}
                className="rounded px-2 py-1 text-xs text-red-700 hover:bg-red-50"
              >
                Remove
              </button>
            ) : null}
          </div>
        ))}
      </div>

      {!readOnly ? (
        <button type="button" onClick={addRow} className="text-xs text-blue-700 hover:underline">
          Add preset key
        </button>
      ) : null}

      {validationErrors.length > 0 ? (
        <ul className="list-disc space-y-1 pl-4 text-xs text-red-700">
          {validationErrors.map((error) => (
            <li key={error}>{error}</li>
          ))}
        </ul>
      ) : null}

      <div className="rounded border border-gray-200 bg-gray-50 p-3">
        <div className="mb-1 text-[11px] font-medium uppercase tracking-wide text-gray-500">INI preview</div>
        <pre className="overflow-x-auto whitespace-pre-wrap font-mono text-xs text-gray-800">{preview}</pre>
      </div>
    </div>
  );
}
