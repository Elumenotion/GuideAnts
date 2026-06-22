import { describe, it, expect, vi } from 'vitest';
import { registerGuideAntsAppBridge } from '../guideantsAppBridge';
import type { GuideantsChatElement, ToolCall } from 'guideants';

describe('guideantsAppBridge', () => {
  it('registers AppEcho and returns echo payload with context', async () => {
    const handlers = new Map<string, (call: ToolCall) => Promise<unknown>>();
    const chat = {
      registerTool: vi.fn((name: string, handler: (call: ToolCall) => Promise<unknown>) => {
        handlers.set(name, handler);
      }),
    } as unknown as GuideantsChatElement;

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
    const result = await handler!({
      id: 'call-1',
      function: { name: 'AppEcho', arguments: { message: 'hello' } },
    });

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
});
