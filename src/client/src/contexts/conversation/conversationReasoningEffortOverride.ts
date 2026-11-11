/**
 * Per-conversation thinking-level override (client-local, session-scoped).
 *
 * The DraftUserCell composer (inside ConversationProvider) lets the user pick
 * a thinking level via Ctrl+`. That pick must reach
 * useConversationActions.sendMessage (also inside the provider) so it can be
 * sent as `reasoningEffort` on the next SendMessageRequest.
 *
 * The override is NOT persisted. It dies on hard refresh. It is also cleared
 * when the user navigates away from the conversation: the keyed
 * ConversationProvider owns that cleanup (it deletes the registry entry on
 * unmount). The composer itself must NOT clear on unmount, because the
 * composer unmounts on every turn while streaming (CellList only renders it
 * at-rest) - that per-turn unmount used to wipe the override (defect).
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

/**
 * Delete the entry for a conversation outright. Owned by the keyed
 * ConversationProvider's unmount cleanup - the only place where "unmount"
 * unambiguously means "the conversation view went away" rather than "the turn
 * ended". A deleted entry reads back as null, so behaviorally it is the same
 * as set(null); the delete keeps the map from accumulating stale entries.
 */
export const clearConversationReasoningEffortOverride = (
  conversationId: string | null | undefined,
): void => {
  if (!conversationId) return;
  overridesByConversation.delete(conversationId);
};
