import type { EnvironmentVariableDto } from '../../../../types/guides';
import { EnvironmentSecretRefField } from '../EnvironmentSecretRefField';
import { formatSecretRef } from './environmentVariableRefs';
import type { McpConnectionSettings, McpEnvironmentVariableRef, McpPackageDescriptor } from './mcpToolSourceTypes';

export interface McpPackageConnectionFieldsProps {
  settings: McpConnectionSettings;
  environmentVariables: EnvironmentVariableDto[];
  onEnvironmentVariablesChange: (variables: EnvironmentVariableDto[]) => void;
  onPackageChange: (packageDescriptor: McpPackageDescriptor) => void;
  onEnvironmentVariableRefsChange: (refs: McpEnvironmentVariableRef[]) => void;
}

export function McpPackageConnectionFields({
  settings,
  environmentVariables,
  onEnvironmentVariablesChange,
  onPackageChange,
  onEnvironmentVariableRefsChange,
}: McpPackageConnectionFieldsProps) {
  const pkg = settings.package ?? {
    registryType: 'npm',
    identifier: '',
    command: 'npx',
    args: [],
  };
  const envRefs = settings.environmentVariables ?? [];

  const updatePackage = (partial: Partial<McpPackageDescriptor>) => {
    onPackageChange({ ...pkg, ...partial });
  };

  const updateEnvRef = (index: number, partial: Partial<McpEnvironmentVariableRef>) => {
    const next = envRefs.map((ref, i) => (i === index ? { ...ref, ...partial } : ref));
    onEnvironmentVariableRefsChange(next);
  };

  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-2">
        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">Registry type</label>
          <select
            value={pkg.registryType}
            onChange={(e) => updatePackage({ registryType: e.target.value })}
            className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md"
            data-testid="mcp-package-registry-type"
          >
            <option value="npm">npm</option>
            <option value="pypi">pypi</option>
          </select>
        </div>
        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">Package identifier</label>
          <input
            type="text"
            value={pkg.identifier}
            onChange={(e) => updatePackage({ identifier: e.target.value })}
            placeholder="@example/mcp-server"
            className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md font-mono"
            data-testid="mcp-package-identifier"
          />
        </div>
      </div>

      <div className="grid gap-3 sm:grid-cols-2">
        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">Command</label>
          <input
            type="text"
            value={pkg.command}
            onChange={(e) => updatePackage({ command: e.target.value })}
            placeholder="npx"
            className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md font-mono"
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">Args (space-separated)</label>
          <input
            type="text"
            value={(pkg.args ?? []).join(' ')}
            onChange={(e) =>
              updatePackage({
                args: e.target.value.trim() ? e.target.value.trim().split(/\s+/) : [],
              })
            }
            placeholder="-y @example/mcp-server"
            className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md font-mono"
          />
        </div>
      </div>

      <div>
        <div className="flex items-center justify-between mb-1">
          <label className="block text-xs font-medium text-gray-700">Environment variables</label>
          <button
            type="button"
            onClick={() =>
              onEnvironmentVariableRefsChange([
                ...envRefs,
                { name: '', secretRef: '' },
              ])
            }
            className="text-xs text-blue-600 hover:underline"
          >
            Add variable
          </button>
        </div>
        <div className="space-y-3">
          {envRefs.length === 0 && (
            <p className="text-xs text-gray-500">Map package env vars to guide secrets.</p>
          )}
          {envRefs.map((ref, index) => (
            <div key={index} className="rounded-md border border-gray-200 bg-white p-3 space-y-3">
              <div className="flex gap-2">
                <input
                  type="text"
                  value={ref.name}
                  placeholder="ENV_VAR_NAME"
                  onChange={(e) => updateEnvRef(index, { name: e.target.value })}
                  className="flex-1 px-2 py-1 text-xs border border-gray-300 rounded-md font-mono"
                />
                <button
                  type="button"
                  onClick={() => onEnvironmentVariableRefsChange(envRefs.filter((_, i) => i !== index))}
                  className="text-xs text-red-600 hover:underline"
                >
                  Remove
                </button>
              </div>
              <EnvironmentSecretRefField
                label="Guide secret"
                selectedVariableName={ref.secretRef.replace(/^\{\{secret:|\}\}$/g, '')}
                variables={environmentVariables}
                onVariablesChange={onEnvironmentVariablesChange}
                onSelectedVariableNameChange={(name) =>
                  updateEnvRef(index, { secretRef: name ? formatSecretRef(name) : '' })
                }
                hint="Stored as {{secret:NAME}} in the tool source descriptor."
              />
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
