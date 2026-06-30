import { useCallback, useEffect, useMemo, useState } from 'react';
import { api } from '../../../../services/api';
import type { CustomToolDto, EnvironmentVariableDto } from '../../../../types/guides';
import {
  sandboxAdminApply,
  sandboxAdminGetApplyJob,
  sandboxAdminGetAptPackages,
  sandboxAdminGetSetupStatus,
  sandboxAdminSetAptPackages,
  sandboxAdminSetInstallScripts,
  sandboxAdminSetRequirements,
} from '../../../../features/guideantsGuide/sandboxAdminBridge';
import {
  applyMcpDiscoveryToSpec,
  buildResolvedMcpConnectionPayload,
  extractExistingMcpToolStates,
  headersContainUnresolvedSecrets,
  mcpHeaderRowsToHeaders,
  parseMcpConnectionSettings,
  parseMcpHeaderRows,
  validateMcpConnectionSettings,
} from './mcpToolSource';
import type {
  McpConnectionPanelState,
  McpConnectionSettings,
  McpDiscoveredToolRow,
  McpHeaderRow,
  McpRuntimeExecution,
} from './mcpToolSourceTypes';
import {
  buildMcpDispatchUrl,
  defaultPackageDescriptor,
  deriveDiscoveryTransport,
  mapSetupStatusFromResponse,
  type McpSandboxSetupStatusKind,
} from './mcpRuntimeMode';
import { stageSandboxSetupForGuide } from './mcpSandboxSetupComposer';

export interface UseMcpConnectionOptions {
  tool: CustomToolDto;
  toolIndex: number;
  allTools: CustomToolDto[];
  environmentVariables: EnvironmentVariableDto[];
  projectId?: string;
  guideId?: string;
  onUpdate: (updates: Partial<CustomToolDto>) => void;
  onDirty?: () => void;
}

