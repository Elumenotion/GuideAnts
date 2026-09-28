/**
 * Per-conversation chat model override (client-local, session-scoped).
 *
 * The notebook header toolbar (which lives OUTSIDE the `ConversationProvider`)
 * lets the user pick a model for the current conversation. That pick must reach
 * `useConversationActions.sendMessage` (which lives INSIDE the provider) so it
 * can be sent as `modelDeploymentId` on the next `SendMessageRequest`.
 *
 * The toolbar and the provider are siblings under `NotebookLayout`, so they
 * cannot share React state directly. They coordinate through this small
 * module-level registry (the same idiom as `getNotebookRuntimeReadyCache` in
 * `runtimeChecks.ts`). Each conversation has its own entry, so a per-tab /
 * per-conversation choice is independent; browser tabs are separate JS contexts
 * and never share this map.
 *
 * The override is NOT persisted server-side. Every turn already records the model
 * it used in `ConversationTurn.ModelDeploymentId`; the override only decides what
 * the *next* message requests.
 */
const overridesByConversation = new Map<string, string | null>();

/** Read the current override for a conversation. `null` = no override (normal cascade). */
export const getConversationModelOverride = (
  conversationId: string | null | undefined,
): string | null => {
  if (!conversationId) return null;
  return overridesByConversation.get(conversationId) ?? null;
};

/**
 * Set the override for a conversation. Pass `null` to clear it so subsequent
 * messages fall back to the normal model-resolution cascade.
 */
export const setConversationModelOverride = (
  conversationId: string | null | undefined,
  modelId: string | null,
): void => {
  if (!conversationId) return;
  overridesByConversation.set(conversationId, modelId ?? null);
};
