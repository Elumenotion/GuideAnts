import type { GuideantsChatElement, ToolCall, ToolResult } from 'guideants';
import { getApiOrigin } from '../../config/apiConfig';
import { withAuthFetchInit } from '../../services/authService';
import type { AppGuideContext } from './types';

type ToolArguments = Record<string, unknown>;

function parseToolArguments(call: ToolCall): unknown {
  const raw = call.function.arguments;
  if (typeof raw === 'string') {
    try {
      return JSON.parse(raw);
    } catch {
      return raw;
    }
  }
  return raw;
}

function toolResult(call: ToolCall, name: string, payload: unknown): ToolResult {
  return { toolCallId: call.id, name, content: JSON.stringify(payload) };
}

function isToolArguments(value: unknown): value is ToolArguments {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function parseToolArgumentsObject(call: ToolCall): ToolArguments {
  const parsed = parseToolArguments(call);
  return isToolArguments(parsed) ? parsed : {};
}

function tryParseJson(value: string): unknown {
  try {
    return JSON.parse(value);
  } catch {
    return value;
  }
}

function normalizeRequestBodyValue(value: unknown): unknown {
  if (typeof value !== 'string') {
    return value;
  }

  const trimmed = value.trim();
  if (!trimmed) {
    return value;
  }

  return tryParseJson(trimmed);
}

function getRequestBodyValue(args: ToolArguments): unknown {
  return normalizeRequestBodyValue(args.requestBody);
}

function getFieldValue(args: ToolArguments, key: string): unknown {
  if (Object.prototype.hasOwnProperty.call(args, key)) {
    return args[key];
  }

  const requestBody = getRequestBodyValue(args);
  if (isToolArguments(requestBody) && Object.prototype.hasOwnProperty.call(requestBody, key)) {
    return requestBody[key];
  }

  return undefined;
}

function readNonEmptyString(value: unknown): string | undefined {
  if (typeof value !== 'string') {
    return undefined;
  }

  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : undefined;
}

function truncateMessage(value: string, maxLength = 1200): string {
  if (value.length <= maxLength) {
    return value;
  }

  return `${value.slice(0, maxLength)}...`;
}

type SandboxScope = {
  projectId?: string;
  guideId?: string;
  notebookId?: string;
};

const scopedSandboxInputError = 'Provide projectId with either guideId or notebookId for scoped sandbox operations.';
const pythonContextRequiredError = 'Python sandbox operations must be done from either a notebook or guide builder context.';

function hasScope(scope: SandboxScope): boolean {
  return Boolean(scope.projectId || scope.guideId || scope.notebookId);
}

function readScopeFromArgs(args: ToolArguments): SandboxScope {
  return {
    projectId: readNonEmptyString(getFieldValue(args, 'projectId')),
    guideId: readNonEmptyString(getFieldValue(args, 'guideId')),
    notebookId: readNonEmptyString(getFieldValue(args, 'notebookId')),
  };
}

function readScopeFromContext(context: AppGuideContext): SandboxScope | null {
  const projectId = readNonEmptyString(context.projectId);
  const notebookId = readNonEmptyString(context.notebookId);
  const guideId = readNonEmptyString(context.guideId);

  if (projectId && notebookId) {
    return { projectId, notebookId };
  }

  if (projectId && guideId) {
    return { projectId, guideId };
  }

  return null;
}

function resolveScopedQuery(scope: SandboxScope): { query: URLSearchParams; error?: string } {
  const { projectId, guideId, notebookId } = scope;
  const hasProject = Boolean(projectId);
  const hasGuide = Boolean(guideId);
  const hasNotebook = Boolean(notebookId);

  if (!hasProject && !hasGuide && !hasNotebook) {
    return { query: new URLSearchParams() };
  }

  if (!hasProject || (hasGuide && hasNotebook) || (!hasGuide && !hasNotebook)) {
    return {
      query: new URLSearchParams(),
      error: scopedSandboxInputError,
    };
  }

  const query = new URLSearchParams();
  query.set('projectId', projectId!);
  if (hasGuide) {
    query.set('guideId', guideId!);
  } else {
    query.set('notebookId', notebookId!);
  }

  return { query };
}

function resolvePythonScopedQuery(args: ToolArguments, context: AppGuideContext): { query: URLSearchParams; error?: string } {
  const argScope = readScopeFromArgs(args);
  if (hasScope(argScope)) {
    return resolveScopedQuery(argScope);
  }

  const contextScope = readScopeFromContext(context);
  if (!contextScope) {
    return {
      query: new URLSearchParams(),
      error: pythonContextRequiredError,
    };
  }

  return resolveScopedQuery(contextScope);
}

function resolveOptionalScopedQuery(args: ToolArguments, context: AppGuideContext): { query: URLSearchParams; error?: string } {
  const argScope = readScopeFromArgs(args);
  if (hasScope(argScope)) {
    return resolveScopedQuery(argScope);
  }

  const contextScope = readScopeFromContext(context);
  if (!contextScope) {
    return { query: new URLSearchParams() };
  }

  return resolveScopedQuery(contextScope);
}

function readTextContentFromParsed(parsed: unknown): string | null {
  if (typeof parsed === 'string') {
    return parsed;
  }

  if (!isToolArguments(parsed)) {
    return null;
  }

  const args = parsed;
  const direct = getFieldValue(args, 'content');
  if (typeof direct === 'string') {
    return direct;
  }

  const requestBody = getRequestBodyValue(args);
  if (typeof requestBody === 'string') {
    return requestBody;
  }

  return null;
}

type SandboxAdminCallResult = {
  status: 'ok' | 'error';
  endpoint: string;
  httpStatus?: number;
  message?: string;
  data?: unknown;
  content?: string;
};

async function callSandboxAdminEndpoint(
  method: 'GET' | 'PUT' | 'POST',
  endpointSegment: string,
  options?: { query?: URLSearchParams; body?: string; contentType?: string },
): Promise<SandboxAdminCallResult> {
  const url = new URL(`/api/system-guide/sandbox-admin/${endpointSegment}`, getApiOrigin());
  for (const [key, value] of options?.query ?? []) {
    url.searchParams.set(key, value);
  }

  const endpoint = `${url.pathname}${url.search}`;
  const headers = new Headers();

  if (options?.body !== undefined) {
    headers.set('Content-Type', options.contentType ?? 'application/json');
  }

  let response: Response;
  try {
    response = await fetch(
      url.toString(),
      withAuthFetchInit({
        method,
        headers,
        body: options?.body,
      }),
    );
  } catch (error) {
    const message = error instanceof Error
      ? error.message
      : 'Network error while calling sandbox admin endpoint.';

    return {
      status: 'error',
      endpoint,
      message: truncateMessage(message),
    };
  }

  const rawBody = await response.text();
  if (!response.ok) {
    const baseMessage = rawBody.trim() || response.statusText || 'Sandbox admin request failed.';
    return {
      status: 'error',
      endpoint,
      httpStatus: response.status,
      message: truncateMessage(baseMessage),
    };
  }

  if (response.status === 204 || rawBody.trim().length === 0) {
    return {
      status: 'ok',
      endpoint,
      httpStatus: response.status,
    };
  }

  const contentType = response.headers.get('Content-Type')?.toLowerCase() ?? '';
  if (contentType.includes('application/json')) {
    try {
      return {
        status: 'ok',
        endpoint,
        httpStatus: response.status,
        data: JSON.parse(rawBody),
      };
    } catch {
      return {
        status: 'ok',
        endpoint,
        httpStatus: response.status,
        content: rawBody,
      };
    }
  }

  return {
    status: 'ok',
    endpoint,
    httpStatus: response.status,
    content: rawBody,
  };
}

export function registerGuideAntsAppBridge(
  chat: GuideantsChatElement,
  buildAppContext: () => AppGuideContext,
  isAdminGuide: boolean,
): void {
  chat.registerTool('AppEcho', async (call) => {
    const args = parseToolArguments(call);
    return toolResult(call, 'AppEcho', {
      status: 'ok',
      echo: args,
      context: buildAppContext(),
    });
  });

  if (!isAdminGuide) {
    return;
  }

  chat.registerTool('SandboxAdminGetHealth', async (call) => {
    const result = await callSandboxAdminEndpoint('GET', 'health');
    return toolResult(call, 'SandboxAdminGetHealth', result);
  });

  chat.registerTool('SandboxAdminGetRequirements', async (call) => {
    const args = parseToolArgumentsObject(call);
    const appContext = buildAppContext();
    const { query, error } = resolvePythonScopedQuery(args, appContext);
    if (error) {
      return toolResult(call, 'SandboxAdminGetRequirements', {
        status: 'error',
        endpoint: '/api/system-guide/sandbox-admin/requirements',
        message: error,
      });
    }

    const result = await callSandboxAdminEndpoint('GET', 'requirements', { query });
    return toolResult(call, 'SandboxAdminGetRequirements', result);
  });

  chat.registerTool('SandboxAdminSetRequirements', async (call) => {
    const parsed = parseToolArguments(call);
    const args = isToolArguments(parsed) ? parsed : {};
    const appContext = buildAppContext();
    const content = readTextContentFromParsed(parsed);
    if (content === null) {
      return toolResult(call, 'SandboxAdminSetRequirements', {
        status: 'error',
        endpoint: '/api/system-guide/sandbox-admin/requirements',
        message: 'content must be a string.',
      });
    }

    const { query, error } = resolvePythonScopedQuery(args, appContext);
    if (error) {
      return toolResult(call, 'SandboxAdminSetRequirements', {
        status: 'error',
        endpoint: '/api/system-guide/sandbox-admin/requirements',
        message: error,
      });
    }

    const result = await callSandboxAdminEndpoint('PUT', 'requirements', {
      query,
      body: content,
      contentType: 'text/plain',
    });
    return toolResult(call, 'SandboxAdminSetRequirements', result);
  });

  chat.registerTool('SandboxAdminGetAptPackages', async (call) => {
    const result = await callSandboxAdminEndpoint('GET', 'apt-packages');
    return toolResult(call, 'SandboxAdminGetAptPackages', result);
  });

  chat.registerTool('SandboxAdminSetAptPackages', async (call) => {
    const parsed = parseToolArguments(call);
    const content = readTextContentFromParsed(parsed);
    if (content === null) {
      return toolResult(call, 'SandboxAdminSetAptPackages', {
        status: 'error',
        endpoint: '/api/system-guide/sandbox-admin/apt-packages',
        message: 'content must be a string.',
      });
    }

    const result = await callSandboxAdminEndpoint('PUT', 'apt-packages', {
      body: content,
      contentType: 'text/plain',
    });
    return toolResult(call, 'SandboxAdminSetAptPackages', result);
  });

  chat.registerTool('SandboxAdminApply', async (call) => {
    const args = parseToolArgumentsObject(call);
    const appContext = buildAppContext();
    const { query, error } = resolveOptionalScopedQuery(args, appContext);
    if (error) {
      return toolResult(call, 'SandboxAdminApply', {
        status: 'error',
        endpoint: '/api/system-guide/sandbox-admin/apply',
        message: error,
      });
    }

    const result = await callSandboxAdminEndpoint('POST', 'apply', { query });
    return toolResult(call, 'SandboxAdminApply', result);
  });

  chat.registerTool('SandboxAdminGetApplyJob', async (call) => {
    const args = parseToolArgumentsObject(call);
    const jobId = typeof args.jobId === 'string' ? args.jobId.trim() : '';
    if (!jobId) {
      return toolResult(call, 'SandboxAdminGetApplyJob', {
        status: 'error',
        endpoint: '/api/system-guide/sandbox-admin/apply/jobs',
        message: 'jobId is required.',
      });
    }

    const result = await callSandboxAdminEndpoint('GET', `apply/jobs/${encodeURIComponent(jobId)}`);
    return toolResult(call, 'SandboxAdminGetApplyJob', result);
  });

  chat.registerTool('SandboxAdminGetSetupStatus', async (call) => {
    const args = parseToolArgumentsObject(call);
    const appContext = buildAppContext();
    const { query, error } = resolveOptionalScopedQuery(args, appContext);
    if (error) {
      return toolResult(call, 'SandboxAdminGetSetupStatus', {
        status: 'error',
        endpoint: '/api/system-guide/sandbox-admin/setup-status',
        message: error,
      });
    }

    const result = await callSandboxAdminEndpoint('GET', 'setup-status', { query });
    return toolResult(call, 'SandboxAdminGetSetupStatus', result);
  });

  chat.registerTool('SandboxAdminGetInstallScripts', async (call) => {
    const args = parseToolArgumentsObject(call);
    const appContext = buildAppContext();
    const { query, error } = resolvePythonScopedQuery(args, appContext);
    if (error) {
      return toolResult(call, 'SandboxAdminGetInstallScripts', {
        status: 'error',
        endpoint: '/api/system-guide/sandbox-admin/install-scripts',
        message: error,
      });
    }

    const result = await callSandboxAdminEndpoint('GET', 'install-scripts', { query });
    return toolResult(call, 'SandboxAdminGetInstallScripts', result);
  });

  chat.registerTool('SandboxAdminSetInstallScripts', async (call) => {
    const args = parseToolArgumentsObject(call);
    const appContext = buildAppContext();
    const { query, error } = resolvePythonScopedQuery(args, appContext);
    if (error) {
      return toolResult(call, 'SandboxAdminSetInstallScripts', {
        status: 'error',
        endpoint: '/api/system-guide/sandbox-admin/install-scripts',
        message: error,
      });
    }

    const content = readTextContentFromParsed(args);
    if (content === null) {
      return toolResult(call, 'SandboxAdminSetInstallScripts', {
        status: 'error',
        endpoint: '/api/system-guide/sandbox-admin/install-scripts',
        message: 'content must be a JSON string.',
      });
    }

    const result = await callSandboxAdminEndpoint('PUT', 'install-scripts', {
      query,
      body: content,
      contentType: 'application/json',
    });
    return toolResult(call, 'SandboxAdminSetInstallScripts', result);
  });
}
