import { useMemo, useState } from 'react';
import type { EnvironmentVariableDto } from '../../../types/guides';
import {
  findDuplicateEnvironmentNames,
  validateEnvironmentVariableName,
} from './environmentVariableValidation';
import { listSecretVariables } from './toolSources/environmentVariableRefs';

const CREATE_NEW_VALUE = '__create_new_secret__';

export interface EnvironmentSecretRefFieldProps {
  label: string;
  hint?: string;
  selectedVariableName: string;
  variables: EnvironmentVariableDto[];
  onVariablesChange: (variables: EnvironmentVariableDto[]) => void;
  onSelectedVariableNameChange: (name: string) => void;
  placeholder?: string;
}

export function EnvironmentSecretRefField({
  label,
  hint,
  selectedVariableName,
  variables,
  onVariablesChange,
  onSelectedVariableNameChange,
  placeholder = 'Select a guide secret',
}: EnvironmentSecretRefFieldProps) {
  const secretVariables = useMemo(() => listSecretVariables(variables), [variables]);
  const [creating, setCreating] = useState(false);
  const [draftName, setDraftName] = useState('');
  const [draftValue, setDraftValue] = useState('');

  const duplicateNames = findDuplicateEnvironmentNames(variables.map((variable) => variable.name));
  const draftNameError = validateEnvironmentVariableName(draftName, duplicateNames);
  const canAddDraft =
    !draftNameError && draftName.trim().length > 0 && draftValue.trim().length > 0;

  const selectValue = creating
    ? CREATE_NEW_VALUE
    : selectedVariableName || '';

  const handleSelectChange = (value: string) => {
    if (value === CREATE_NEW_VALUE) {
      setCreating(true);
      setDraftName('');
      setDraftValue('');
      return;
    }

    setCreating(false);
    onSelectedVariableNameChange(value);
  };

  const handleAddDraft = () => {
    if (!canAddDraft) return;

    const nextVariable: EnvironmentVariableDto = {
      name: draftName.trim(),
      value: draftValue,
      isSecret: true,
    };

    onVariablesChange([...variables, nextVariable]);
    onSelectedVariableNameChange(nextVariable.name);
    setCreating(false);
    setDraftName('');
    setDraftValue('');
  };

  return (
    <div className="space-y-2">
      <label className="block text-xs font-medium text-gray-700">{label}</label>
      <select
        value={selectValue}
        onChange={(e) => handleSelectChange(e.target.value)}
        className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
        data-testid="environment-secret-ref-select"
      >
        <option value="">{placeholder}</option>
        {secretVariables.map((variable) => (
          <option key={variable.name} value={variable.name}>
            {variable.name}
          </option>
        ))}
        <option value={CREATE_NEW_VALUE}>+ Create new secret…</option>
      </select>

      {creating && (
        <div className="rounded-md border border-blue-200 bg-blue-50 p-3 space-y-3">
          <p className="text-xs text-blue-900">
            Adds a secret to this guide&apos;s environment. Save the guide to persist it.
          </p>
          <div>
            <label className="mb-1 block text-xs font-medium text-gray-700">
              Secret name <span className="text-red-500">*</span>
            </label>
            <input
              type="text"
              value={draftName}
              onChange={(e) => setDraftName(e.target.value)}
              placeholder="MCP_API_KEY"
              className={`w-full rounded-md border px-3 py-2 text-sm font-mono ${
                draftNameError ? 'border-red-400' : 'border-gray-300'
              }`}
              data-testid="environment-secret-ref-draft-name"
            />
            {draftNameError && <p className="mt-1 text-xs text-red-600">{draftNameError}</p>}
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-gray-700">
              Secret value <span className="text-red-500">*</span>
            </label>
            <input
              type="password"
              value={draftValue}
              onChange={(e) => setDraftValue(e.target.value)}
              placeholder="Enter secret value"
              className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
              data-testid="environment-secret-ref-draft-value"
            />
          </div>
          <div className="flex gap-2">
            <button
              type="button"
              onClick={handleAddDraft}
              disabled={!canAddDraft}
              className="rounded-md bg-blue-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-blue-700 disabled:opacity-50"
              data-testid="environment-secret-ref-add"
            >
              Add secret
            </button>
            <button
              type="button"
              onClick={() => {
                setCreating(false);
                setDraftName('');
                setDraftValue('');
              }}
              className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50"
            >
              Cancel
            </button>
          </div>
        </div>
      )}

      {hint && <p className="text-xs text-gray-500">{hint}</p>}
    </div>
  );
}
