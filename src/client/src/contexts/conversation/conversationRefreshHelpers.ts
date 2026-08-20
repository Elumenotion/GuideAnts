import type { MessageDto } from '../../types/conversation';
import type { ConversationStreamingPreviewDto } from '../../types/notebook';
import { userService } from '../../services/userService';

export function appendStreamingPreviewMessage(
  messages: MessageDto[],
  preview: ConversationStreamingPreviewDto | null | undefined,
  assistantName?: string | null,
): MessageDto[] {
  if (!preview) {
    return messages;
  }

  const previewId = `streaming-${preview.messageId}`;
  if (messages.some(m => m.id === previewId)) {
    return messages;
  }

  return [
    ...messages,
    {
      id: previewId,
      role: 'assistant',
      content: preview.content,
      created: new Date().toISOString(),
      isEdited: false,
      streaming: true,
      assistantName: assistantName ?? undefined,
      turnIndex: preview.turnIndex,
    } as MessageDto,
  ];
}

export function buildInlineUserProfiles(messages: MessageDto[]): Record<string, { id: string; name?: string; email?: string }> {
  const profileMap: Record<string, { id: string; name?: string; email?: string }> = {};
  for (const m of messages) {
    if (m.userId && (m.userName || m.userEmail)) {
      profileMap[m.userId] = {
        ...(profileMap[m.userId] || {}),
        id: m.userId,
        name: m.userName,
        email: m.userEmail,
      };
    }
  }
  return profileMap;
}

export async function fetchMissingUserProfiles(messages: MessageDto[]): Promise<Record<string, unknown>> {
  const idsSet = new Set<string>();
  const profileMap: Record<string, unknown> = {};

  for (const m of messages) {
    if (m.userId) {
      idsSet.add(m.userId);
    }
    if (m.userId && (m.userName || m.userEmail)) {
      profileMap[m.userId] = {
        ...(profileMap[m.userId] as object || {}),
        id: m.userId,
        name: m.userName,
        email: m.userEmail,
      };
    }
  }

  try {
    const me = await userService.getCurrentUser();
    if (me?.id) {
      idsSet.add(me.id);
      profileMap[me.id] = { ...(profileMap[me.id] as object || {}), ...me };
    }
  } catch {
    // best effort
  }

  const idsToFetch = Array.from(idsSet).filter(id => {
    const cached = profileMap[id] as { name?: string; email?: string } | undefined;
    return !cached || (!cached.name && !cached.email);
  });

  if (idsToFetch.length > 0) {
    const profiles = await Promise.all(
      idsToFetch.map(id => userService.getUserById(id).catch(() => null)),
    );
    idsToFetch.forEach((id, idx) => {
      if (profiles[idx]) {
        profileMap[id] = profiles[idx];
      }
    });
  }

  return profileMap;
}
