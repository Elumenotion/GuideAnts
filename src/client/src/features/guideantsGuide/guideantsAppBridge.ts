import type { GuideantsChatElement, ToolCall, ToolResult } from 'guideants';
import { getApiOrigin } from '../../config/apiConfig';
import { withAuthFetchInit } from '../../services/authService';
import { api } from '../../services/api';
import type { AppGuideContext, GuideAppActions, GuideViewContext } from './types';

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

type AppToolResult = {
  status: 'ok' | 'error';
  action: string;
  message?: string;
  httpStatus?: number;
  data?: unknown;
  navigatedTo?: string;
};

function appError(action: string, message: string, httpStatus?: number): AppToolResult {
  return { status: 'error', action, message: truncateMessage(message), httpStatus };
}

function describeError(error: unknown): { message: string; httpStatus?: number } {
  if (error && typeof error === 'object') {
    const candidate = error as { message?: unknown; status?: unknown };
    const message = typeof candidate.message === 'string' && candidate.message.trim().length > 0
      ? candidate.message
      : 'Request failed.';
    const httpStatus = typeof candidate.status === 'number' ? candidate.status : undefined;
    return { message, httpStatus };
  }
  return { message: 'Request failed.' };
}

/**
 * Run an API-backed app action under the user's identity and normalize the
 * result. Server-side authorization decides success or failure; any error is
 * surfaced verbatim so the guide can report it instead of guessing.
 */
async function runAppAction(action: string, fn: () => Promise<unknown>): Promise<AppToolResult> {
  try {
    const data = await fn();
    return { status: 'ok', action, data };
  } catch (error) {
    const { message, httpStatus } = describeError(error);
    return appError(action, message, httpStatus);
  }
}

function resolveProjectId(args: ToolArguments, context: AppGuideContext): string | undefined {
  return readNonEmptyString(getFieldValue(args, 'projectId')) ?? readNonEmptyString(context.projectId);
}

function resolveNotebookId(args: ToolArguments, context: AppGuideContext): string | undefined {
  return readNonEmptyString(getFieldValue(args, 'notebookId')) ?? readNonEmptyString(context.notebookId);
}

