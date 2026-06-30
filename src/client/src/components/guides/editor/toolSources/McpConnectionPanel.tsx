import { FaPlug, FaRedo, FaSearch } from 'react-icons/fa';
import type { CustomToolDto, EnvironmentVariableDto } from '../../../../types/guides';
import { isLegacyClientBridgeMcpSource, validateMcpToolNamePrefixCollision } from './mcpToolSource';
import type { McpRuntimeExecution } from './mcpToolSourceTypes';
import { McpDiscoveryResults } from './McpDiscoveryResults';
import { McpHttpConnectionFields } from './McpHttpConnectionFields';
import { McpPackageConnectionFields } from './McpPackageConnectionFields';
import { McpSandboxSetupStatus } from './McpSandboxSetupStatus';
import { useMcpConnection } from './useMcpConnection';

export interface McpConnectionPanelProps {
  tool: CustomToolDto;
  toolIndex: number;
  allTools: CustomToolDto[];
  environmentVariables: EnvironmentVariableDto[];
  projectId?: string;
  guideId?: string;
  onEnvironmentVariablesChange: (variables: EnvironmentVariableDto[]) => void;
  onUpdate: (updates: Partial<CustomToolDto>) => void;
  onDirty?: () => void;
  inputRef?: (el: HTMLInputElement | null) => void;
}

