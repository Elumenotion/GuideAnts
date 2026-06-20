import { FaPlus, FaTrash } from 'react-icons/fa';
import { EnvironmentVariableDto } from '../../../types/guides';
import {
  MASKED_SECRET_VALUE,
  findDuplicateEnvironmentNames,
  validateEnvironmentVariableName,
} from './environmentVariableValidation';

interface EnvironmentConfigProps {
  entityLabel: 'guide' | 'assistant';
  variables: EnvironmentVariableDto[];
  onChange: (variables: EnvironmentVariableDto[]) => void;
}

export function EnvironmentConfig({
  entityLabel,
  variables,
  onChange,
}: EnvironmentConfigProps) {
  const updateVariables = (
    updater: (variables: EnvironmentVariableDto[]) => EnvironmentVariableDto[]
  ) => {
    onChange(updater(variables));
  };

  const duplicates = findDuplicateEnvironmentNames(variables.map((variable) => variable.name));

  const updateVariable = (index: number, updates: Partial<EnvironmentVariableDto>) => {
    updateVariables((variables) =>
      variables.map((variable, i) => (i === index ? { ...variable, ...updates } : variable))
    );
  };

  return (
    <section className="bg-white rounded-lg border border-gray-200 p-6 space-y-4">
      <div>
        <h2 className="text-lg font-semibold text-gray-900">
          {entityLabel === 'guide' ? 'Guide' : 'Assistant'} Environment
        </h2>
        <p className="text-sm text-gray-600">
          {entityLabel === 'guide'
            ? 'Configured for this guide in this project. Script execution hydrates this guide plus its crew member environments. MCP tool sources reference secrets from here for API keys and auth headers.'
            : 'Configured for this assistant in this project. Script execution and MCP tool sources on this assistant can reference these secrets for API keys and auth headers. Secret values are masked after save.'}
        </p>
      </div>

      <div className="space-y-3">
        {variables.length === 0 ? (
          <div className="rounded-md border border-dashed border-gray-300 bg-gray-50 px-4 py-6 text-sm text-gray-600">
            No environment variables configured. Add secrets here to use them in script execution and MCP tool connection headers.
          </div>
        ) : (
          variables.map((variable, index) => {
            const nameError = validateEnvironmentVariableName(variable.name, duplicates);

            return (
              <div key={`guide-${index}`} className="rounded-md border border-gray-200 bg-gray-50 p-4 space-y-3">
                  <div className="flex items-start justify-between gap-3">
                    <div>
                      <div className="text-sm font-medium text-gray-900">
                        {variable.name || 'New variable'}
                      </div>
                    </div>
                    <button
                      type="button"
                      onClick={() => updateVariables((current) => current.filter((_, i) => i !== index))}
                      className="rounded p-1 text-red-600 hover:bg-red-50 hover:text-red-700"
                      title="Remove variable"
                    >
                      <FaTrash className="h-4 w-4" />
                    </button>
                  </div>

                  <div className="grid grid-cols-1 gap-3 md:grid-cols-12">
                    <div className="md:col-span-4">
                      <label className="mb-1 block text-xs font-medium text-gray-700">
                        Name <span className="text-red-500">*</span>
                      </label>
                      <input
                        type="text"
                        value={variable.name}
                        onChange={(e) => updateVariable(index, { name: e.target.value })}
                        placeholder="MY_API_KEY"
                        className={`w-full rounded-md border px-3 py-2 text-sm focus:outline-none focus:ring-2 ${
                          nameError
                            ? 'border-red-400 focus:ring-red-500'
                            : 'border-gray-300 focus:ring-blue-500'
                        }`}
                      />
                      {nameError && <p className="mt-1 text-xs text-red-600">{nameError}</p>}
                    </div>

                    <div className="md:col-span-6">
                      <label className="mb-1 block text-xs font-medium text-gray-700">
                        Value {variable.isSecret && <span className="text-red-500">*</span>}
                      </label>
                      <input
                        type={variable.isSecret ? 'password' : 'text'}
                        value={variable.value || ''}
                        onChange={(e) => updateVariable(index, { value: e.target.value })}
                        placeholder={variable.isSecret ? 'Enter secret value' : 'Value exposed to scripts'}
                        className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                      />
                      {variable.isSecret && (
                        <p className="mt-1 text-xs text-gray-500">
                          This value is write-only. Leave the masked value unchanged to preserve the existing secret.
                        </p>
                      )}
                    </div>

                    <div className="md:col-span-2 md:self-start md:pt-6">
                      <label className="inline-flex items-center gap-2 text-sm text-gray-700">
                        <input
                          type="checkbox"
                          checked={variable.isSecret}
                          onChange={(e) => {
                            const isSecret = e.target.checked;
                            updateVariable(index, {
                              isSecret,
                              value: !isSecret && variable.value === MASKED_SECRET_VALUE ? '' : variable.value,
                            });
                          }}
                          className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                        />
                        Secret
                      </label>
                    </div>
                  </div>
                </div>
            );
          })
        )}
      </div>

      <button
        type="button"
        onClick={() => updateVariables((current) => [...current, { name: '', value: '', isSecret: false }])}
        className="flex items-center gap-2 rounded-md border border-blue-300 px-4 py-2 text-sm text-blue-600 hover:bg-blue-50"
      >
        <FaPlus className="h-4 w-4" />
        Add Variable
      </button>
    </section>
  );
}
