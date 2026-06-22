// Mirror backend DTOs for Guides & Assistants

export interface GuideDto {
  id: string;
  name: string;
  description: string;
  avatarUrl?: string;
  modelId?: string;
  modelName?: string;
  toolCount: number;
  crewMemberCount: number;
  created: string;
  updated?: string;
}

export interface GuideDetailsDto {
  guide: GuideDto;
  instructions?: string;
  homePageMarkdown?: string;
  temperature?: number | null;
  topP?: number | null;
  reasoningEffort?: string;
  tools: ToolAssignmentDto[];
  contextOptions: ContextOptionDto[];
  authProviders?: AuthProviderDto[];
  environmentVariables?: EnvironmentVariableDto[];
  customTools: CustomToolDto[];
  files: FileDto[];
  conversationStarters: ConversationStarterDto[];
  crews: CrewSummaryDto[];
}

export interface CreateGuideDto {
  projectId?: string;
  name: string;
  description: string;
  instructions?: string;
  homePageMarkdown?: string;
  modelId?: string;
  temperature?: number | null;
  topP?: number | null;
  reasoningEffort?: string;
  samplingParametersJson?: string;
  avatarImageBytes?: string; // base64 encoded, empty string to clear, undefined to leave unchanged
  avatarContentType?: string;
  toolIds?: string[];
  customTools?: CustomToolDto[];
  contextOptions?: ContextOptionDto[];
  authProviders?: AuthProviderDto[];
  environmentVariables?: EnvironmentVariableDto[];
  files?: FileUploadDto[];
  conversationStarters?: string[];
  crewMemberIds?: string[];
}

export interface UpdateGuideDto {
  projectId?: string;
  name: string;
  description: string;
  instructions?: string;
  homePageMarkdown?: string;
  modelId?: string;
  temperature?: number | null;
  topP?: number | null;
  reasoningEffort?: string;
  samplingParametersJson?: string;
  avatarImageBytes?: string; // base64 encoded, empty string to clear, undefined to leave unchanged
  avatarContentType?: string;
  toolIds?: string[];
  customTools?: CustomToolDto[];
  contextOptions?: ContextOptionDto[];
  authProviders?: AuthProviderDto[];
  environmentVariables?: EnvironmentVariableDto[];
  fileIdsToKeep?: string[]; // IDs of existing files to keep (omitted files are deleted)
  filesToAdd?: FileUploadDto[]; // New files to upload
  conversationStarters?: string[];
  crewMemberIds?: string[];
}

export interface AssistantDto {
  id: string;
  name: string;
  description: string;
  avatarUrl?: string;
  modelId?: string;
  modelName?: string;
  toolCount: number;
  crewNames: string[];
  created: string;
  updated?: string;
}

export interface AssistantDetailsDto {
  assistant: AssistantDto;
  instructions?: string;
  temperature?: number | null;
  topP?: number | null;
  reasoningEffort?: string;
  tools: ToolAssignmentDto[];
  contextOptions: ContextOptionDto[];
  environmentVariables?: EnvironmentVariableDto[];
  customTools: CustomToolDto[];
  files: FileDto[];
  conversationStarters: ConversationStarterDto[];
}

export interface CreateAssistantDto {
  projectId?: string;
  name: string;
  description: string;
  instructions?: string;
  modelId?: string;
  temperature?: number | null;
  topP?: number | null;
  reasoningEffort?: string;
  samplingParametersJson?: string;
  avatarImageBytes?: string; // base64 encoded, empty string to clear, undefined to leave unchanged
  avatarContentType?: string;
  toolIds?: string[];
  customTools?: CustomToolDto[];
  contextOptions?: ContextOptionDto[];
  authProviders?: AuthProviderDto[];
  environmentVariables?: EnvironmentVariableDto[];
  files?: FileUploadDto[];
  conversationStarters?: string[];
}

export interface UpdateAssistantDto {
  projectId?: string;
  name: string;
  description: string;
  instructions?: string;
  modelId?: string;
  temperature?: number | null;
  topP?: number | null;
  reasoningEffort?: string;
  samplingParametersJson?: string;
  avatarImageBytes?: string; // base64 encoded, empty string to clear, undefined to leave unchanged
  avatarContentType?: string;
  toolIds?: string[];
  customTools?: CustomToolDto[];
  contextOptions?: ContextOptionDto[];
  authProviders?: AuthProviderDto[];
  environmentVariables?: EnvironmentVariableDto[];
  fileIdsToKeep?: string[]; // IDs of existing files to keep (omitted files are deleted)
  filesToAdd?: FileUploadDto[]; // New files to upload
  conversationStarters?: string[];
}

// Supporting types
export interface ToolAssignmentDto {
  toolId: string;
  toolType: string;
  displayName: string;
  isCustom: boolean;
}

export interface CustomToolDto {
  name: string;
  openApiSpec: string;
  apiHost?: string; // Extracted from OpenAPI spec servers, must be unique
  authConfig?: OpenApiAuthConfig; // Optional 1:1 auth configuration
  operations?: OpenApiOperation[]; // Individual operations parsed from the schema
}