export function useMcpConnection({
  tool,
  toolIndex,
  allTools,
  environmentVariables,
  projectId,
  guideId,
  onUpdate,
  onDirty,
}: UseMcpConnectionOptions) {
  const initialSettings = useMemo(() => parseMcpConnectionSettings(tool.openApiSpec), [tool.openApiSpec]);
  const [settings, setSettings] = useState<McpConnectionSettings>(initialSettings);
  const [panelState, setPanelState] = useState<McpConnectionPanelState>('idle');
  const [statusMessage, setStatusMessage] = useState<string | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [discoveredTools, setDiscoveredTools] = useState<McpDiscoveredToolRow[]>([]);
  const [pendingDiscovery, setPendingDiscovery] = useState<McpDiscoveredToolRow[] | null>(null);
  const [headerRows, setHeaderRows] = useState<McpHeaderRow[]>(() => parseMcpHeaderRows(initialSettings.headers));
  const [setupStatus, setSetupStatus] = useState<McpSandboxSetupStatusKind>('unknown');
  const [showApplyConfirm, setShowApplyConfirm] = useState(false);
  const [applyTrigger, setApplyTrigger] = useState<'test' | 'install' | null>(null);

  const scope = projectId && guideId ? { projectId, guideId } : null;
  const validationError = validateMcpConnectionSettings(settings);
  const dispatchUrl = buildMcpDispatchUrl(settings.bridgeId, settings.runtimeExecution);

  const syncSettingsToSpec = useCallback(
    (next: McpConnectionSettings) => {
      let parsed: Record<string, unknown>;
      try {
        parsed = JSON.parse(tool.openApiSpec);
      } catch {
        return;
      }

      parsed.servers = [
        {
          url: buildMcpDispatchUrl(next.bridgeId, next.runtimeExecution),
          description:
            next.runtimeExecution === 'sandbox_subprocess'
              ? 'MCP sandbox subprocess'
              : 'MCP API execution',
        },
      ];
      parsed['x-guideants-tool-source'] = {
        kind: 'mcp',
        runtimeExecution: next.runtimeExecution,
        discoveryTransport: next.discoveryTransport,
        bridgeId: next.bridgeId,
        toolNamePrefix: next.toolNamePrefix || undefined,
        ...(next.runtimeExecution === 'api' && next.url ? { url: next.url } : {}),
        ...(next.package ? { package: next.package } : {}),
        ...(next.environmentVariables?.length ? { environmentVariables: next.environmentVariables } : {}),
        ...(Object.keys(next.headers).length > 0 ? { headers: next.headers } : {}),
      };

      onUpdate({
        openApiSpec: JSON.stringify(parsed, null, 2),
        apiHost: next.bridgeId,
        name: next.bridgeId,
      });
      onDirty?.();
    },
    [onDirty, onUpdate, tool.openApiSpec],
  );

  const refreshSetupStatus = useCallback(async () => {
    if (!scope || settings.runtimeExecution !== 'sandbox_subprocess') {
      return;
    }

    const result = await sandboxAdminGetSetupStatus(scope);
    if (result.status === 'ok') {
      setSetupStatus(mapSetupStatusFromResponse(result.data));
    }
  }, [scope, settings.runtimeExecution]);

  const stageSandboxSetup = useCallback(
    async (next: McpConnectionSettings) => {
      if (!scope || next.runtimeExecution !== 'sandbox_subprocess' || !next.package) {
        return null;
      }

      return stageSandboxSetupForGuide(scope, [next.package], {
        setRequirements: async (guideScope, content) => {
          const result = await sandboxAdminSetRequirements(guideScope, content);
          return { status: result.status, message: result.message };
        },
        setInstallScripts: async (guideScope, content) => {
          const result = await sandboxAdminSetInstallScripts(guideScope, content);
          return { status: result.status, message: result.message };
        },
        getAptPackages: async () => {
          const result = await sandboxAdminGetAptPackages();
          return { status: result.status, content: result.content, message: result.message };
        },
        setAptPackages: async (content) => {
          const result = await sandboxAdminSetAptPackages(content);
          return { status: result.status, message: result.message };
        },
      });
    },
    [scope],
  );

  const updateSettings = useCallback(
    async (partial: Partial<McpConnectionSettings>) => {
      const next = { ...settings, ...partial };
      setSettings(next);
      syncSettingsToSpec(next);

      if (next.runtimeExecution === 'sandbox_subprocess' && next.package && scope) {
        const stageError = await stageSandboxSetup(next);
        if (stageError) {
          setErrorMessage(stageError);
          setSetupStatus('drift');
        } else {
          await refreshSetupStatus();
        }
      }
    },
    [refreshSetupStatus, scope, settings, stageSandboxSetup, syncSettingsToSpec],
  );

  const updateHeaders = (rows: McpHeaderRow[]) => {
    setHeaderRows(rows);
    void updateSettings({ headers: mcpHeaderRowsToHeaders(rows) });
  };

  const buildConnectionPayload = () => {
    const resolved = buildResolvedMcpConnectionPayload(settings, environmentVariables);
    return {
      runtimeExecution: resolved.runtimeExecution,
      discoveryTransport: resolved.discoveryTransport,
      url: resolved.url,
      bridgeId: resolved.bridgeId,
      headers: resolved.headers,
      toolNamePrefix: resolved.toolNamePrefix,
      package: resolved.package,
      environmentVariables: resolved.environmentVariables,
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

  const runApply = useCallback(async () => {
    if (!scope) {
      setErrorMessage('Save the guide in a project before applying sandbox packages.');
      setPanelState('apply-failed');
      return;
    }

    setPanelState('applying');
    setErrorMessage(null);
    setStatusMessage(null);

    const stageError = await stageSandboxSetup(settings);
    if (stageError) {
      setPanelState('apply-failed');
      setErrorMessage(stageError);
      return;
    }

    const applyResult = await sandboxAdminApply(scope);
    if (applyResult.status === 'error') {
      setPanelState('apply-failed');
      setErrorMessage(applyResult.message ?? 'Sandbox apply failed.');
      return;
    }

    const jobId =
      applyResult.data && typeof applyResult.data === 'object' && 'jobId' in applyResult.data
        ? String((applyResult.data as Record<string, unknown>).jobId)
        : '';

    if (jobId) {
      for (let attempt = 0; attempt < 60; attempt += 1) {
        await new Promise((resolve) => setTimeout(resolve, 1000));
        const jobResult = await sandboxAdminGetApplyJob(jobId);
        if (jobResult.status === 'error') {
          setPanelState('apply-failed');
          setErrorMessage(jobResult.message ?? 'Failed to poll sandbox apply job.');
          return;
        }

        const jobData = jobResult.data as Record<string, unknown> | undefined;
        const status = typeof jobData?.status === 'string' ? jobData.status : '';
        if (status === 'succeeded') {
          break;
        }
        if (status === 'failed') {
          setPanelState('apply-failed');
          setErrorMessage(typeof jobData?.error === 'string' ? jobData.error : 'Sandbox apply failed.');
          return;
        }
      }
    }

    await refreshSetupStatus();
    setPanelState('connected');
    setStatusMessage('Sandbox packages applied for this guide scope.');
  }, [refreshSetupStatus, scope, settings, stageSandboxSetup]);

  const handleTestConnection = async () => {
    if (settings.runtimeExecution === 'sandbox_subprocess') {
      setApplyTrigger('test');
      setShowApplyConfirm(true);
      return;
    }

    await executeTestConnection();
  };

  const executeTestConnection = async () => {
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
          runtimeExecution: payload.runtimeExecution,
          discoveryTransport: payload.discoveryTransport,
          url: payload.url,
          bridgeId: payload.bridgeId,
          headers: payload.headers,
          toolNamePrefix: payload.toolNamePrefix,
          package: payload.package,
          environmentVariables: payload.environmentVariables,
        },
      });
      if (result.connected) {
        setPanelState('connected');
        const details = [result.message];
        if (result.serverName) {
          details.push(
            `Server: ${result.serverName}${result.serverVersion ? ` v${result.serverVersion}` : ''}`,
          );
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
    if (settings.runtimeExecution === 'sandbox_subprocess' && setupStatus !== 'applied') {
      setApplyTrigger('install');
      setShowApplyConfirm(true);
      return;
    }

    await executeDiscover();
  };

  const executeDiscover = async () => {
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
          runtimeExecution: payload.runtimeExecution,
          discoveryTransport: payload.discoveryTransport,
          url: payload.url,
          bridgeId: payload.bridgeId,
          headers: payload.headers,
          toolNamePrefix: payload.toolNamePrefix,
          package: payload.package,
          environmentVariables: payload.environmentVariables,
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
    const list = (pendingDiscovery ?? discoveredTools).map((row) =>
      row.backingToolId === backingToolId
        ? {
            ...row,
            selected,
            diffState: !selected && row.diffState === 'unchanged' ? 'disabled' : row.diffState,
          }
        : row,
    );
    if (pendingDiscovery) {
      setPendingDiscovery(list);
    } else {
      setDiscoveredTools(list);
    }
  };

  const handleRuntimeExecutionChange = (runtimeExecution: McpRuntimeExecution) => {
    void updateSettings({
      runtimeExecution,
      discoveryTransport: deriveDiscoveryTransport(runtimeExecution),
      ...(runtimeExecution === 'sandbox_subprocess'
        ? { package: settings.package ?? defaultPackageDescriptor() }
        : {}),
    });
  };

  const confirmApply = async () => {
    setShowApplyConfirm(false);
    await runApply();
    if (applyTrigger === 'test') {
      await executeTestConnection();
    } else if (applyTrigger === 'install') {
      await executeDiscover();
    }
    setApplyTrigger(null);
  };

  const requestApply = () => {
    setApplyTrigger(null);
    setShowApplyConfirm(true);
  };

  useEffect(() => {
    void refreshSetupStatus();
  }, [refreshSetupStatus]);

  const displayTools = pendingDiscovery ?? discoveredTools;
  const showDiffReview = pendingDiscovery !== null;
  const isBusy = panelState === 'testing' || panelState === 'discovering' || panelState === 'applying';

  return {
    settings,
    headerRows,
    panelState,
    statusMessage,
    errorMessage,
    validationError,
    dispatchUrl,
    setupStatus,
    showApplyConfirm,
    setShowApplyConfirm,
    confirmApply,
    requestApply,
    displayTools,
    showDiffReview,
    pendingDiscovery,
    isBusy,
    updateSettings,
    updateHeaders,
    handleRuntimeExecutionChange,
    handleTestConnection,
    handleDiscover,
    handleApplyDiscovery,
    toggleToolSelected,
    runApply,
    toolIndex,
    allTools,
  };
}