export function McpConnectionPanel({
  tool,
  toolIndex,
  allTools,
  environmentVariables,
  projectId,
  guideId,
  onEnvironmentVariablesChange,
  onUpdate,
  onDirty,
  inputRef,
}: McpConnectionPanelProps) {
  const connection = useMcpConnection({
    tool,
    toolIndex,
    allTools,
    environmentVariables,
    projectId,
    guideId,
    onUpdate,
    onDirty,
  });

  const prefixCollision = validateMcpToolNamePrefixCollision(
    allTools,
    toolIndex,
    connection.settings.toolNamePrefix,
  );
  const showMigrationNotice = isLegacyClientBridgeMcpSource(tool.openApiSpec);

  return (
    <div className="space-y-4" data-testid="mcp-connection-panel">
      <div className="rounded-md border border-teal-200 bg-teal-50 p-3 text-xs text-teal-900">
        MCP tools execute server-side via <code className="font-mono">mcp+api://</code> or{' '}
        <code className="font-mono">mcp+sandbox://</code> dispatch URLs.
      </div>

      {showMigrationNotice && (
        <p className="text-xs text-blue-800 bg-blue-50 border border-blue-200 rounded-md p-2" role="status">
          Migrated to API — the removed client-bridge path now runs through server-side MCP execution. Save to
          persist the updated descriptor.
        </p>
      )}

      <div className="grid gap-3 sm:grid-cols-2">
        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">Runtime execution</label>
          <select
            value={connection.settings.runtimeExecution}
            onChange={(e) => connection.handleRuntimeExecutionChange(e.target.value as McpRuntimeExecution)}
            className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md"
            data-testid="mcp-runtime-execution"
          >
            <option value="api">API (server-side HTTP)</option>
            <option value="sandbox_subprocess">Sandbox subprocess (registry package)</option>
          </select>
        </div>
        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">Discovery transport</label>
          <input
            type="text"
            readOnly
            value={connection.settings.discoveryTransport}
            className="w-full px-3 py-2 text-sm border border-gray-200 rounded-md bg-gray-50 font-mono"
            data-testid="mcp-discovery-transport"
          />
        </div>
      </div>

      <div className="grid gap-3 sm:grid-cols-2">
        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">
            MCP bridge id <span className="text-red-500">*</span>
          </label>
          <input
            ref={inputRef}
            type="text"
            value={connection.settings.bridgeId}
            onChange={(e) => void connection.updateSettings({ bridgeId: e.target.value })}
            className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md font-mono"
            data-testid="mcp-dispatch-id"
          />
          <p className="text-xs text-gray-500 mt-1 font-mono" data-testid="mcp-dispatch-url-preview">
            {connection.dispatchUrl}
          </p>
        </div>
      </div>

      {connection.settings.runtimeExecution === 'api' ? (
        <McpHttpConnectionFields
          settings={connection.settings}
          headerRows={connection.headerRows}
          environmentVariables={environmentVariables}
          onEnvironmentVariablesChange={onEnvironmentVariablesChange}
          onUrlChange={(url) => void connection.updateSettings({ url })}
          onHeaderRowsChange={connection.updateHeaders}
        />
      ) : (
        <>
          <McpPackageConnectionFields
            settings={connection.settings}
            environmentVariables={environmentVariables}
            onEnvironmentVariablesChange={onEnvironmentVariablesChange}
            onPackageChange={(packageDescriptor) => void connection.updateSettings({ package: packageDescriptor })}
            onEnvironmentVariableRefsChange={(environmentVariablesRefs) =>
              void connection.updateSettings({ environmentVariables: environmentVariablesRefs })
            }
          />
          <McpSandboxSetupStatus
            setupStatus={connection.setupStatus}
            panelState={connection.panelState}
            showApplyConfirm={connection.showApplyConfirm}
            isBusy={connection.isBusy}
            onRequestApply={connection.requestApply}
            onConfirmApply={() => void connection.confirmApply()}
            onCancelApply={() => connection.setShowApplyConfirm(false)}
          />
        </>
      )}

      <div>
        <label className="block text-xs font-medium text-gray-700 mb-1">Tool name prefix</label>
        <input
          type="text"
          value={connection.settings.toolNamePrefix}
          onChange={(e) => void connection.updateSettings({ toolNamePrefix: e.target.value })}
          className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md font-mono"
          data-testid="mcp-tool-name-prefix"
        />
        {prefixCollision && (
          <p className="text-xs text-red-600 mt-1" role="alert">
            {prefixCollision}
          </p>
        )}
      </div>

      {(connection.validationError || prefixCollision) && (
        <p className="text-xs text-red-600" role="alert">
          {connection.validationError ?? prefixCollision}
        </p>
      )}

      <div className="flex flex-wrap gap-2">
        <button
          type="button"
          onClick={() => void connection.handleTestConnection()}
          disabled={connection.isBusy || !!prefixCollision}
          className="inline-flex items-center gap-2 px-3 py-1.5 text-xs font-medium text-teal-700 bg-teal-50 border border-teal-200 rounded-md hover:bg-teal-100 disabled:opacity-50"
          data-testid="mcp-test-connection"
        >
          <FaPlug className="w-3 h-3" />
          {connection.panelState === 'testing' ? 'Testing connection…' : 'Test connection'}
        </button>
        <button
          type="button"
          onClick={() => void connection.handleDiscover()}
          disabled={connection.isBusy || !!connection.validationError || !!prefixCollision}
          className="inline-flex items-center gap-2 px-3 py-1.5 text-xs font-medium text-blue-700 bg-blue-50 border border-blue-200 rounded-md hover:bg-blue-100 disabled:opacity-50"
          data-testid="mcp-discover-tools"
        >
          <FaSearch className="w-3 h-3" />
          {connection.panelState === 'discovering' ? 'Discovering tools…' : 'Discover tools'}
        </button>
        {connection.panelState === 'discovery-failed' && (
          <button
            type="button"
            onClick={() => void connection.handleDiscover()}
            className="inline-flex items-center gap-2 px-3 py-1.5 text-xs font-medium text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50"
          >
            <FaRedo className="w-3 h-3" />
            Retry discovery
          </button>
        )}
        {connection.panelState === 'apply-failed' && (
          <button
            type="button"
            onClick={() => void connection.runApply()}
            className="inline-flex items-center gap-2 px-3 py-1.5 text-xs font-medium text-orange-700 bg-orange-50 border border-orange-200 rounded-md hover:bg-orange-100"
          >
            <FaRedo className="w-3 h-3" />
            Retry apply
          </button>
        )}
      </div>

      {connection.statusMessage && connection.panelState !== 'discovery-failed' && (
        <p className="text-xs text-green-700 bg-green-50 border border-green-200 rounded-md p-2" aria-live="polite">
          {connection.statusMessage}
        </p>
      )}

      {connection.errorMessage && (
        <p
          className="text-xs text-red-700 bg-red-50 border border-red-200 rounded-md p-2"
          role="alert"
          aria-live="polite"
        >
          {connection.errorMessage}
        </p>
      )}

      <McpDiscoveryResults
        displayTools={connection.displayTools}
        showDiffReview={connection.showDiffReview}
        pendingDiscovery={connection.pendingDiscovery}
        panelState={connection.panelState}
        onApplyDiscovery={connection.handleApplyDiscovery}
        onToggleToolSelected={connection.toggleToolSelected}
      />
    </div>
  );
}
