export interface SystemGuideSessionDto {
  publishedGuideId: string;
  projectId: string;
  notebookId: string;
  guideId: string;
  guideName: string;
  clientBridgeId: string;
  isAdminGuide: boolean;
  commandMode: boolean;
}

export interface SystemGuideWorkspaceDto {
  projectId: string;
  projectSlug: string;
}
