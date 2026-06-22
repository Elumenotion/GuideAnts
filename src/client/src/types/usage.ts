export type UsageBucket = 'day' | 'week' | 'month';

export type UsageCategory =
  | 'ChatCompletion'
  | 'ImageGeneration'
  | 'DocumentExtraction'
  | 'SpeechTranscription'
  | 'SpeechSynthesis'
  | 'Search'
  | 'StorageUploaded'
  | 'StorageSystemGenerated'
  | 'ToolCall';

export interface UsageTimePointDto {
  periodStart: string; // ISO UTC start of bucket
  totalEvents: number;
  totalCostUsd: number;
}

export interface UsageCategoryTotalsDto {
  category: UsageCategory;
  totalEvents: number;
  totalCostUsd: number;
}

export interface UsageSummaryDto {
  from: string; // ISO
  to: string;   // ISO
  bucket: UsageBucket;
  totals: {
    totalEvents: number;
    totalCostUsd: number;
  };
  timeSeries: UsageTimePointDto[];
  byCategory: UsageCategoryTotalsDto[];
}

export interface ProjectUsageSummaryDto {
  projectId: string;
  projectTitle: string;
  totalEvents: number;
  totalCostUsd: number;
}

export interface UsageEventDto {
  id: string;
  created: string; // ISO
  projectId: string | null;
  notebookId: string | null;
  conversationId: string | null;
  contentFileId: string | null;
  notebookFileId: string | null;
  category: UsageCategory;
  service: string;
  operation: string;
  modelDeploymentId: string | null;
  valueInput: number;
  valueCachedInput: number;
  valueReasoning: number;
  valueOutput: number;
  valueOther: number;
  metadataJson: string | null;
  costUsd: number | null;
}

export interface UsageDetailsQuery {
  from: string; // ISO
  to: string;   // ISO
  projectId?: string;
  category?: UsageCategory;
  service?: string;
  operation?: string;
  page?: number;
  pageSize?: number;
}

export interface PagedResultDto<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface ServiceCostDto {
  service: string;
  totalEvents: number;
  totalCostUsd: number;
}

export interface OperationCostDto {
  service: string;
  operation: string;
  totalEvents: number;
  totalCostUsd: number;
}

export interface UsageBreakdownDto {
  byService: ServiceCostDto[];
  byOperation: OperationCostDto[];
}

export interface ReportCategoryDto {
  id: string;
  key: string;
  title: string;
  description?: string | null;
  totalEvents: number;
  totalCostUsd: number;
}

export interface UsageBreakdownWithCategoriesDto {
  byService: ServiceCostDto[];
  byOperation: OperationCostDto[];
  byCategory: ReportCategoryDto[];
}

// Guide Usage Report Types

export interface CrewMemberUsageDto {
  assistantId: string;
  assistantName: string;
  totalInvocations: number;
  averageInvocationsPerConversation: number;
  totalCost: number;
  averageCostPerConversation: number;
}

export interface ToolUsageDto {
  toolName: string;
  totalCalls: number;
  averageCallsPerConversation: number;
  totalCost: number;
  averageCostPerConversation: number;
}

export interface DailyUsageBucketDto {
  date: string; // ISO date
  // Guide's direct ChatCompletion tokens only
  promptTokens: number;
  cachedTokens: number;
  reasoningTokens: number;
  completionTokens: number;
  // Guide direct + crew/agent invocation subtree tokens
  promptTokensWithCrew: number;
  cachedTokensWithCrew: number;
  reasoningTokensWithCrew: number;
  completionTokensWithCrew: number;
  directCost: number;
  totalCost: number;
}

export interface GuideUsageSummaryDto {
  guideId: string;
  guideName: string;
  fromDate: string;
  toDate: string;
  totalConversations: number;
  totalPromptTokens: number;
  totalCachedTokens: number;
  totalReasoningTokens: number;
  totalCompletionTokens: number;
  totalPromptTokensWithCrew: number;
  totalCachedTokensWithCrew: number;
  totalReasoningTokensWithCrew: number;
  totalCompletionTokensWithCrew: number;
  directCost: number;
  totalCost: number;
}

