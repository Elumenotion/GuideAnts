import type { CustomToolDto } from '../../../../types/guides';
import type { McpPackageDescriptor, McpConnectionSettings } from './mcpToolSourceTypes';
import { parseMcpToolSourceMetadata } from './mcpToolSource';

export interface McpSandboxStagingArtifacts {
  requirementsText: string;
  aptPackagesText: string;
  installScriptsJson: string;
}

export function composeSandboxStagingArtifacts(
  packages: McpPackageDescriptor[],
): McpSandboxStagingArtifacts {
  const requirements = new Set<string>();
  const scripts: Array<{
    id: string;
    order: number;
    name: string;
    scriptType: string;
    script: string;
  }> = [];
  let needsNode = false;
  let order = 1;

  for (const pkg of packages) {
    const registryType = pkg.registryType.trim().toLowerCase();
    const identifier = pkg.identifier.trim();
    if (!identifier) {
      continue;
    }

    if (registryType === 'pypi' || registryType === 'python') {
      requirements.add(identifier);
      continue;
    }

    if (registryType === 'npm' || registryType === 'node') {
      needsNode = true;
      scripts.push({
        id: `mcp-npm-${sanitizeScriptId(identifier)}`,
        order: order++,
        name: `Install MCP npm package ${identifier}`,
        scriptType: 'Bash',
        script: `npm install -g ${shellQuote(identifier)}`,
      });
      continue;
    }

    if (pkg.command === 'npx' || pkg.command === 'npm') {
      needsNode = true;
    }

    const args = pkg.args?.length ? pkg.args.map(shellQuote).join(' ') : '';
    const commandLine = args ? `${shellQuote(pkg.command)} ${args}` : shellQuote(pkg.command);
    scripts.push({
      id: `mcp-pkg-${sanitizeScriptId(identifier)}`,
      order: order++,
      name: `Prepare MCP package ${identifier}`,
      scriptType: 'Bash',
      script: commandLine,
    });
  }

  return {
    requirementsText: requirements.size > 0 ? `${[...requirements].sort().join('\n')}\n` : '',
    aptPackagesText: needsNode ? 'nodejs\n' : '',
    installScriptsJson: JSON.stringify({ version: 1, scripts }, null, 2),
  };
}

export function collectSandboxPackagesFromTools(customTools: CustomToolDto[]): McpPackageDescriptor[] {
  const packages: McpPackageDescriptor[] = [];
  for (const tool of customTools) {
    const meta = parseMcpToolSourceMetadata(tool.openApiSpec);
    if (meta?.runtimeExecution === 'sandbox_subprocess' && meta.package) {
      packages.push(meta.package);
    }
  }
  return packages;
}

export function collectSandboxPackagesForToolUpdate(
  allTools: CustomToolDto[],
  toolIndex: number,
  nextSettings: Pick<McpConnectionSettings, 'runtimeExecution' | 'package'>,
): McpPackageDescriptor[] {
  const packages: McpPackageDescriptor[] = [];
  for (let index = 0; index < allTools.length; index += 1) {
    if (index === toolIndex) {
      if (nextSettings.runtimeExecution === 'sandbox_subprocess' && nextSettings.package) {
        packages.push(nextSettings.package);
      }
      continue;
    }

    const meta = parseMcpToolSourceMetadata(allTools[index].openApiSpec);
    if (meta?.runtimeExecution === 'sandbox_subprocess' && meta.package) {
      packages.push(meta.package);
    }
  }
  return packages;
}

function sanitizeScriptId(value: string): string {
  const sanitized = value.replace(/[^a-zA-Z0-9]+/g, '-').replace(/^-+|-+$/g, '').toLowerCase();
  return sanitized || 'pkg';
}

function shellQuote(value: string): string {
  if (!value) {
    return "''";
  }

  if (/^[a-zA-Z0-9@._/:-]+$/.test(value)) {
    return value;
  }

  return `'${value.replace(/'/g, "'\\''")}'`;
}

async function mergeAptPackages(existing: string, additions: string): Promise<string> {
  const lines = new Set<string>();
  for (const line of `${existing}\n${additions}`.split('\n')) {
    const trimmed = line.trim();
    if (trimmed && !trimmed.startsWith('#')) {
      lines.add(trimmed);
    }
  }
  return lines.size > 0 ? `${[...lines].sort().join('\n')}\n` : '';
}

export async function stageSandboxSetupForGuide(
  scope: { projectId: string; guideId: string },
  packages: McpPackageDescriptor[],
  bridge: {
    setRequirements: (scope: { projectId: string; guideId: string }, content: string) => Promise<{ status: string; message?: string }>;
    setInstallScripts: (scope: { projectId: string; guideId: string }, content: string) => Promise<{ status: string; message?: string }>;
    getAptPackages: () => Promise<{ status: string; content?: string; message?: string }>;
    setAptPackages: (content: string) => Promise<{ status: string; message?: string }>;
  },
): Promise<string | null> {
  if (packages.length === 0) {
    return null;
  }

  const artifacts = composeSandboxStagingArtifacts(packages);
  const requirementsResult = await bridge.setRequirements(scope, artifacts.requirementsText);
  if (requirementsResult.status === 'error') {
    return requirementsResult.message ?? 'Failed to stage sandbox requirements.';
  }

  const installScriptsResult = await bridge.setInstallScripts(scope, artifacts.installScriptsJson);
  if (installScriptsResult.status === 'error') {
    return installScriptsResult.message ?? 'Failed to stage sandbox install scripts.';
  }

  if (artifacts.aptPackagesText) {
    const aptRead = await bridge.getAptPackages();
    const merged = await mergeAptPackages(aptRead.content ?? '', artifacts.aptPackagesText);
    const aptResult = await bridge.setAptPackages(merged);
    if (aptResult.status === 'error') {
      return aptResult.message ?? 'Failed to stage sandbox apt packages.';
    }
  }

  return null;
}