export interface OpenApiOperation {
  id: string;
  operationId: string;
  method: string;
  path: string;
  summary?: string;
  schemaFragment?: string;
  toolDefinition?: string;
}

export interface UpdateOperationDto {
  schemaFragmentJson: string;
}

export interface PreviewToolDefinitionDto {
  schemaFragmentJson: string;
  openApiSpecJson: string;
}

export type ToolSourceValidationSeverity = 'error' | 'warning';

export interface ToolSourceValidationMessage {
  code: string;
  message: string;
  field?: string;
  severity: ToolSourceValidationSeverity;
}

export interface ToolDefinitionPreviewResult {
  sourceKind: string;
  actionType: string;
  toolDefinition: string;
  hiddenParameters: string[];
  responseSchemas?: Record<string, unknown>;
  validationMessages: ToolSourceValidationMessage[];
}

export type {
  McpTransport,
  McpConnectionPanelState,
  McpToolDiffState,
  McpToolSourceMetadata,
  McpConnectionSettings,
  McpDiscoveredToolRow,
  McpDiscoverDiffSummary,
  McpDiscoverToolsResponse,
  McpTestConnectionResponse,
} from '../components/guides/editor/toolSources/mcpToolSourceTypes';

export interface McpToolSourceConnectionDto {
  transport: import('../components/guides/editor/toolSources/mcpToolSourceTypes').McpTransport;
  url?: string;
  bridgeId?: string;
  headers?: Record<string, string>;
  toolNamePrefix?: string;
}

export interface McpExistingToolStateDto {
  backingToolId: string;
  schemaHash?: string;
  enabled: boolean;
  operationId?: string;
}

export interface McpTestConnectionRequest {
  connection: McpToolSourceConnectionDto;
}

export interface McpDiscoverToolsRequest {
  connection: McpToolSourceConnectionDto;
  existingTools?: McpExistingToolStateDto[];
  bridgeTools?: Array<{
    name: string;
    title?: string;
    description?: string;
    inputSchema?: unknown;
  }>;
}

export interface OpenApiAuthConfig {
  authType: 'oauth' | 'service_http' | 'service_query';
  // OAuth fields
  clientId?: string;
  tenant?: string;
  scopes?: string[];
  valueTemplate?: string;
  // Service HTTP fields (for API keys, use service_http with appropriate header name)
  headerName?: string;
  // User configuration policy
  userConfigPolicy?: 'required' | 'optional' | 'none';
}

export interface ContextOptionDto {
  key: string;
  value?: string;
}

export interface AuthProviderDto {
  id?: string;
  providerId: string;
  authType: string;
  clientId?: string;
  tenant?: string;
  headerName?: string;
  valueTemplate?: string;
  userConfigPolicy?: string;
  scopes?: string[];
}

export interface EnvironmentVariableDto {
  name: string;
  value?: string | null;
  isSecret: boolean;
}

export enum MarkdownExtractionStatus {
  Pending = 'Pending',
  Processing = 'Processing',
  Completed = 'Completed',
  Failed = 'Failed',
  Skipped = 'Skipped'
}

export interface AssistantFileMarkdownShadowDto {
  id: string;
  originalAssistantFileId: string;
  contentHash: string;
  fileSize: number;
  status: MarkdownExtractionStatus;
  errorMessage?: string;
  created: string;
  processedAt?: string;
}

export interface FileDto {
  id: string;
  folderKind: string;
  vectorStoreName?: string;
  relativePath: string;
  contentType?: string;
  created: string;
  markdownShadow?: AssistantFileMarkdownShadowDto;
}

export interface FileUploadDto {
  folderKind: string;
  vectorStoreName?: string;
  relativePath: string;
  contentBytes: string; // Base64-encoded file content
  contentType?: string;
}

export interface ConversationStarterDto {
  id?: string;
  prompt: string;
  orderIndex: number;
}

export interface CrewSummaryDto {
  crewId: string;
  name: string;
  members: CrewMemberDto[];
}

export interface CrewMemberDto {
  assistantId: string;
  name: string;
  avatarUrl?: string;
  isGlobal: boolean;
  displayOrder: number;
}

// Catalog types
export interface LocalRuntimeDescriptorDto {
  routerModelId: string;
  runtimeProfileId: string;
  loadParams?: Record<string, unknown>;
}

export interface SamplingParameterPolicyDto {
  key: string;
  label: string;
  description: string;
  min: number;
  max: number;
  step: number;
  recommendedDefault: number;
  displayOrder: number;
}

export interface ModelDto {
  modelId: string;
  displayName: string;
  description?: string;
  reasoningChoicesJson?: string;
  isActive: boolean;
  displayOrder?: number;
  localRuntime?: LocalRuntimeDescriptorDto;
  samplingParameterPolicy?: SamplingParameterPolicyDto[];
  reasoningChoices?: string[];
  defaultReasoningChoice?: string;
}

