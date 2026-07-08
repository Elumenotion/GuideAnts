import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { CustomToolDto } from '../../../../../types/guides';
import { api } from '../../../../../services/api';
import {
  sandboxAdminApply,
  sandboxAdminGetApplyJob,
  sandboxAdminGetAptPackages,
  sandboxAdminGetSetupStatus,
  sandboxAdminSetAptPackages,
  sandboxAdminSetInstallScripts,
  sandboxAdminSetRequirements,
} from '../../../../../features/guideantsGuide/sandboxAdminBridge';
import { buildEmptyOpenApiDescriptor } from '../openApiDescriptorBuilder';
import { useMcpConnection } from '../useMcpConnection';
import { parseMcpConnectionSettings } from '../mcpToolSource';

vi.mock('../../../../../services/api', () => ({
  api: {
    guides: {
      guides: {
        mcpToolSources: {
          testConnection: vi.fn(),
          discover: vi.fn(),
        },
      },
    },
  },
}));

vi.mock('../../../../../features/guideantsGuide/sandboxAdminBridge', () => ({
  sandboxAdminApply: vi.fn(),
  sandboxAdminGetApplyJob: vi.fn(),
  sandboxAdminGetAptPackages: vi.fn(),
  sandboxAdminGetSetupStatus: vi.fn(),
  sandboxAdminSetAptPackages: vi.fn(),
  sandboxAdminSetInstallScripts: vi.fn(),
  sandboxAdminSetRequirements: vi.fn(),
}));

function buildTool(overrides: Partial<CustomToolDto> = {}): CustomToolDto {
  const openApiSpec = buildEmptyOpenApiDescriptor('mcp-connection');
  const parsed = JSON.parse(openApiSpec) as Record<string, unknown>;
  const source = parsed['x-guideants-tool-source'] as Record<string, unknown>;
  return {
    name: 'mcp-bridge',
    apiHost: String(source.bridgeId),
    openApiSpec,
    ...overrides,
  };
}

function renderMcpHook(overrides: Partial<Parameters<typeof useMcpConnection>[0]> = {}) {
  const tool = buildTool();
  const onUpdate = vi.fn();
  const onDirty = vi.fn();
  const hook = renderHook(() =>
    useMcpConnection({
      tool,
      toolIndex: 0,
      allTools: [tool],
      environmentVariables: [],
      projectId: 'project-1',
      guideId: 'guide-1',
      onUpdate,
      onDirty,
      ...overrides,
    }),
  );
  return { ...hook, onUpdate, onDirty, tool };
}

const discoveredToolRow = {
  backingToolId: 'search',
  name: 'search',
  title: 'Search',
  description: 'Search the web',
  schemaHash: 'abc',
  selected: true,
  diffState: 'added' as const,
  operationId: 'mcp_search',
  path: '/tools/search',
  method: 'post',
  schemaFragmentJson: JSON.stringify({
    path: '/tools/search',
    method: 'post',
    operation: {
      operationId: 'mcp_search',
      summary: 'Search',
      'x-guideants-mcp-tool': {
        backingToolId: 'search',
        schemaHash: 'abc',
        enabled: true,
      },
      requestBody: {
        required: true,
        content: { 'application/json': { schema: { type: 'object' } } },
      },
      responses: { '200': { description: 'ok' } },
    },
  }),
};

