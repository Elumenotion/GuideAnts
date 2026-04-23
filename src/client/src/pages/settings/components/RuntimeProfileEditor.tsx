import { ProfileFormState } from '../types';
import { TextActionButton } from './shared/ActionButtons';

interface RuntimeProfileEditorProps {
  mode: 'full' | 'inline';
  value: ProfileFormState;
  onChange: <K extends keyof ProfileFormState>(key: K, nextValue: ProfileFormState[K]) => void;
  onInsertTemplate?: (template: 'qwen3_5' | 'qwen3_6' | 'gemma4') => void;
  onSubmit?: () => void;
  submitting?: boolean;
  submitLabel?: string;
  disableIdentityFields?: boolean;
}

export function RuntimeProfileEditor({
  mode,
  value,
  onChange,
  onInsertTemplate,
  onSubmit,
  submitting = false,
  submitLabel = 'Save profile',
  disableIdentityFields = false,
}: RuntimeProfileEditorProps) {
  const inline = mode === 'inline';
  return (
    <div className={inline ? 'space-y-3' : 'grid grid-cols-1 gap-4 md:grid-cols-2'}>
      <div className="space-y-2">
        <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Profile ID</label>
        <input
          type="text"
          value={value.profileId}
          onChange={(event) => onChange('profileId', event.target.value)}
          disabled={disableIdentityFields}
          className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 disabled:bg-gray-100"
          autoComplete="off"
          spellCheck={false}
        />
      </div>

      <div className="space-y-2">
        <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Display Name</label>
        <input
          type="text"
          value={value.displayName}
          onChange={(event) => onChange('displayName', event.target.value)}
          className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
          autoComplete="off"
        />
      </div>

      {onInsertTemplate ? (
        <div className={inline ? '' : 'md:col-span-2'}>
          <div className="flex flex-wrap gap-1">
            <button
              type="button"
              onClick={() => onInsertTemplate('qwen3_5')}
              className="rounded border border-gray-300 px-2 py-1 text-xs text-gray-700 hover:bg-gray-50"
            >
              Insert qwen3_5
            </button>
            <button
              type="button"
              onClick={() => onInsertTemplate('qwen3_6')}
              className="rounded border border-gray-300 px-2 py-1 text-xs text-gray-700 hover:bg-gray-50"
            >
              Insert qwen3_6
            </button>
            <button
              type="button"
              onClick={() => onInsertTemplate('gemma4')}
              className="rounded border border-gray-300 px-2 py-1 text-xs text-gray-700 hover:bg-gray-50"
            >
              Insert gemma4
            </button>
          </div>
        </div>
      ) : null}

      <div className={inline ? 'space-y-2' : 'space-y-2 md:col-span-2'}>
        <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Description</label>
        <textarea
          value={value.description}
          onChange={(event) => onChange('description', event.target.value)}
          rows={2}
          className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
        />
      </div>

      <div className={inline ? 'space-y-2' : 'space-y-2 md:col-span-2'}>
        <label className="inline-flex items-center gap-2 text-sm text-gray-700">
          <input
            type="checkbox"
            checked={value.combineSystemAndDeveloperMessages}
            onChange={(event) => onChange('combineSystemAndDeveloperMessages', event.target.checked)}
            className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
          />
          Combine System and Developer Messages
        </label>
      </div>

      <div className={inline ? 'space-y-2' : 'space-y-2 md:col-span-2'}>
        <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Thought Block Pattern (Regex)</label>
        <input
          type="text"
          value={value.thoughtBlockPattern}
          onChange={(event) => onChange('thoughtBlockPattern', event.target.value)}
          className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
          autoComplete="off"
          spellCheck={false}
        />
      </div>

      <div className={inline ? 'space-y-2' : 'space-y-2 md:col-span-2'}>
        <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Sampling Parameters JSON</label>
        <textarea
          value={value.samplingParametersJson}
          onChange={(event) => onChange('samplingParametersJson', event.target.value)}
          rows={inline ? 4 : 5}
          className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-xs text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
          spellCheck={false}
        />
      </div>

      <div className={inline ? 'space-y-2' : 'space-y-2 md:col-span-2'}>
        <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Thinking Control JSON</label>
        <textarea
          value={value.thinkingControlJson}
          onChange={(event) => onChange('thinkingControlJson', event.target.value)}
          rows={inline ? 4 : 5}
          className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-xs text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
          spellCheck={false}
        />
      </div>

      {onSubmit ? (
        <div className={inline ? '' : 'md:col-span-2'}>
          <TextActionButton tone="primary" onClick={onSubmit} disabled={submitting} title={submitLabel}>
            {submitLabel}
          </TextActionButton>
        </div>
      ) : null}
    </div>
  );
}
