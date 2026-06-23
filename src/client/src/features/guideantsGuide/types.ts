import type { AppRole } from '../../types/user';

export interface AppGuideContext {
  route: string;
  role: AppRole;
  userId: string;
  displayName: string;
  projectId?: string;
  notebookId?: string;
  guideId?: string;
}

/**
 * A lightweight description of an item the user currently has selected or
 * focused in the UI (a notebook, content file, folder, conversation, etc.).
 */
export interface GuideViewSelectedItem {
  type: string;
  id: string;
  name?: string;
}

/**
 * The full context snapshot injected into the guide chat widget every turn.
 * Extends {@link AppGuideContext} (route + identity + route-derived ids) with
 * human-readable, page-published UI state so the guide can resolve phrases like
 * "this notebook" / "this project" without asking the user.
 *
 * Everything beyond {@link AppGuideContext} is descriptive only. It never
 * carries permission flags: authorization is always enforced server-side under
 * the signed-in user's identity when a tool calls the API.
 */
export interface GuideViewContext extends AppGuideContext {
  /** Coarse screen identifier, e.g. 'home' | 'projects' | 'project' | 'notebook'. */
  screen?: string;
  projectTitle?: string;
  notebookTitle?: string;
  guideName?: string;
  selectedItem?: GuideViewSelectedItem;
  activeConversationId?: string;
  activeConversationTitle?: string;
  /** Settings sub-section name when on the settings screen. */
  settingsTab?: string;
  /** Lightweight counts to ground the guide, e.g. { notebooks: 3, files: 12 }. */
  itemCounts?: Record<string, number>;
}

/**
 * The slice a page publishes into the view-context registry. `route` is
 * required so the provider only merges a slice while its publishing page is the
 * active route (prevents stale context leaking across navigations).
 */
export type GuideViewContextPatch = Partial<Omit<GuideViewContext, 'role' | 'userId' | 'displayName'>> & {
  route: string;
};

/**
 * Imperative app actions the guide bridge can invoke on behalf of the user.
 * Navigation runs entirely client-side (React Router); it changes the URL only
 * and never bypasses route guards.
 */
export interface GuideAppActions {
  navigate: (path: string) => void;
  goBack: () => void;
}