function registerAppActionTools(
  chat: GuideantsChatElement,
  buildAppContext: () => GuideViewContext,
  appActions: GuideAppActions,
): void {
  // --- Context / reads ---------------------------------------------------

  chat.registerTool('AppGetCurrentContext', async (call) =>
    toolResult(call, 'AppGetCurrentContext', { status: 'ok', action: 'getContext', data: buildAppContext() }),
  );

  chat.registerTool('AppListProjects', async (call) => {
    const result = await runAppAction('listProjects', () => api.projects.getUserProjects());
    return toolResult(call, 'AppListProjects', result);
  });

  chat.registerTool('AppListNotebooks', async (call) => {
    const args = parseToolArgumentsObject(call);
    const projectId = resolveProjectId(args, buildAppContext());
    if (!projectId) {
      return toolResult(call, 'AppListNotebooks', appError('listNotebooks', 'projectId is required; none found in context.'));
    }
    const result = await runAppAction('listNotebooks', async () => {
      const details = await api.projects.getProjectDetails(projectId);
      return { projectId, projectTitle: details.title, notebooks: details.notebooks };
    });
    return toolResult(call, 'AppListNotebooks', result);
  });

  chat.registerTool('AppListConversations', async (call) => {
    const args = parseToolArgumentsObject(call);
    const context = buildAppContext();
    const projectId = resolveProjectId(args, context);
    const notebookId = resolveNotebookId(args, context);
    if (!projectId || !notebookId) {
      return toolResult(
        call,
        'AppListConversations',
        appError('listConversations', 'projectId and notebookId are required; none found in context.'),
      );
    }
    const result = await runAppAction('listConversations', async () => ({
      projectId,
      notebookId,
      conversations: await api.projects.notebooks.conversations.getAll(projectId, notebookId),
    }));
    return toolResult(call, 'AppListConversations', result);
  });

  // --- Navigation --------------------------------------------------------

  const navTo = (call: ToolCall, name: string, action: string, path: string): ToolResult => {
    appActions.navigate(path);
    return toolResult(call, name, { status: 'ok', action, navigatedTo: path } satisfies AppToolResult);
  };

  chat.registerTool('AppNavigateHome', async (call) => navTo(call, 'AppNavigateHome', 'navigate', '/'));
  chat.registerTool('AppNavigateProjects', async (call) => navTo(call, 'AppNavigateProjects', 'navigate', '/projects'));
  chat.registerTool('AppNavigateConversations', async (call) => navTo(call, 'AppNavigateConversations', 'navigate', '/conversations'));
  chat.registerTool('AppNavigateUsage', async (call) => navTo(call, 'AppNavigateUsage', 'navigate', '/usage'));
  chat.registerTool('AppNavigateSettings', async (call) => navTo(call, 'AppNavigateSettings', 'navigate', '/settings'));

  chat.registerTool('AppNavigateBack', async (call) => {
    appActions.goBack();
    return toolResult(call, 'AppNavigateBack', { status: 'ok', action: 'navigateBack' } satisfies AppToolResult);
  });

  chat.registerTool('AppNavigateProject', async (call) => {
    const args = parseToolArgumentsObject(call);
    const projectId = resolveProjectId(args, buildAppContext());
    if (!projectId) {
      return toolResult(call, 'AppNavigateProject', appError('navigate', 'projectId is required; none found in context.'));
    }
    return navTo(call, 'AppNavigateProject', 'navigate', `/projects/${encodeURIComponent(projectId)}`);
  });

  chat.registerTool('AppNavigateNotebook', async (call) => {
    const args = parseToolArgumentsObject(call);
    const context = buildAppContext();
    const projectId = resolveProjectId(args, context);
    const notebookId = resolveNotebookId(args, context);
    if (!projectId || !notebookId) {
      return toolResult(call, 'AppNavigateNotebook', appError('navigate', 'projectId and notebookId are required; none found in context.'));
    }
    return navTo(
      call,
      'AppNavigateNotebook',
      'navigate',
      `/projects/${encodeURIComponent(projectId)}/notebooks/${encodeURIComponent(notebookId)}`,
    );
  });

  // --- Content actions (mutations, server-authorized) --------------------

  chat.registerTool('AppCreateProject', async (call) => {
    const args = parseToolArgumentsObject(call);
    const title = readNonEmptyString(getFieldValue(args, 'title'));
    if (!title) {
      return toolResult(call, 'AppCreateProject', appError('createProject', 'title is required.'));
    }
    const description = readNonEmptyString(getFieldValue(args, 'description'));
    const result = await runAppAction('createProject', () => api.projects.create({ title, description }));
    if (result.status === 'ok') {
      dispatchAppEvent('refresh-project');
      const created = result.data as { id?: string } | undefined;
      if (created?.id) {
        const path = `/projects/${encodeURIComponent(created.id)}`;
        appActions.navigate(path);
        result.navigatedTo = path;
      }
    }
    return toolResult(call, 'AppCreateProject', result);
  });

  chat.registerTool('AppRenameProject', async (call) => {
    const args = parseToolArgumentsObject(call);
    const context = buildAppContext();
    const projectId = resolveProjectId(args, context);
    const title = readNonEmptyString(getFieldValue(args, 'title'));
    if (!projectId) {
      return toolResult(call, 'AppRenameProject', appError('renameProject', 'projectId is required; none found in context.'));
    }
    if (!title) {
      return toolResult(call, 'AppRenameProject', appError('renameProject', 'title is required.'));
    }
    const description = readNonEmptyString(getFieldValue(args, 'description'));
    const result = await runAppAction('renameProject', () => api.projects.updateProject(projectId, { title, description }));
    if (result.status === 'ok') {
      dispatchAppEvent('refresh-project');
    }
    return toolResult(call, 'AppRenameProject', result);
  });

  chat.registerTool('AppCreateNotebook', async (call) => {
    const args = parseToolArgumentsObject(call);
    const context = buildAppContext();
    const projectId = resolveProjectId(args, context);
    const title = readNonEmptyString(getFieldValue(args, 'title'));
    if (!projectId) {
      return toolResult(call, 'AppCreateNotebook', appError('createNotebook', 'projectId is required; none found in context.'));
    }
    if (!title) {
      return toolResult(call, 'AppCreateNotebook', appError('createNotebook', 'title is required.'));
    }
    const description = readNonEmptyString(getFieldValue(args, 'description'));
    const guideId = readNonEmptyString(getFieldValue(args, 'guideId'));
    const result = await runAppAction('createNotebook', () =>
      api.projects.createNotebook(projectId, { title, description, guideId }),
    );
    if (result.status === 'ok') {
      dispatchAppEvent('refresh-project');
      const created = result.data as { id?: string } | undefined;
      const navigate = getFieldValue(args, 'navigate');
      if (created?.id && navigate !== false) {
        const path = `/projects/${encodeURIComponent(projectId)}/notebooks/${encodeURIComponent(created.id)}`;
        appActions.navigate(path);
        result.navigatedTo = path;
      }
    }
    return toolResult(call, 'AppCreateNotebook', result);
  });

  chat.registerTool('AppRenameNotebook', async (call) => {
    const args = parseToolArgumentsObject(call);
    const context = buildAppContext();
    const projectId = resolveProjectId(args, context);
    const notebookId = resolveNotebookId(args, context);
    const title = readNonEmptyString(getFieldValue(args, 'title'));
    if (!projectId || !notebookId) {
      return toolResult(call, 'AppRenameNotebook', appError('renameNotebook', 'projectId and notebookId are required; none found in context.'));
    }
    if (!title) {
      return toolResult(call, 'AppRenameNotebook', appError('renameNotebook', 'title is required.'));
    }
    const description = readNonEmptyString(getFieldValue(args, 'description'));
    const result = await runAppAction('renameNotebook', () =>
      api.projects.updateNotebook(projectId, notebookId, { title, description }),
    );
    if (result.status === 'ok') {
      dispatchAppEvent('refresh-project');
      dispatchAppEvent('refresh-notebook-toolbar');
    }
    return toolResult(call, 'AppRenameNotebook', result);
  });

  chat.registerTool('AppCreateConversation', async (call) => {
    const args = parseToolArgumentsObject(call);
    const context = buildAppContext();
    const projectId = resolveProjectId(args, context);
    const notebookId = resolveNotebookId(args, context);
    const title = readNonEmptyString(getFieldValue(args, 'title'));
    if (!projectId || !notebookId) {
      return toolResult(call, 'AppCreateConversation', appError('createConversation', 'projectId and notebookId are required; none found in context.'));
    }
    if (!title) {
      return toolResult(call, 'AppCreateConversation', appError('createConversation', 'title is required.'));
    }
    const result = await runAppAction('createConversation', () =>
      api.projects.notebooks.conversations.create(projectId, notebookId, title),
    );
    if (result.status === 'ok') {
      dispatchAppEvent('refresh-conversations');
    }
    return toolResult(call, 'AppCreateConversation', result);
  });

  chat.registerTool('AppRenameConversation', async (call) => {
    const args = parseToolArgumentsObject(call);
    const context = buildAppContext();
    const projectId = resolveProjectId(args, context);
    const notebookId = resolveNotebookId(args, context);
    const conversationId = readNonEmptyString(getFieldValue(args, 'conversationId'))
      ?? readNonEmptyString(context.activeConversationId);
    const title = readNonEmptyString(getFieldValue(args, 'title'));
    if (!projectId || !notebookId || !conversationId) {
      return toolResult(
        call,
        'AppRenameConversation',
        appError('renameConversation', 'projectId, notebookId and conversationId are required; none found in context.'),
      );
    }
    if (!title) {
      return toolResult(call, 'AppRenameConversation', appError('renameConversation', 'title is required.'));
    }
    const result = await runAppAction('renameConversation', () =>
      api.projects.notebooks.conversations.rename(projectId, notebookId, conversationId, title),
    );
    if (result.status === 'ok') {
      dispatchAppEvent('refresh-conversations');
    }
    return toolResult(call, 'AppRenameConversation', result);
  });
}

function dispatchAppEvent(name: string): void {
  try {
    window.dispatchEvent(new Event(name));
  } catch {
    // Event dispatch is a best-effort UI refresh hint; ignore environments
    // without a DOM (e.g. unit tests without window event support).
  }
}

export function registerGuideAntsAppBridge(
  chat: GuideantsChatElement,
  buildAppContext: () => GuideViewContext,
  isAdminGuide: boolean,
  appActions: GuideAppActions,
): void {
  chat.registerTool('AppEcho', async (call) => {
    const args = parseToolArguments(call);
    return toolResult(call, 'AppEcho', {
      status: 'ok',
      echo: args,
      context: buildAppContext(),
    });
  });

  registerAppActionTools(chat, buildAppContext, appActions);

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
