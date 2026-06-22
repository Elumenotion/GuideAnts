import type { GuideantsChatElement, ToolCall, ToolResult } from 'guideants';
import type { AppGuideContext } from './types';

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

export function registerGuideAntsAppBridge(
  chat: GuideantsChatElement,
  buildAppContext: () => AppGuideContext,
  _isAdminGuide: boolean,
): void {
  chat.registerTool('AppEcho', async (call) => {
    const args = parseToolArguments(call);
    return toolResult(call, 'AppEcho', {
      status: 'ok',
      echo: args,
      context: buildAppContext(),
    });
  });
}
