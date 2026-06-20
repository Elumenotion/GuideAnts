import { useCallback, useMemo, useState } from 'react';
import { FaCheck, FaRedo, FaSearch, FaPlug } from 'react-icons/fa';
import { api } from '../../../../services/api';
import type { CustomToolDto, EnvironmentVariableDto } from '../../../../types/guides';
import {
  applyMcpDiscoveryToSpec,
  buildResolvedMcpConnectionPayload,
  diffStateChipClassName,
  diffStateLabel,
  extractExistingMcpToolStates,
  headersContainUnresolvedSecrets,
  mcpHeaderRowsToHeaders,
  parseMcpConnectionSettings,
  parseMcpHeaderRows,
  validateMcpConnectionSettings,
  buildMcpBridgeServerUrl,
} from './mcpToolSource';
import type {
  McpConnectionPanelState,
  McpConnectionSettings,
  McpDiscoveredToolRow,
  McpHeaderRow,
  McpTransport,
} from './mcpToolSourceTypes';
import { EnvironmentSecretRefField } from '../EnvironmentSecretRefField';

export interface McpConnectionPanelProps {
  tool: CustomToolDto;
  environmentVariables: EnvironmentVariableDto[];
  onEnvironmentVariablesChange: (variables: EnvironmentVariableDto[]) => void;
  onUpdate: (updates: Partial<CustomToolDto>) => void;
  onDirty?: () => void;
  inputRef?: (el: HTMLInputElement | null) => void;
}

