import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../../../services/api', () => ({
  api: {
    projects: {
      getUserProjects: vi.fn(),
      getProjectDetails: vi.fn(),
      create: vi.fn(),
      updateProject: vi.fn(),
      createNotebook: vi.fn(),
      updateNotebook: vi.fn(),
      notebooks: {
        conversations: {
          getAll: vi.fn(),
          create: vi.fn(),
          rename: vi.fn(),
        },
      },
    },
  },
}));

import { registerGuideAntsAppBridge } from '../guideantsAppBridge';
import { api } from '../../../services/api';
import type { GuideAppActions, GuideViewContext } from '../types';
import type { GuideantsChatElement, ToolCall } from 'guideants';

function createChatHarness() {
  const handlers = new Map<string, (call: ToolCall) => Promise<unknown>>();
  const chat = {
    registerTool: vi.fn((name: string, handler: (call: ToolCall) => Promise<unknown>) => {
      handlers.set(name, handler);
    }),
  } as unknown as GuideantsChatElement;
  return { handlers, chat };
}

function createAppActions() {
  const navigate = vi.fn();
  const goBack = vi.fn();
  const appActions: GuideAppActions = { navigate, goBack };
  return { appActions, navigate, goBack };
}

function makeCall(name: string, args: unknown, id: string): ToolCall {
  return { id, function: { name, arguments: args } };
}

function parsePayload(result: unknown): Record<string, unknown> {
  return JSON.parse((result as { content: string }).content) as Record<string, unknown>;
}

const notebookContext: GuideViewContext = {
  route: '/projects/p1/notebooks/n1',
  role: 'Contributor',
  userId: 'u1',
  displayName: 'Ada',
  projectId: 'project-1',
  notebookId: 'notebook-1',
  activeConversationId: 'conversation-1',
};