export interface GuideUsageCrewDto {
  crewMembers: CrewMemberUsageDto[];
  directToolCalls: ToolUsageDto[];
}

export interface GuideUsageConversationsPageDto {
  page: number;
  pageSize: number;
  totalCount: number;
  items: ConversationUsageSummaryDto[];
}

export type GuideUsageSourceFilter = 'all' | 'conversation' | 'published_chat' | 'mcp' | 'wire_api';

export interface GuideApiUsageRowDto {
  sourceChannel: string;
  endpoint: string;
  alias: string;
  providerServiceMode: string;
  statusFamily: string;
  events: number;
  chargeUsd: number;
}

export interface GuideApiUsageReportDto {
  guideId: string;
  guideName: string;
  fromDate: string;
  toDate: string;
  sourceFilter: string;
  totalEvents: number;
  totalChargeUsd: number;
  rows: GuideApiUsageRowDto[];
}

export interface GuideUsageReportDto {
  guideId: string;
  guideName: string;
  fromDate: string; // ISO datetime
  toDate: string;   // ISO datetime
  totalConversations: number;
  // Tokens (guide's direct ChatCompletion only)
  totalPromptTokens: number;
  totalCachedTokens: number;
  totalReasoningTokens: number;
  totalCompletionTokens: number;
  // Tokens including crew/agent invocations (guide direct + all subtrees)
  totalPromptTokensWithCrew: number;
  totalCachedTokensWithCrew: number;
  totalReasoningTokensWithCrew: number;
  totalCompletionTokensWithCrew: number;
  // Costs
  directCost: number;  // Guide's own usage (ChatCompletion + direct tool calls)
  totalCost: number;   // Direct + all crew invocation subtree costs
  // Crew and tool usage
  crewMembers: CrewMemberUsageDto[];
  directToolCalls: ToolUsageDto[];
  // Chart data
  dailyBuckets: DailyUsageBucketDto[];
  // Conversation-level aggregates for drill-down
  conversations: ConversationUsageSummaryDto[];
}

export interface ConversationUsageSummaryDto {
  conversationId: string;
  notebookId: string;
  title: string;
  created: string; // ISO datetime
  totalToolCalls: number;
  assistantSteps: number;
  promptTokens: number;
  cachedTokens: number;
  reasoningTokens: number;
  completionTokens: number;
  totalCost: number;
  turnIndices: number[];
}

// Invocation Detail Types

export interface InvocationMessageDto {
  id: string;
  role: string;
  content: string | null;
  toolCallsJson: string | null;
  functionName: string | null;
  created: string; // ISO datetime
}

export interface InvocationNodeDto {
  id: string;
  parentInvocationId: string | null;
  triggeringToolCallId: string | null;
  assistantName: string;
  assistantId: string | null;
  modelDeploymentId: string | null;
  depth: number;
  status: string;
  promptTokens: number;
  completionTokens: number;
  toolCallCount: number;
  llmRoundTrips: number;
  chargeUsd: number;
  created: string; // ISO datetime
  completed: string | null;
  durationMs: number;
  isOutlier: boolean;
  children: InvocationNodeDto[];
  messages?: InvocationMessageDto[];
}

export interface TurnInvocationTreeDto {
  conversationId: string;
  turnIndex: number;
  turnStarted: string; // ISO datetime
  totalDurationMs: number;
  totalChargeUsd: number;
  totalTokens: number;
  rootInvocations: InvocationNodeDto[];
}

export interface TurnMessageDto {
  id: string;
  role: string;
  content: string | null;
  toolCallsJson: string | null;
  toolCallId: string | null;
  functionName: string | null;
  created: string; // ISO datetime
  agentInvocationId: string | null; // For drill-down into agent invocations
}

export interface TurnMessagesDto {
  conversationId: string;
  turnIndex: number;
  assistantName: string | null;
  turnStarted: string; // ISO datetime
  messages: TurnMessageDto[];
}


