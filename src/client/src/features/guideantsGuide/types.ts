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
