/**
 * Per-conversation thinking-level override (client-local, session-scoped).
 *
 * The DraftUserCell composer (inside ConversationProvider) lets the user pick
 * a thinking level via Ctrl+`. That pick must reach
 * useConversationActions.sendMessage (also inside the provider) so it can be
 * sent as `reasoningEffort` on the next SendMessageRequest.
 *
 * The override is NOT persisted. It dies on hard refresh. It is also cleared
 * when the user navigates away from the conversation (the keyed
 * ConversationProvider unmounts the composer, whose cleanup calls set(null)).
 *
 * This is a deliberate divergence from conversationModelOverride, which
 * intentionally persists across in-tab navigation. The thinking level is a
 * per-viewing-session choice, not a per-conversation setting.
 */
const overridesByConversation = new Map<string, string | null>();

export const getConversationReasoningEffortOverride = (
  conversationId: string | null | undefined,
): string | null => {
  if (!conversationId) return null;
  return overridesByConversation.get(conversationId) ?? null;
};

export const setConversationReasoningEffortOverride = (
  conversationId: string | null | undefined,
  value: string | null,
): void => {
  if (!conversationId) return;
  overridesByConversation.set(conversationId, value ?? null);
};