export function McpConnectionPanel({
  tool,
  environmentVariables,
  onEnvironmentVariablesChange,
  onUpdate,
  onDirty,
  inputRef,
}: McpConnectionPanelProps) {
  const initialSettings = useMemo(() => parseMcpConnectionSettings(tool.openApiSpec), [tool.openApiSpec]);
  const [settings, setSettings] = useState<McpConnectionSettings>(initialSettings);
  const [panelState, setPanelState] = useState<McpConnectionPanelState>('idle');
  const [statusMessage, setStatusMessage] = useState<string | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [discoveredTools, setDiscoveredTools] = useState<McpDiscoveredToolRow[]>([]);
  const [pendingDiscovery, setPendingDiscovery] = useState<McpDiscoveredToolRow[] | null>(null);
  const [headerRows, setHeaderRows] = useState<McpHeaderRow[]>(() =>
    parseMcpHeaderRows(initialSettings.headers)
  );

  const validationError = validateMcpConnectionSettings(settings);

  const syncSettingsToSpec = useCallback(
    (next: McpConnectionSettings) => {
      let parsed: Record<string, unknown>;
      try {
        parsed = JSON.parse(tool.openApiSpec);
      } catch {
        return;
      }

      parsed.servers = [{ url: buildMcpBridgeServerUrl(next.bridgeId), description: 'MCP client bridge' }];
      parsed['x-guideants-tool-source'] = {
        kind: 'mcp',
        transport: next.transport,
        bridgeId: next.bridgeId,
        toolNamePrefix: next.toolNamePrefix || undefined,
        ...(next.transport === 'streamable_http' && next.url ? { url: next.url } : {}),
        ...(Object.keys(next.headers).length > 0 ? { headers: next.headers } : {}),
      };

      onUpdate({
        openApiSpec: JSON.stringify(parsed, null, 2),
        apiHost: next.bridgeId,
        name: next.bridgeId,
      });
      onDirty?.();
    },
    [onDirty, onUpdate, tool.openApiSpec]
  );

  const updateSettings = (partial: Partial<McpConnectionSettings>) => {
    const next = { ...settings, ...partial };
    setSettings(next);
    syncSettingsToSpec(next);
  };

  const updateHeaders = (rows: McpHeaderRow[]) => {
    setHeaderRows(rows);
    updateSettings({ headers: mcpHeaderRowsToHeaders(rows) });
  };

  const buildConnectionPayload = () => {
    const resolved = buildResolvedMcpConnectionPayload(settings, environmentVariables);
    return {
      transport: resolved.transport,
      url: resolved.url,
      bridgeId: resolved.bridgeId,
      headers: resolved.headers,
      toolNamePrefix: resolved.toolNamePrefix,
      missingSecretRefs: resolved.missingSecretRefs,
    };
  };

  const ensureResolvableSecrets = (): string | null => {
    const missing = headersContainUnresolvedSecrets(settings.headers, environmentVariables);
    if (missing.length === 0) {
      return null;
    }

    return `Missing or unavailable guide secrets: ${missing.join(', ')}. Select an existing secret, create one here, or re-enter the value on the Environment tab.`;
  };

  const handleTestConnection = async () => {
    setErrorMessage(null);
    setStatusMessage(null);
    if (validationError) {
      setErrorMessage(validationError);
      setPanelState('discovery-failed');
      return;
    }

    const secretError = ensureResolvableSecrets();
    if (secretError) {
      setErrorMessage(secretError);
      setPanelState('discovery-failed');
      return;
    }

    const payload = buildConnectionPayload();
    setPanelState('testing');
    try {
      const result = await api.guides.guides.mcpToolSources.testConnection({
        connection: {
          transport: payload.transport,
          url: payload.url,
          bridgeId: payload.bridgeId,
          headers: payload.headers,
          toolNamePrefix: payload.toolNamePrefix,
        },
      });
      if (result.connected) {
        setPanelState('connected');
        const details = [result.message];
        if (result.serverName) {
          details.push(`Server: ${result.serverName}${result.serverVersion ? ` v${result.serverVersion}` : ''}`);
        }
        setStatusMessage(details.join(' '));
      } else {
        setPanelState('discovery-failed');
        setErrorMessage(result.message);
      }
    } catch (err) {
      setPanelState('discovery-failed');
      setErrorMessage(err instanceof Error ? err.message : 'Connection test failed.');
    }
  };

  const handleDiscover = async () => {
    setErrorMessage(null);
    setStatusMessage(null);
    if (validationError) {
      setErrorMessage(validationError);
      setPanelState('discovery-failed');
      return;
    }

    const secretError = ensureResolvableSecrets();
    if (secretError) {
      setErrorMessage(secretError);
      setPanelState('discovery-failed');
      return;
    }

    const payload = buildConnectionPayload();
    setPanelState('discovering');
    try {
      const existingTools = extractExistingMcpToolStates(tool.openApiSpec);
      const result = await api.guides.guides.mcpToolSources.discover({
        connection: {
          transport: payload.transport,
          url: payload.url,
          bridgeId: payload.bridgeId,
          headers: payload.headers,
          toolNamePrefix: payload.toolNamePrefix,
        },
        existingTools,
      });

      if (!result.success) {
        setPanelState('discovery-failed');
        setErrorMessage(result.message);
        return;
      }

      setPanelState('connected');
      setStatusMessage(result.message);
      setPendingDiscovery(result.tools);
    } catch (err) {
      setPanelState('discovery-failed');
      setErrorMessage(err instanceof Error ? err.message : 'Tool discovery failed.');
    }
  };

  const handleApplyDiscovery = () => {
    if (!pendingDiscovery) return;
    const nextSpec = applyMcpDiscoveryToSpec(tool.openApiSpec, settings, pendingDiscovery);
    setDiscoveredTools(pendingDiscovery);
    setPendingDiscovery(null);
    onUpdate({ openApiSpec: nextSpec });
    onDirty?.();
    setStatusMessage('Discovery changes applied to the tool source descriptor.');
  };

  const toggleToolSelected = (backingToolId: string, selected: boolean) => {
    const list = (pendingDiscovery ?? discoveredTools).map((t) =>
      t.backingToolId === backingToolId
        ? { ...t, selected, diffState: !selected && t.diffState === 'unchanged' ? 'disabled' : t.diffState }
        : t
    );
    if (pendingDiscovery) {
      setPendingDiscovery(list);
    } else {
      setDiscoveredTools(list);
    }
  };

  const displayTools = pendingDiscovery ?? discoveredTools;
  const showDiffReview = pendingDiscovery !== null;

  return (
    <div className="space-y-4" data-testid="mcp-connection-panel">
      <div className="rounded-md border border-teal-200 bg-teal-50 p-3 text-xs text-teal-900">
        MCP tools route through <code className="font-mono">client://mcp-bridge-…</code> and execute via the
        client bridge at runtime (D1 client-bridge-first).
      </div>

      <div className="grid gap-3 sm:grid-cols-2">
        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">Transport</label>
          <select
            value={settings.transport}
            onChange={(e) => updateSettings({ transport: e.target.value as McpTransport })}
            className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md"
            data-testid="mcp-transport"
          >
            <option value="streamable_http">Streamable HTTP (server-side discovery)</option>
            <option value="client_bridge">Client bridge (host-local MCP)</option>
          </select>
        </div>
        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">
            MCP bridge id <span className="text-red-500">*</span>
          </label>
          <input
            ref={inputRef}
            type="text"
            value={settings.bridgeId}
            onChange={(e) => updateSettings({ bridgeId: e.target.value })}
            className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md font-mono"
            data-testid="mcp-bridge-id"
          />
          <p className="text-xs text-gray-500 mt-1 font-mono">{buildMcpBridgeServerUrl(settings.bridgeId)}</p>
        </div>
      </div>

      {settings.transport === 'streamable_http' && (
        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">
            MCP server URL <span className="text-red-500">*</span>
          </label>
          <input
            type="url"
            value={settings.url}
            onChange={(e) => updateSettings({ url: e.target.value })}
            placeholder="https://mcp.example.com/mcp"
            className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md font-mono"
            data-testid="mcp-server-url"
          />
        </div>
      )}

      {settings.transport === 'client_bridge' && (
        <p className="text-xs text-gray-600 bg-gray-50 border border-gray-200 rounded-md p-2">
          Client bridge transport requires a connected client host to supply discovered tools. Test validates bridge
          configuration; discovery uses tools reported by the client bridge.
        </p>
      )}

      <div>
        <label className="block text-xs font-medium text-gray-700 mb-1">Tool name prefix</label>
        <input
          type="text"
          value={settings.toolNamePrefix}
          onChange={(e) => updateSettings({ toolNamePrefix: e.target.value })}
          className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md font-mono"
        />
      </div>

      {settings.transport === 'streamable_http' && (
        <div>
          <div className="flex items-center justify-between mb-1">
            <label className="block text-xs font-medium text-gray-700">HTTP headers (optional)</label>
            <button
              type="button"
              onClick={() =>
                updateHeaders([
                  ...headerRows,
                  { key: '', secretRefName: '', literalValue: '', useLiteral: false },
                ])
              }
              className="text-xs text-blue-600 hover:underline"
            >
              Add header
            </button>
          </div>
          <div className="space-y-3">
            {headerRows.length === 0 && (
              <p className="text-xs text-gray-500">
                Use headers such as <code className="font-mono">Authorization</code> with a guide secret for API keys.
              </p>
            )}
            {headerRows.map((row, index) => (
              <div key={index} className="rounded-md border border-gray-200 bg-white p-3 space-y-3">
                <div className="flex gap-2">
                  <input
                    type="text"
                    value={row.key}
                    placeholder="Header name"
                    onChange={(e) => {
                      const rows = [...headerRows];
                      rows[index] = { ...row, key: e.target.value };
                      updateHeaders(rows);
                    }}
                    className="flex-1 px-2 py-1 text-xs border border-gray-300 rounded-md font-mono"
                  />
                  <button
                    type="button"
                    onClick={() => updateHeaders(headerRows.filter((_, i) => i !== index))}
                    className="text-xs text-red-600 hover:underline"
                  >
                    Remove
                  </button>
                </div>

                {row.useLiteral ? (
                  <div className="space-y-2">
                    <input
                      type="text"
                      value={row.literalValue}
                      placeholder="Literal header value"
                      onChange={(e) => {
                        const rows = [...headerRows];
                        rows[index] = { ...row, literalValue: e.target.value };
                        updateHeaders(rows);
                      }}
                      className="w-full px-2 py-1 text-xs border border-gray-300 rounded-md font-mono"
                    />
                    <button
                      type="button"
                      onClick={() => {
                        const rows = [...headerRows];
                        rows[index] = { ...row, useLiteral: false, literalValue: '' };
                        updateHeaders(rows);
                      }}
                      className="text-xs text-blue-600 hover:underline"
                    >
                      Use guide secret instead
                    </button>
                  </div>
                ) : (
                  <div className="space-y-2">
                    <EnvironmentSecretRefField
                      label="Guide secret"
                      selectedVariableName={row.secretRefName}
                      variables={environmentVariables}
                      onVariablesChange={onEnvironmentVariablesChange}
                      onSelectedVariableNameChange={(name) => {
                        const rows = [...headerRows];
                        rows[index] = { ...row, secretRefName: name };
                        updateHeaders(rows);
                      }}
                      hint="Stored as {{secret:NAME}} in the tool source descriptor."
                    />
                    <button
                      type="button"
                      onClick={() => {
                        const rows = [...headerRows];
                        rows[index] = { ...row, useLiteral: true, secretRefName: '' };
                        updateHeaders(rows);
                      }}
                      className="text-xs text-gray-600 hover:underline"
                    >
                      Use literal value instead
                    </button>
                  </div>
                )}
              </div>
            ))}
          </div>
        </div>
      )}

      {validationError && (
        <p className="text-xs text-red-600" role="alert">
          {validationError}
        </p>
      )}

      <div className="flex flex-wrap gap-2">
        <button
          type="button"
          onClick={handleTestConnection}
          disabled={panelState === 'testing' || panelState === 'discovering'}
          className="inline-flex items-center gap-2 px-3 py-1.5 text-xs font-medium text-teal-700 bg-teal-50 border border-teal-200 rounded-md hover:bg-teal-100 disabled:opacity-50"
          data-testid="mcp-test-connection"
        >
          <FaPlug className="w-3 h-3" />
          {panelState === 'testing' ? 'Testing connection…' : 'Test connection'}
        </button>
        <button
          type="button"
          onClick={handleDiscover}
          disabled={panelState === 'testing' || panelState === 'discovering' || !!validationError}
          className="inline-flex items-center gap-2 px-3 py-1.5 text-xs font-medium text-blue-700 bg-blue-50 border border-blue-200 rounded-md hover:bg-blue-100 disabled:opacity-50"
          data-testid="mcp-discover-tools"
        >
          <FaSearch className="w-3 h-3" />
          {panelState === 'discovering' ? 'Discovering tools…' : 'Discover tools'}
        </button>
        {panelState === 'discovery-failed' && (
          <button
            type="button"
            onClick={handleDiscover}
            className="inline-flex items-center gap-2 px-3 py-1.5 text-xs font-medium text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50"
          >
            <FaRedo className="w-3 h-3" />
            Retry discovery
          </button>
        )}
      </div>

      {statusMessage && panelState !== 'discovery-failed' && (
        <p className="text-xs text-green-700 bg-green-50 border border-green-200 rounded-md p-2" aria-live="polite">
          {statusMessage}
        </p>
      )}

      {errorMessage && (
        <p className="text-xs text-red-700 bg-red-50 border border-red-200 rounded-md p-2" role="alert" aria-live="polite">
          {errorMessage}
        </p>
      )}

      {showDiffReview && pendingDiscovery && (
        <div className="rounded-md border border-amber-300 bg-amber-50 p-3 space-y-2">
          <p className="text-sm font-medium text-amber-900">Review discovery changes before applying</p>
          <p className="text-xs text-amber-800">
            Added {pendingDiscovery.filter((t) => t.diffState === 'added').length}, changed{' '}
            {pendingDiscovery.filter((t) => t.diffState === 'changed').length}, removed{' '}
            {pendingDiscovery.filter((t) => t.diffState === 'removed').length}, disabled{' '}
            {pendingDiscovery.filter((t) => t.diffState === 'disabled').length}
          </p>
          <button
            type="button"
            onClick={handleApplyDiscovery}
            className="inline-flex items-center gap-2 px-3 py-1.5 text-xs font-medium text-white bg-teal-600 rounded-md hover:bg-teal-700"
            data-testid="mcp-apply-discovery"
          >
            <FaCheck className="w-3 h-3" />
            Apply discovery to descriptor
          </button>
        </div>
      )}

      {displayTools.length > 0 && (
        <div className="space-y-2">
          <h4 className="text-sm font-medium text-gray-900">Discovered MCP tools</h4>
          {displayTools.map((row) => (
            <div
              key={row.backingToolId}
              className="flex items-start gap-3 p-3 bg-white border border-gray-200 rounded-md"
              data-testid={`mcp-tool-row-${row.backingToolId}`}
            >
              <input
                type="checkbox"
                checked={row.selected}
                disabled={row.diffState === 'removed'}
                onChange={(e) => toggleToolSelected(row.backingToolId, e.target.checked)}
                className="mt-1"
                aria-label={`Enable ${row.name}`}
              />
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2 flex-wrap">
                  <span className="text-sm font-medium text-gray-900">{row.name}</span>
                  {diffStateLabel(row.diffState) && (
                    <span
                      className={`inline-flex px-2 py-0.5 rounded text-xs font-medium ${diffStateChipClassName(row.diffState)}`}
                    >
                      {diffStateLabel(row.diffState)}
                    </span>
                  )}
                </div>
                <p className="text-xs text-gray-500 font-mono mt-0.5">id: {row.backingToolId}</p>
                {row.description && <p className="text-xs text-gray-600 mt-1">{row.description}</p>}
                <p className="text-xs text-gray-500 mt-1">
                  operationId: <code className="font-mono">{row.operationId}</code>
                </p>
              </div>
            </div>
          ))}
        </div>
      )}

      {panelState === 'discovering' && displayTools.length === 0 && (
        <p className="text-sm text-gray-600" aria-live="polite">
          Discovery in progress…
        </p>
      )}
    </div>
  );
}