describe('useMcpConnection', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('updates OpenAPI spec when connection settings change', async () => {
    const { result, onUpdate, onDirty } = renderMcpHook();

    await act(async () => {
      await result.current.updateSettings({
        url: 'http://localhost:8080/mcp',
        toolNamePrefix: 'demo',
      });
    });

    expect(onUpdate).toHaveBeenCalled();
    expect(onDirty).toHaveBeenCalled();
    const nextSpec = JSON.parse(onUpdate.mock.calls.at(-1)?.[0].openApiSpec as string);
    expect(nextSpec['x-guideants-tool-source'].url).toBe('http://localhost:8080/mcp');
    expect(nextSpec['x-guideants-tool-source'].toolNamePrefix).toBe('demo');
  });

  it('marks the panel connected after a successful connection test', async () => {
    vi.mocked(api.guides.guides.mcpToolSources.testConnection).mockResolvedValue({
      connected: true,
      message: 'Connected',
      serverName: 'demo-mcp',
      serverVersion: '1.0.0',
    });

    const { result } = renderMcpHook();

    await act(async () => {
      await result.current.updateSettings({ url: 'http://localhost:8080/mcp' });
    });

    await act(async () => {
      await result.current.handleTestConnection();
    });

    await waitFor(() => {
      expect(result.current.panelState).toBe('connected');
      expect(result.current.statusMessage).toMatch(/Connected/);
      expect(result.current.statusMessage).toMatch(/demo-mcp/);
    });
  });

  it('reports failed connection tests and discovery responses', async () => {
    vi.mocked(api.guides.guides.mcpToolSources.testConnection).mockResolvedValue({
      connected: false,
      message: 'Connection refused',
    });
    vi.mocked(api.guides.guides.mcpToolSources.discover).mockResolvedValue({
      success: false,
      message: 'Discovery failed',
      tools: [],
      diff: { added: 0, changed: 0, removed: 0, disabled: 0 },
    });

    const { result } = renderMcpHook();

    await act(async () => {
      await result.current.updateSettings({ url: 'http://localhost:8080/mcp' });
    });

    await act(async () => {
      await result.current.handleTestConnection();
    });
    expect(result.current.panelState).toBe('discovery-failed');
    expect(result.current.errorMessage).toBe('Connection refused');

    await act(async () => {
      await result.current.handleDiscover();
    });
    expect(result.current.errorMessage).toBe('Discovery failed');
  });

  it('switches runtime execution mode and blocks apply without guide scope', async () => {
    const { result } = renderMcpHook({
      projectId: undefined,
      guideId: undefined,
    });

    act(() => {
      result.current.handleRuntimeExecutionChange('sandbox_subprocess');
    });

    await act(async () => {
      await result.current.runApply();
    });

    expect(result.current.panelState).toBe('apply-failed');
    expect(result.current.errorMessage).toMatch(/Save the guide in a project/i);
  });

  it('surfaces secret resolution failures before discovery', async () => {
    const tool = buildTool();
    const settings = parseMcpConnectionSettings(tool.openApiSpec);
    const { result } = renderMcpHook({
      tool,
      environmentVariables: [],
    });

    await act(async () => {
      await result.current.updateSettings({
        ...settings,
        url: 'http://localhost:8080/mcp',
        headers: { Authorization: '{{secret:MISSING}}' },
      });
    });

    await act(async () => {
      await result.current.handleDiscover();
    });

    expect(result.current.panelState).toBe('discovery-failed');
    expect(result.current.errorMessage).toMatch(/Missing or unavailable guide secrets/i);
  });

  it('stores discovered tools for review and applies them to the descriptor', async () => {
    vi.mocked(api.guides.guides.mcpToolSources.discover).mockResolvedValue({
      success: true,
      message: 'Found tools',
      tools: [{ ...discoveredToolRow, diffState: 'unchanged' }],
      diff: { added: 0, changed: 0, removed: 0, disabled: 0 },
    });

    const { result, onUpdate, onDirty } = renderMcpHook();

    await act(async () => {
      await result.current.updateSettings({ url: 'http://localhost:8080/mcp' });
    });

    await act(async () => {
      await result.current.handleDiscover();
    });

    await waitFor(() => {
      expect(result.current.pendingDiscovery).toHaveLength(1);
      expect(result.current.showDiffReview).toBe(true);
    });

    act(() => {
      result.current.toggleToolSelected('search', false);
    });

    expect(result.current.pendingDiscovery?.[0]).toMatchObject({
      backingToolId: 'search',
      selected: false,
      diffState: 'disabled',
    });

    act(() => {
      result.current.handleApplyDiscovery();
    });

    expect(onUpdate).toHaveBeenCalledWith(expect.objectContaining({ openApiSpec: expect.any(String) }));
    expect(onDirty).toHaveBeenCalled();
    expect(result.current.pendingDiscovery).toBeNull();
  });

  it('updates header rows and handles API failures during test and discovery', async () => {
    vi.mocked(api.guides.guides.mcpToolSources.testConnection).mockRejectedValue(
      new Error('Network down'),
    );
    vi.mocked(api.guides.guides.mcpToolSources.discover).mockRejectedValue(
      new Error('Discovery timeout'),
    );

    const { result } = renderMcpHook();

    await act(async () => {
      await result.current.updateSettings({ url: 'http://localhost:8080/mcp' });
    });

    act(() => {
      result.current.updateHeaders([
        { key: 'X-Test', secretRefName: '', literalValue: 'value', useLiteral: true },
      ]);
    });

    await act(async () => {
      await result.current.handleTestConnection();
    });
    expect(result.current.errorMessage).toBe('Network down');

    await act(async () => {
      await result.current.handleDiscover();
    });
    expect(result.current.errorMessage).toBe('Discovery timeout');
  });

  it('blocks connection tests when settings fail validation', async () => {
    const { result } = renderMcpHook();

    await act(async () => {
      await result.current.handleTestConnection();
    });
    expect(result.current.panelState).toBe('discovery-failed');
    expect(result.current.errorMessage).toBeTruthy();

    await act(async () => {
      await result.current.handleDiscover();
    });
    expect(result.current.errorMessage).toBeTruthy();
  });

  it('ignores invalid OpenAPI specs when syncing settings updates', async () => {
    const tool = buildTool({ openApiSpec: '{bad-json' });
    const onUpdate = vi.fn();
    const { result } = renderMcpHook({ tool, onUpdate });

    await act(async () => {
      await result.current.updateSettings({ url: 'http://localhost:8080/mcp' });
    });

    expect(onUpdate).not.toHaveBeenCalled();
  });

  it('re-parses connection settings when the tool descriptor changes externally', async () => {
    const tool = buildTool();
    const { result, rerender } = renderHook(
      ({ currentTool }) =>
        useMcpConnection({
          tool: currentTool,
          toolIndex: 0,
          allTools: [currentTool],
          environmentVariables: [],
          projectId: 'project-1',
          guideId: 'guide-1',
          onUpdate: vi.fn(),
        }),
      { initialProps: { currentTool: tool } },
    );

    const nextSpec = buildEmptyOpenApiDescriptor('mcp-connection');
    const parsed = JSON.parse(nextSpec) as Record<string, unknown>;
    const source = parsed['x-guideants-tool-source'] as Record<string, unknown>;
    source.toolNamePrefix = 'external';
    const updatedTool = {
      ...tool,
      openApiSpec: JSON.stringify(parsed, null, 2),
    };

    rerender({ currentTool: updatedTool });

    await waitFor(() => {
      expect(result.current.settings.toolNamePrefix).toBe('external');
    });
  });

  it('prompts for sandbox apply before testing when setup is not applied', async () => {
    vi.mocked(sandboxAdminGetSetupStatus).mockResolvedValue({
      status: 'ok',
      data: {
        overallStatus: 'pending',
        requirements: { pendingApply: true },
        installScripts: { pendingApply: false },
      },
    } as never);

    const { result } = renderMcpHook();

    await act(async () => {
      result.current.handleRuntimeExecutionChange('sandbox_subprocess');
    });

    await act(async () => {
      await result.current.handleTestConnection();
    });

    expect(result.current.showApplyConfirm).toBe(true);
  });

  it('completes sandbox apply when staging and job polling succeed', async () => {
    vi.mocked(sandboxAdminGetSetupStatus).mockResolvedValue({
      status: 'ok',
      data: {
        overallStatus: 'ready',
        requirements: { pendingApply: false },
        installScripts: { pendingApply: false },
      },
    } as never);
    vi.mocked(sandboxAdminApply).mockResolvedValue({
      status: 'ok',
      data: { jobId: 'job-1' },
    } as never);
    vi.mocked(sandboxAdminGetApplyJob).mockResolvedValue({
      status: 'ok',
      data: { status: 'succeeded' },
    } as never);

    const { result } = renderMcpHook();

    await act(async () => {
      result.current.handleRuntimeExecutionChange('sandbox_subprocess');
      await result.current.runApply();
    });

    expect(result.current.panelState).toBe('connected');
    expect(result.current.statusMessage).toMatch(/Sandbox packages applied/i);
    expect(sandboxAdminApply).toHaveBeenCalled();
  });

  it('opens sandbox apply confirmation via requestApply', () => {
    const { result } = renderMcpHook();

    act(() => {
      result.current.requestApply();
    });

    expect(result.current.showApplyConfirm).toBe(true);
  });

  it('confirmApply runs sandbox apply then retries the deferred connection test', async () => {
    vi.mocked(sandboxAdminGetSetupStatus).mockResolvedValue({
      status: 'ok',
      data: {
        overallStatus: 'pending',
        requirements: { pendingApply: true },
        installScripts: { pendingApply: false },
      },
    } as never);
    vi.mocked(sandboxAdminSetRequirements).mockResolvedValue({ status: 'ok', message: '' } as never);
    vi.mocked(sandboxAdminSetInstallScripts).mockResolvedValue({ status: 'ok', message: '' } as never);
    vi.mocked(sandboxAdminGetAptPackages).mockResolvedValue({ status: 'ok', content: '', message: '' } as never);
    vi.mocked(sandboxAdminSetAptPackages).mockResolvedValue({ status: 'ok', message: '' } as never);
    vi.mocked(sandboxAdminApply).mockResolvedValue({
      status: 'ok',
      data: {},
    } as never);
    vi.mocked(api.guides.guides.mcpToolSources.testConnection).mockResolvedValue({
      connected: true,
      message: 'Connected after apply',
    });

    const { result } = renderMcpHook();

    await act(async () => {
      result.current.handleRuntimeExecutionChange('sandbox_subprocess');
    });

    await act(async () => {
      await result.current.handleTestConnection();
    });

    expect(result.current.showApplyConfirm).toBe(true);

    await act(async () => {
      await result.current.confirmApply();
    });

    expect(sandboxAdminApply).toHaveBeenCalled();
    expect(api.guides.guides.mcpToolSources.testConnection).toHaveBeenCalled();
    expect(result.current.showApplyConfirm).toBe(false);
    expect(result.current.panelState).toBe('connected');
  });

  it('confirmApply runs sandbox apply then retries deferred discovery', async () => {
    vi.mocked(sandboxAdminGetSetupStatus).mockResolvedValue({
      status: 'ok',
      data: {
        overallStatus: 'pending',
        requirements: { pendingApply: true },
        installScripts: { pendingApply: false },
      },
    } as never);
    vi.mocked(sandboxAdminSetRequirements).mockResolvedValue({ status: 'ok', message: '' } as never);
    vi.mocked(sandboxAdminSetInstallScripts).mockResolvedValue({ status: 'ok', message: '' } as never);
    vi.mocked(sandboxAdminGetAptPackages).mockResolvedValue({ status: 'ok', content: '', message: '' } as never);
    vi.mocked(sandboxAdminSetAptPackages).mockResolvedValue({ status: 'ok', message: '' } as never);
    vi.mocked(sandboxAdminApply).mockResolvedValue({
      status: 'ok',
      data: {},
    } as never);
    vi.mocked(api.guides.guides.mcpToolSources.discover).mockResolvedValue({
      success: true,
      message: 'Found tools after apply',
      tools: [discoveredToolRow],
      diff: { added: 1, changed: 0, removed: 0, disabled: 0 },
    });

    const { result } = renderMcpHook();

    await act(async () => {
      result.current.handleRuntimeExecutionChange('sandbox_subprocess');
    });

    await act(async () => {
      await result.current.handleDiscover();
    });

    expect(result.current.showApplyConfirm).toBe(true);

    await act(async () => {
      await result.current.confirmApply();
    });

    expect(sandboxAdminApply).toHaveBeenCalled();
    expect(api.guides.guides.mcpToolSources.discover).toHaveBeenCalled();
    expect(result.current.pendingDiscovery).toHaveLength(1);
  });
});