describe('guideantsAppBridge app actions', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('AppGetCurrentContext returns the live view context', async () => {
    const { chat, handlers } = createChatHarness();
    const { appActions } = createAppActions();
    registerGuideAntsAppBridge(chat, () => notebookContext, false, appActions);

    const result = await handlers.get('AppGetCurrentContext')!(makeCall('AppGetCurrentContext', {}, 'c1'));
    const payload = parsePayload(result);
    expect(payload.status).toBe('ok');
    expect(payload.data).toMatchObject({ projectId: 'project-1', notebookId: 'notebook-1' });
  });

  it('AppNavigateProject uses projectId from context', async () => {
    const { chat, handlers } = createChatHarness();
    const { appActions, navigate } = createAppActions();
    registerGuideAntsAppBridge(chat, () => notebookContext, false, appActions);

    const result = await handlers.get('AppNavigateProject')!(makeCall('AppNavigateProject', {}, 'c2'));
    expect(navigate).toHaveBeenCalledWith('/projects/project-1');
    const payload = parsePayload(result);
    expect(payload.status).toBe('ok');
    expect(payload.navigatedTo).toBe('/projects/project-1');
  });

  it('AppNavigateProject errors when no projectId is available', async () => {
    const { chat, handlers } = createChatHarness();
    const { appActions, navigate } = createAppActions();
    const bareContext: GuideViewContext = { route: '/', role: 'Reader', userId: 'u1', displayName: 'Ada' };
    registerGuideAntsAppBridge(chat, () => bareContext, false, appActions);

    const result = await handlers.get('AppNavigateProject')!(makeCall('AppNavigateProject', {}, 'c3'));
    expect(navigate).not.toHaveBeenCalled();
    const payload = parsePayload(result);
    expect(payload.status).toBe('error');
    expect(String(payload.message)).toContain('projectId is required');
  });

  it('AppListProjects returns the API result under ok', async () => {
    vi.mocked(api.projects.getUserProjects).mockResolvedValue([
      { id: 'project-1', title: 'First', description: '', created: '', canCreateContent: true },
    ] as never);

    const { chat, handlers } = createChatHarness();
    const { appActions } = createAppActions();
    registerGuideAntsAppBridge(chat, () => notebookContext, false, appActions);

    const result = await handlers.get('AppListProjects')!(makeCall('AppListProjects', {}, 'c4'));
    const payload = parsePayload(result);
    expect(payload.status).toBe('ok');
    expect(Array.isArray(payload.data)).toBe(true);
  });

  it('AppCreateNotebook creates from context project and navigates to the new notebook', async () => {
    vi.mocked(api.projects.createNotebook).mockResolvedValue({ id: 'notebook-new', title: 'Notes', guideId: 'g' } as never);
    const dispatchSpy = vi.spyOn(window, 'dispatchEvent');

    const { chat, handlers } = createChatHarness();
    const { appActions, navigate } = createAppActions();
    registerGuideAntsAppBridge(chat, () => notebookContext, false, appActions);

    const result = await handlers.get('AppCreateNotebook')!(
      makeCall('AppCreateNotebook', { title: 'Notes' }, 'c5'),
    );

    expect(api.projects.createNotebook).toHaveBeenCalledWith('project-1', {
      title: 'Notes',
      description: undefined,
      guideId: undefined,
    });
    expect(navigate).toHaveBeenCalledWith('/projects/project-1/notebooks/notebook-new');
    expect(dispatchSpy.mock.calls.some(([event]) => (event as Event).type === 'refresh-project')).toBe(true);

    const payload = parsePayload(result);
    expect(payload.status).toBe('ok');
    expect(payload.navigatedTo).toBe('/projects/project-1/notebooks/notebook-new');
  });

  it('AppCreateNotebook requires a title', async () => {
    const { chat, handlers } = createChatHarness();
    const { appActions } = createAppActions();
    registerGuideAntsAppBridge(chat, () => notebookContext, false, appActions);

    const result = await handlers.get('AppCreateNotebook')!(makeCall('AppCreateNotebook', {}, 'c6'));
    expect(api.projects.createNotebook).not.toHaveBeenCalled();
    const payload = parsePayload(result);
    expect(payload.status).toBe('error');
    expect(String(payload.message)).toContain('title is required');
  });

  it('AppRenameNotebook surfaces API errors verbatim', async () => {
    const apiError = Object.assign(new Error('Forbidden'), { status: 403 });
    vi.mocked(api.projects.updateNotebook).mockRejectedValue(apiError);

    const { chat, handlers } = createChatHarness();
    const { appActions } = createAppActions();
    registerGuideAntsAppBridge(chat, () => notebookContext, false, appActions);

    const result = await handlers.get('AppRenameNotebook')!(
      makeCall('AppRenameNotebook', { title: 'Renamed' }, 'c7'),
    );

    expect(api.projects.updateNotebook).toHaveBeenCalledWith('project-1', 'notebook-1', {
      title: 'Renamed',
      description: undefined,
    });
    const payload = parsePayload(result);
    expect(payload.status).toBe('error');
    expect(payload.message).toBe('Forbidden');
    expect(payload.httpStatus).toBe(403);
  });

  it('AppRenameConversation falls back to the active conversation from context', async () => {
    vi.mocked(api.projects.notebooks.conversations.rename).mockResolvedValue(undefined as never);

    const { chat, handlers } = createChatHarness();
    const { appActions } = createAppActions();
    registerGuideAntsAppBridge(chat, () => notebookContext, false, appActions);

    const result = await handlers.get('AppRenameConversation')!(
      makeCall('AppRenameConversation', { title: 'New title' }, 'c8'),
    );

    expect(api.projects.notebooks.conversations.rename).toHaveBeenCalledWith(
      'project-1',
      'notebook-1',
      'conversation-1',
      'New title',
    );
    expect(parsePayload(result).status).toBe('ok');
  });
});

describe('guideantsAppBridge connector parity', () => {
  const here = dirname(fileURLToPath(import.meta.url));
  const repoRoot = resolve(here, '../../../../../..');

  function operationIdsFor(guideFolder: string): string[] {
    const file = resolve(
      repoRoot,
      'src/server/GuideAntsApi/Resources/bootstrap/guides',
      guideFolder,
      'OpenAPI/Web Connector.json',
    );
    const spec = JSON.parse(readFileSync(file, 'utf8')) as {
      paths: Record<string, { post: { operationId: string } }>;
    };
    return Object.values(spec.paths).map((entry) => entry.post.operationId);
  }

  function registeredToolNames(isAdminGuide: boolean): Set<string> {
    const { chat, handlers } = createChatHarness();
    const { appActions } = createAppActions();
    registerGuideAntsAppBridge(chat, () => notebookContext, isAdminGuide, appActions);
    return new Set(handlers.keys());
  }

  it('every user-guide connector operationId has a registered handler', () => {
    const registered = registeredToolNames(false);
    for (const operationId of operationIdsFor('guideants-guide')) {
      expect(registered.has(operationId)).toBe(true);
    }
  });

  it('every admin-guide connector operationId has a registered handler', () => {
    const registered = registeredToolNames(true);
    for (const operationId of operationIdsFor('guideants-guide-admin')) {
      expect(registered.has(operationId)).toBe(true);
    }
  });
});
