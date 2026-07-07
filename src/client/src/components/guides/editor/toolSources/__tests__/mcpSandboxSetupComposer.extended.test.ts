import { describe, expect, it, vi } from 'vitest';
import type { CustomToolDto } from '../../../../../types/guides';
import { buildEmptyOpenApiDescriptor } from '../openApiDescriptorBuilder';
import {
  collectSandboxPackagesForToolUpdate,
  collectSandboxPackagesFromTools,
  composeSandboxStagingArtifacts,
  stageSandboxSetupForGuide,
} from '../mcpSandboxSetupComposer';
import { applyMcpDiscoveryToSpec, parseMcpConnectionSettings } from '../mcpToolSource';

function buildSandboxSpec(packageMeta: Record<string, unknown>): string {
  const spec = buildEmptyOpenApiDescriptor('mcp-connection');
  const settings = {
    ...parseMcpConnectionSettings(spec),
    runtimeExecution: 'sandbox_subprocess' as const,
    package: packageMeta,
  };
  return applyMcpDiscoveryToSpec(spec, settings, []);
}

describe('mcpSandboxSetupComposer extended', () => {
  it('composes pypi requirements without node apt dependency', () => {
    const artifacts = composeSandboxStagingArtifacts([
      { registryType: 'pypi', identifier: 'mcp-server', command: 'python', args: ['-m', 'mcp_server'] },
      { registryType: 'python', identifier: 'requests>=2.0', command: 'python', args: [] },
    ]);

    expect(artifacts.requirementsText).toContain('mcp-server');
    expect(artifacts.requirementsText).toContain('requests>=2.0');
    expect(artifacts.aptPackagesText).toBe('');
    expect(artifacts.installScriptsJson).toBe('{\n  "version": 1,\n  "scripts": []\n}');
  });

  it('quotes shell arguments that need escaping', () => {
    const artifacts = composeSandboxStagingArtifacts([
      {
        registryType: 'custom',
        identifier: "pkg'name",
        command: 'bash',
        args: ["echo 'hello'"],
      },
    ]);

    expect(artifacts.installScriptsJson).toContain("pkg'name");
    expect(artifacts.installScriptsJson).toContain('bash');
  });

  it('collects sandbox packages from custom tools metadata', () => {
    const tools: CustomToolDto[] = [
      {
        name: 'sandbox-mcp',
        openApiSpec: buildSandboxSpec({
          registryType: 'npm',
          identifier: '@acme/mcp',
          command: 'npx',
          args: ['-y', '@acme/mcp'],
        }),
      },
      {
        name: 'api-mcp',
        openApiSpec: buildEmptyOpenApiDescriptor('mcp-connection'),
      },
    ];

    const packages = collectSandboxPackagesFromTools(tools);
    expect(packages).toHaveLength(1);
    expect(packages[0].identifier).toBe('@acme/mcp');
  });

  it('collects packages for a tool update while preserving siblings', () => {
    const tools: CustomToolDto[] = [
      {
        name: 'first',
        openApiSpec: buildSandboxSpec({
          registryType: 'pypi',
          identifier: 'first-pkg',
          command: 'python',
          args: [],
        }),
      },
      {
        name: 'second',
        openApiSpec: buildEmptyOpenApiDescriptor('mcp-connection'),
      },
    ];

    const packages = collectSandboxPackagesForToolUpdate(tools, 1, {
      runtimeExecution: 'sandbox_subprocess',
      package: {
        registryType: 'pypi',
        identifier: 'second-pkg',
        command: 'python',
        args: [],
      },
    });

    expect(packages.map((pkg) => pkg.identifier).sort()).toEqual(['first-pkg', 'second-pkg']);
  });

  it('stages sandbox setup artifacts through the bridge', async () => {
    const setRequirements = vi.fn().mockResolvedValue({ status: 'ok' });
    const setInstallScripts = vi.fn().mockResolvedValue({ status: 'ok' });
    const getAptPackages = vi.fn().mockResolvedValue({ status: 'ok', content: 'curl\n' });
    const setAptPackages = vi.fn().mockResolvedValue({ status: 'ok' });

    const error = await stageSandboxSetupForGuide(
      { projectId: 'project-1', guideId: 'guide-1' },
      [{ registryType: 'npm', identifier: '@acme/mcp', command: 'npx', args: ['-y', '@acme/mcp'] }],
      { setRequirements, setInstallScripts, getAptPackages, setAptPackages },
    );

    expect(error).toBeNull();
    expect(setRequirements).toHaveBeenCalled();
    expect(setInstallScripts).toHaveBeenCalled();
    expect(setAptPackages).toHaveBeenCalledWith('curl\nnodejs\n');
  });

  it('returns bridge error messages when staging fails', async () => {
    const error = await stageSandboxSetupForGuide(
      { projectId: 'project-1', guideId: 'guide-1' },
      [{ registryType: 'pypi', identifier: 'pkg', command: 'python', args: [] }],
      {
        setRequirements: vi.fn().mockResolvedValue({ status: 'error', message: 'requirements failed' }),
        setInstallScripts: vi.fn(),
        getAptPackages: vi.fn(),
        setAptPackages: vi.fn(),
      },
    );

    expect(error).toBe('requirements failed');
  });

  it('returns install script staging errors', async () => {
    const error = await stageSandboxSetupForGuide(
      { projectId: 'project-1', guideId: 'guide-1' },
      [{ registryType: 'pypi', identifier: 'pkg', command: 'python', args: [] }],
      {
        setRequirements: vi.fn().mockResolvedValue({ status: 'ok' }),
        setInstallScripts: vi.fn().mockResolvedValue({ status: 'error', message: 'install failed' }),
        getAptPackages: vi.fn(),
        setAptPackages: vi.fn(),
      },
    );

    expect(error).toBe('install failed');
  });

  it('returns apt package staging errors for npm packages', async () => {
    const error = await stageSandboxSetupForGuide(
      { projectId: 'project-1', guideId: 'guide-1' },
      [{ registryType: 'npm', identifier: '@acme/mcp', command: 'npx', args: ['-y', '@acme/mcp'] }],
      {
        setRequirements: vi.fn().mockResolvedValue({ status: 'ok' }),
        setInstallScripts: vi.fn().mockResolvedValue({ status: 'ok' }),
        getAptPackages: vi.fn().mockResolvedValue({ status: 'ok', content: '' }),
        setAptPackages: vi.fn().mockResolvedValue({ status: 'error', message: 'apt failed' }),
      },
    );

    expect(error).toBe('apt failed');
  });

  it('quotes empty shell arguments defensively', () => {
    const artifacts = composeSandboxStagingArtifacts([
      {
        registryType: 'custom',
        identifier: 'pkg',
        command: 'bash',
        args: [''],
      },
    ]);

    expect(artifacts.installScriptsJson).toContain("''");
  });

  it('adds node apt dependency for npx-driven sandbox packages', () => {
    const artifacts = composeSandboxStagingArtifacts([
      {
        registryType: 'custom',
        identifier: '@acme/mcp',
        command: 'npx',
        args: ['-y', '@acme/mcp'],
      },
    ]);

    expect(artifacts.aptPackagesText).toBe('nodejs\n');
    expect(artifacts.installScriptsJson).toContain('npx');
  });

  it('returns null when there are no packages to stage', async () => {
    const error = await stageSandboxSetupForGuide(
      { projectId: 'project-1', guideId: 'guide-1' },
      [],
      {
        setRequirements: vi.fn(),
        setInstallScripts: vi.fn(),
        getAptPackages: vi.fn(),
        setAptPackages: vi.fn(),
      },
    );

    expect(error).toBeNull();
  });
});
