import { afterEach, describe, expect, it, vi } from 'vitest';
import { registerGuideAntsAppBridge } from '../guideantsAppBridge';
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

function makeCall(name: string, args: unknown, id: string): ToolCall {
  return {
    id,
    function: { name, arguments: args },
  };
}

function parseToolPayload(result: unknown): Record<string, unknown> {
  const typed = result as { content: string };
  return JSON.parse(typed.content) as Record<string, unknown>;
}

describe('guideantsAppBridge', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('registers AppEcho and returns echo payload with context', async () => {
    const { chat, handlers } = createChatHarness();

    const buildAppContext = () => ({
      route: '/home',
      role: 'Contributor' as const,
      userId: 'u1',
      displayName: 'Ada',
    });

    registerGuideAntsAppBridge(chat, buildAppContext, false);
    expect(chat.registerTool).toHaveBeenCalledWith('AppEcho', expect.any(Function));

    const handler = handlers.get('AppEcho');
    expect(handler).toBeDefined();
    const result = await handler!(makeCall('AppEcho', { message: 'hello' }, 'call-1'));

    expect(result).toEqual({
      toolCallId: 'call-1',
      name: 'AppEcho',
      content: JSON.stringify({
        status: 'ok',
        echo: { message: 'hello' },
        context: buildAppContext(),
      }),
    });
  });

  it('registers sandbox admin tools only for admin guide sessions', () => {
    const buildAppContext = () => ({
      route: '/home',
      role: 'Admin' as const,
      userId: 'u-admin',
      displayName: 'Admin',
    });

    const nonAdminHarness = createChatHarness();
    registerGuideAntsAppBridge(nonAdminHarness.chat, buildAppContext, false);
    expect(Array.from(nonAdminHarness.handlers.keys())).toEqual(['AppEcho']);

    const adminHarness = createChatHarness();
    registerGuideAntsAppBridge(adminHarness.chat, buildAppContext, true);
    expect(adminHarness.handlers.has('AppEcho')).toBe(true);
    expect(adminHarness.handlers.has('SandboxAdminSetToken')).toBe(false);
    expect(adminHarness.handlers.has('SandboxAdminClearToken')).toBe(false);
    expect(adminHarness.handlers.has('SandboxAdminGetConfig')).toBe(false);
    expect(adminHarness.handlers.has('SandboxAdminSetConfig')).toBe(false);
    expect(adminHarness.handlers.has('SandboxAdminGetRequirements')).toBe(true);
    expect(adminHarness.handlers.has('SandboxAdminApply')).toBe(true);
    expect(adminHarness.handlers.has('SandboxAdminGetApplyJob')).toBe(true);
    expect(adminHarness.handlers.has('SandboxAdminGetSetupStatus')).toBe(true);
    expect(adminHarness.handlers.has('SandboxAdminGetInstallScripts')).toBe(true);
    expect(adminHarness.handlers.has('SandboxAdminSetInstallScripts')).toBe(true);
  });

  it('validates scoped sandbox requests require projectId with either guideId or notebookId', async () => {
    const fetchMock = vi.spyOn(window, 'fetch').mockResolvedValue(
      new Response('{}', {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );

    const { chat, handlers } = createChatHarness();
    registerGuideAntsAppBridge(
      chat,
      () => ({
        route: '/settings/system-guides',
        role: 'Admin',
        userId: 'u-admin',
        displayName: 'Admin',
      }),
      true,
    );

    const result = await handlers.get('SandboxAdminSetRequirements')!(
      makeCall(
        'SandboxAdminSetRequirements',
        { projectId: '11111111-1111-1111-1111-111111111111', content: 'requests==2.32.3' },
        'call-scoped-error',
      ),
    );

    expect(fetchMock).not.toHaveBeenCalled();
    const payload = parseToolPayload(result);
    expect(payload.status).toBe('error');
    expect(payload.message).toBe('Provide projectId with either guideId or notebookId for scoped sandbox operations.');
  });

  it('requires notebook or guide-builder context for python requirement tools', async () => {
    const fetchMock = vi.spyOn(window, 'fetch').mockResolvedValue(
      new Response('{}', {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );

    const { chat, handlers } = createChatHarness();
    registerGuideAntsAppBridge(
      chat,
      () => ({
        route: '/settings/system-guides',
        role: 'Admin',
        userId: 'u-admin',
        displayName: 'Admin',
      }),
      true,
    );

    const result = await handlers.get('SandboxAdminGetRequirements')!(
      makeCall('SandboxAdminGetRequirements', {}, 'call-missing-context'),
    );

    expect(fetchMock).not.toHaveBeenCalled();
    const payload = parseToolPayload(result);
    expect(payload.status).toBe('error');
    expect(payload.message).toBe('Python sandbox operations must be done from either a notebook or guide builder context.');
  });

  it('accepts requestBody string for apt-packages writes', async () => {
    const fetchMock = vi.spyOn(window, 'fetch').mockResolvedValue(
      new Response(null, {
        status: 204,
      }),
    );

    const { chat, handlers } = createChatHarness();
    registerGuideAntsAppBridge(
      chat,
      () => ({
        route: '/settings/system-guides',
        role: 'Admin',
        userId: 'u-admin',
        displayName: 'Admin',
      }),
      true,
    );

    const result = await handlers.get('SandboxAdminSetAptPackages')!(
      makeCall(
        'SandboxAdminSetAptPackages',
        { requestBody: '# Admin-managed apt packages.\njq' },
        'call-set-apt-request-body',
      ),
    );

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [requestUrl, requestInit] = fetchMock.mock.calls[0] as [RequestInfo | URL, RequestInit | undefined];
    const requestUrlValue = new URL(String(requestUrl));
    expect(requestUrlValue.pathname).toBe('/api/system-guide/sandbox-admin/apt-packages');
    expect(requestInit?.method).toBe('PUT');
    expect(requestInit?.credentials).toBe('include');
    expect(requestInit?.body).toBe('# Admin-managed apt packages.\njq');
    const headers = new Headers(requestInit?.headers);
    expect(headers.get('Content-Type')).toBe('text/plain');

    const payload = parseToolPayload(result);
    expect(payload.status).toBe('ok');
    expect(payload.endpoint).toBe('/api/system-guide/sandbox-admin/apt-packages');
    expect(payload.httpStatus).toBe(204);
  });

  it('accepts raw string tool args for text writes', async () => {
    const fetchMock = vi.spyOn(window, 'fetch').mockResolvedValue(
      new Response(null, {
        status: 204,
      }),
    );

    const { chat, handlers } = createChatHarness();
    registerGuideAntsAppBridge(
      chat,
      () => ({
        route: '/projects/p1/notebooks/n1',
        role: 'Admin',
        userId: 'u-admin',
        displayName: 'Admin',
        projectId: '11111111-1111-1111-1111-111111111111',
        notebookId: '22222222-2222-2222-2222-222222222222',
      }),
      true,
    );

    const result = await handlers.get('SandboxAdminSetRequirements')!(
      makeCall('SandboxAdminSetRequirements', 'requests==2.32.3', 'call-set-requirements-raw-string'),
    );

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [requestUrl, requestInit] = fetchMock.mock.calls[0] as [RequestInfo | URL, RequestInit | undefined];
    const requestUrlValue = new URL(String(requestUrl));
    expect(requestUrlValue.pathname).toBe('/api/system-guide/sandbox-admin/requirements');
    expect(requestUrlValue.searchParams.get('projectId')).toBe('11111111-1111-1111-1111-111111111111');
    expect(requestUrlValue.searchParams.get('notebookId')).toBe('22222222-2222-2222-2222-222222222222');
    expect(requestInit?.method).toBe('PUT');
    expect(requestInit?.body).toBe('requests==2.32.3');
    const headers = new Headers(requestInit?.headers);
    expect(headers.get('Content-Type')).toBe('text/plain');

    const payload = parseToolPayload(result);
    expect(payload.status).toBe('ok');
    expect(payload.endpoint).toBe('/api/system-guide/sandbox-admin/requirements?projectId=11111111-1111-1111-1111-111111111111&notebookId=22222222-2222-2222-2222-222222222222');
    expect(payload.httpStatus).toBe(204);
  });

  it('auto-scopes python requirement reads from guide-builder context', async () => {
    const fetchMock = vi.spyOn(window, 'fetch').mockResolvedValue(
      new Response('requests==2.32.3\n', {
        status: 200,
        headers: { 'Content-Type': 'text/plain' },
      }),
    );

    const { chat, handlers } = createChatHarness();
    registerGuideAntsAppBridge(
      chat,
      () => ({
        route: '/projects/p1/guides/guide/g1',
        role: 'Admin',
        userId: 'u-admin',
        displayName: 'Admin',
        projectId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
        guideId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
      }),
      true,
    );

    const result = await handlers.get('SandboxAdminGetRequirements')!(
      makeCall('SandboxAdminGetRequirements', {}, 'call-read-requirements-context'),
    );

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [requestUrl] = fetchMock.mock.calls[0] as [RequestInfo | URL, RequestInit | undefined];
    const requestUrlValue = new URL(String(requestUrl));
    expect(requestUrlValue.pathname).toBe('/api/system-guide/sandbox-admin/requirements');
    expect(requestUrlValue.searchParams.get('projectId')).toBe('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
    expect(requestUrlValue.searchParams.get('guideId')).toBe('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb');

    const payload = parseToolPayload(result);
    expect(payload.status).toBe('ok');
    expect(payload.endpoint).toBe('/api/system-guide/sandbox-admin/requirements?projectId=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa&guideId=bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb');
  });

  it('accepts scoped fields from requestBody object', async () => {
    const fetchMock = vi.spyOn(window, 'fetch').mockResolvedValue(
      new Response(null, {
        status: 204,
      }),
    );

    const { chat, handlers } = createChatHarness();
    registerGuideAntsAppBridge(
      chat,
      () => ({
        route: '/settings/system-guides',
        role: 'Admin',
        userId: 'u-admin',
        displayName: 'Admin',
      }),
      true,
    );

    const result = await handlers.get('SandboxAdminSetRequirements')!(
      makeCall(
        'SandboxAdminSetRequirements',
        {
          requestBody: {
            projectId: '11111111-1111-1111-1111-111111111111',
            guideId: '22222222-2222-2222-2222-222222222222',
            content: 'requests==2.32.3',
          },
        },
        'call-set-requirements-request-body',
      ),
    );

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [requestUrl, requestInit] = fetchMock.mock.calls[0] as [RequestInfo | URL, RequestInit | undefined];
    const requestUrlValue = new URL(String(requestUrl));
    expect(requestUrlValue.pathname).toBe('/api/system-guide/sandbox-admin/requirements');
    expect(requestUrlValue.searchParams.get('projectId')).toBe('11111111-1111-1111-1111-111111111111');
    expect(requestUrlValue.searchParams.get('guideId')).toBe('22222222-2222-2222-2222-222222222222');
    expect(requestInit?.method).toBe('PUT');
    expect(requestInit?.body).toBe('requests==2.32.3');

    const payload = parseToolPayload(result);
    expect(payload.status).toBe('ok');
    expect(payload.endpoint).toBe('/api/system-guide/sandbox-admin/requirements?projectId=11111111-1111-1111-1111-111111111111&guideId=22222222-2222-2222-2222-222222222222');
  });
});