export interface ToolDto {
  id: string;
  toolType: string;
  displayName: string;
  description?: string;
  category?: string;
  isActive: boolean;
  displayOrder?: number;
}

export interface GlobalAssistantDto {
  id: string;
  name: string;
  description: string;
  defaultInstructions?: string;
  defaultModelId?: string; // Now string (ModelId) instead of Guid
  avatarUrl?: string;
  isActive: boolean;
  displayOrder?: number;
  defaultToolIds: string[];
}

export interface GlobalAssistantDetailsDto {
  assistant: GlobalAssistantDto;
  tools: ToolDto[];
}

// Export/Import types
export interface ImportPreviewDto {
  guideName: string;
  crewCount: number;
  customAssistantCount: number;
  nameConflicts: string[];
}

export interface ImportGuideResultDto {
  success: boolean;
  guideId?: string;
  customAssistantsCreated: number;
  crewsCreated: number;
  globalAssistantsLinked: number;
  itemsSkipped: number;
  warnings: string[];
}

// Published Guide types
export type PublishedGuideAuthMode = 'Anonymous' | 'Webhook' | 'ApiKey' | 'AppIdentity';

export interface PublishedGuideDto {
  id: string;
  guideId: string;
  guideName: string;
  notebookId: string;
  projectId: string;
  created: string;
  active: boolean;
  retentionPeriod?: number;
  maxUserMessageLength?: number;
  maxTurns?: number;
  dailyChargeLimitUsd?: number;
  billingPeriodChargeLimitUsd?: number;
  authValidationWebhookUrl?: string;
  authWebhookTimeoutSeconds?: number;
  friendlyName?: string;
  displayMode?: 'full' | 'last-turn';
  commandMode?: boolean;
  showTurnNavigation?: boolean;
  collapsible?: boolean;
  showConversationStarters?: boolean;
  showAttachments?: boolean;
  /** Explicit auth mode for this published guide */
  authMode?: PublishedGuideAuthMode;
  wireApiConfig?: PublishedWireApiConfigDto;
  /** Indicates whether an API key is configured (the key itself is never returned) */
  hasApiKey?: boolean;
  /** Whether MCP (Model Context Protocol) access is enabled */
  mcpEnabled?: boolean;
  /** Client-facing description for MCP discovery */
  mcpDescription?: string | null;
}

/** Response returned when generating or regenerating an API key */
export interface ApiKeyGenerationResultDto {
  /** The plaintext API key - store securely, shown only once */
  apiKey: string;
  /** Warning about the key being shown only once */
  warning: string;
}

export interface PublishGuideDto {
  projectId: string;
  retentionPeriod?: number;
  maxUserMessageLength?: number;
  maxTurns?: number;
  dailyChargeLimitUsd?: number;
  billingPeriodChargeLimitUsd?: number;
  authValidationWebhookUrl?: string;
  authWebhookTimeoutSeconds?: number;
  friendlyName?: string;
  displayMode?: 'full' | 'last-turn';
  commandMode?: boolean;
  showTurnNavigation?: boolean;
  collapsible?: boolean;
  showConversationStarters?: boolean;
  showAttachments?: boolean;
  wireApiConfig?: PublishedWireApiConfigDto;
  mcpEnabled?: boolean;
  mcpDescription?: string;
}

export interface UpdatePublishedGuideDto {
  retentionPeriod?: number;
  maxUserMessageLength?: number;
  maxTurns?: number;
  dailyChargeLimitUsd?: number;
  billingPeriodChargeLimitUsd?: number;
  authValidationWebhookUrl?: string;
  authWebhookTimeoutSeconds?: number;
  friendlyName?: string;
  displayMode?: 'full' | 'last-turn';
  commandMode?: boolean;
  showTurnNavigation?: boolean;
  collapsible?: boolean;
  showConversationStarters?: boolean;
  showAttachments?: boolean;
  wireApiConfig?: PublishedWireApiConfigDto;
  mcpEnabled?: boolean;
  mcpDescription?: string;
}

export interface PublishedWireApiConfigDto {
  enabled?: boolean;
  profile?: string;
  endpointFlags?: PublishedWireApiEndpointFlagsDto;
  aliasMap?: Record<string, string>;
  maxRequestSizes?: PublishedWireApiMaxRequestSizesDto;
}

export interface PublishedWireApiEndpointFlagsDto {
  models?: boolean;
  chatCompletions?: boolean;
  responses?: boolean;
  embeddings?: boolean;
  imageGenerations?: boolean;
  audioTranscriptions?: boolean;
  audioSpeech?: boolean;
}

export interface PublishedWireApiMaxRequestSizesDto {
  chatCompletionsBytes?: number;
  responsesBytes?: number;
  embeddingsBytes?: number;
  imageGenerationsBytes?: number;
  audioTranscriptionsBytes?: number;
  audioSpeechBytes?: number;
}
