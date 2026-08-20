import { describe, expect, it } from 'vitest';

import { appendStreamingPreviewMessage } from '../conversationRefreshHelpers';

describe('appendStreamingPreviewMessage', () => {
  it('appends a streaming placeholder when preview is present', () => {
    const result = appendStreamingPreviewMessage(
      [{ id: 'u1', role: 'user', content: 'hi' } as any],
      { messageId: 'msg-1', content: 'partial', turnIndex: 2 },
      'Creative Guide',
    );

    expect(result).toHaveLength(2);
    expect(result[1]).toMatchObject({
      id: 'streaming-msg-1',
      role: 'assistant',
      content: 'partial',
      streaming: true,
      turnIndex: 2,
      assistantName: 'Creative Guide',
    });
  });

  it('does not duplicate an existing preview placeholder', () => {
    const messages = [
      { id: 'streaming-msg-1', role: 'assistant', content: 'partial' } as any,
    ];

    const result = appendStreamingPreviewMessage(
      messages,
      { messageId: 'msg-1', content: 'partial', turnIndex: 2 },
    );

    expect(result).toBe(messages);
  });
});
