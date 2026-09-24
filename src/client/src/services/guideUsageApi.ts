import { API_BASE_URL } from '../config/apiConfig';
import { withAuthFetchInit, withAuthHeaders } from './authService';
import { broadcastAuthExpired } from './authEvents';
import type {
  DailyUsageBucketDto,
  GuideUsageConversationsPageDto,
  GuideUsageCrewDto,
  GuideApiUsageReportDto,
  GuideUsageSourceFilter,
  GuideUsageSummaryDto,
  InvocationNodeDto,
  TurnInvocationTreeDto,
  TurnMessagesDto,
} from '../types/usage';

function getHeaders(): HeadersInit {
  return withAuthHeaders({
    'Content-Type': 'application/json',
  });
}

function handleUnauthorized(response: Response): void {
  if (response.status === 401) {
    broadcastAuthExpired('Authentication expired.');
  }
}

export const guideUsageApi = {
  async getGuideUsageSummary(
    guideId: string,
    from: string,
    to: string,
    projectId?: string
  ): Promise<GuideUsageSummaryDto> {
    const params = new URLSearchParams({ from, to });
    const url = projectId
      ? `${API_BASE_URL}/projects/${projectId}/guides/${guideId}/usage/summary?${params}`
      : `${API_BASE_URL}/guides/${guideId}/usage/summary?${params}`;
    
    const response = await fetch(url, withAuthFetchInit({
      method: 'GET',
      headers: getHeaders(),
    }));
    handleUnauthorized(response);
    
    if (!response.ok) {
      throw new Error(`Failed to fetch guide usage summary: ${response.status}`);
    }
    
    return response.json();
  },

  async getAssistantUsageSummary(
    assistantId: string,
    from: string,
    to: string,
    projectId?: string
  ): Promise<GuideUsageSummaryDto> {
    const params = new URLSearchParams({ from, to });
    const url = projectId
      ? `${API_BASE_URL}/projects/${projectId}/assistants/${assistantId}/usage/summary?${params}`
      : `${API_BASE_URL}/assistants/${assistantId}/usage/summary?${params}`;
    
    const response = await fetch(url, withAuthFetchInit({
      method: 'GET',
      headers: getHeaders(),
    }));
    handleUnauthorized(response);
    
    if (!response.ok) {
      throw new Error(`Failed to fetch assistant usage summary: ${response.status}`);
    }
    
    return response.json();
  },

  async getGuideUsageCharts(
    guideId: string,
    from: string,
    to: string,
    projectId?: string
  ): Promise<DailyUsageBucketDto[]> {
    const params = new URLSearchParams({ from, to });
    const url = projectId
      ? `${API_BASE_URL}/projects/${projectId}/guides/${guideId}/usage/charts?${params}`
      : `${API_BASE_URL}/guides/${guideId}/usage/charts?${params}`;
    const response = await fetch(url, withAuthFetchInit({ method: 'GET', headers: getHeaders() }));
    handleUnauthorized(response);
    if (!response.ok) {
      throw new Error(`Failed to fetch guide usage charts: ${response.status}`);
    }
    return response.json();
  },

  async getAssistantUsageCharts(
    assistantId: string,
    from: string,
    to: string,
    projectId?: string
  ): Promise<DailyUsageBucketDto[]> {
    const params = new URLSearchParams({ from, to });
    const url = projectId
      ? `${API_BASE_URL}/projects/${projectId}/assistants/${assistantId}/usage/charts?${params}`
      : `${API_BASE_URL}/assistants/${assistantId}/usage/charts?${params}`;
    const response = await fetch(url, withAuthFetchInit({ method: 'GET', headers: getHeaders() }));
    handleUnauthorized(response);
    if (!response.ok) {
      throw new Error(`Failed to fetch assistant usage charts: ${response.status}`);
    }
    return response.json();
  },

  async getGuideUsageCrew(
    guideId: string,
    from: string,
    to: string,
    projectId?: string
  ): Promise<GuideUsageCrewDto> {
    const params = new URLSearchParams({ from, to });
    const url = projectId
      ? `${API_BASE_URL}/projects/${projectId}/guides/${guideId}/usage/crew?${params}`
      : `${API_BASE_URL}/guides/${guideId}/usage/crew?${params}`;
    const response = await fetch(url, withAuthFetchInit({ method: 'GET', headers: getHeaders() }));
    handleUnauthorized(response);
    if (!response.ok) {
      throw new Error(`Failed to fetch guide usage crew: ${response.status}`);
    }
    return response.json();
  },

  async getAssistantUsageCrew(
    assistantId: string,
    from: string,
    to: string,
    projectId?: string
  ): Promise<GuideUsageCrewDto> {
    const params = new URLSearchParams({ from, to });
    const url = projectId
      ? `${API_BASE_URL}/projects/${projectId}/assistants/${assistantId}/usage/crew?${params}`
      : `${API_BASE_URL}/assistants/${assistantId}/usage/crew?${params}`;
    const response = await fetch(url, withAuthFetchInit({ method: 'GET', headers: getHeaders() }));
    handleUnauthorized(response);
    if (!response.ok) {
      throw new Error(`Failed to fetch assistant usage crew: ${response.status}`);
    }
    return response.json();
  },

  async getGuideUsageConversations(
    guideId: string,
    from: string,
    to: string,
    page: number,
    pageSize: number,
    projectId?: string
  ): Promise<GuideUsageConversationsPageDto> {
    const params = new URLSearchParams({ from, to, page: String(page), pageSize: String(pageSize) });
    const url = projectId
      ? `${API_BASE_URL}/projects/${projectId}/guides/${guideId}/usage/conversations?${params}`
      : `${API_BASE_URL}/guides/${guideId}/usage/conversations?${params}`;
    const response = await fetch(url, withAuthFetchInit({ method: 'GET', headers: getHeaders() }));
    handleUnauthorized(response);
    if (!response.ok) {
      throw new Error(`Failed to fetch guide usage conversations: ${response.status}`);
    }
    return response.json();
  },

  async getAssistantUsageConversations(
    assistantId: string,
    from: string,
    to: string,
    page: number,
    pageSize: number,
    projectId?: string
  ): Promise<GuideUsageConversationsPageDto> {
    const params = new URLSearchParams({ from, to, page: String(page), pageSize: String(pageSize) });
    const url = projectId
      ? `${API_BASE_URL}/projects/${projectId}/assistants/${assistantId}/usage/conversations?${params}`
      : `${API_BASE_URL}/assistants/${assistantId}/usage/conversations?${params}`;
    const response = await fetch(url, withAuthFetchInit({ method: 'GET', headers: getHeaders() }));
    handleUnauthorized(response);
    if (!response.ok) {
      throw new Error(`Failed to fetch assistant usage conversations: ${response.status}`);
    }
    return response.json();
  },

  async getGuideApiUsage(
    guideId: string,
    from: string,
    to: string,
    source: GuideUsageSourceFilter = 'all',
    projectId?: string
  ): Promise<GuideApiUsageReportDto> {
    const params = new URLSearchParams({ from, to });
    if (source !== 'all') {
      params.set('source', source);
    }
    const url = projectId
      ? `${API_BASE_URL}/projects/${projectId}/guides/${guideId}/usage/api?${params}`
      : `${API_BASE_URL}/guides/${guideId}/usage/api?${params}`;
    const response = await fetch(url, withAuthFetchInit({ method: 'GET', headers: getHeaders() }));
    handleUnauthorized(response);
    if (!response.ok) {
      throw new Error(`Failed to fetch guide API usage: ${response.status}`);
    }
    return response.json();
  },

  async getAssistantApiUsage(
    assistantId: string,
    from: string,
    to: string,
    source: GuideUsageSourceFilter = 'all',
    projectId?: string
  ): Promise<GuideApiUsageReportDto> {
    const params = new URLSearchParams({ from, to });
    if (source !== 'all') {
      params.set('source', source);
    }
    const url = projectId
      ? `${API_BASE_URL}/projects/${projectId}/assistants/${assistantId}/usage/api?${params}`
      : `${API_BASE_URL}/assistants/${assistantId}/usage/api?${params}`;
    const response = await fetch(url, withAuthFetchInit({ method: 'GET', headers: getHeaders() }));
    handleUnauthorized(response);
    if (!response.ok) {
      throw new Error(`Failed to fetch assistant API usage: ${response.status}`);
    }
    return response.json();
  },

  /**
   * Get a single invocation with its messages.
   * Supports both AgentInvocation IDs and NotebookConversation IDs.
   */
  async getInvocation(
    invocationId: string, 
    includeMessages: boolean = true
  ): Promise<InvocationNodeDto> {
    const url = `${API_BASE_URL}/invocations/${invocationId}?includeMessages=${includeMessages}`;
    
    const response = await fetch(url, withAuthFetchInit({
      method: 'GET',
      headers: getHeaders(),
    }));
    handleUnauthorized(response);
    
    if (!response.ok) {
      throw new Error(`Failed to fetch invocation: ${response.status}`);
    }
    
    return response.json();
  },

  /**
   * Get the full invocation tree for a conversation turn.
   */
  async getTurnInvocations(
    conversationId: string, 
    turnIndex: number
  ): Promise<TurnInvocationTreeDto> {
    const url = `${API_BASE_URL}/conversations/${conversationId}/turns/${turnIndex}/invocations`;
    
    const response = await fetch(url, withAuthFetchInit({
      method: 'GET',
      headers: getHeaders(),
    }));
    handleUnauthorized(response);
    
    if (!response.ok) {
      throw new Error(`Failed to fetch turn invocations: ${response.status}`);
    }
    
    return response.json();
  },

  /**
   * Get invocation trees for ALL turns in a conversation in a single request.
   * More efficient than calling getTurnInvocations for each turn.
   */
  async getAllTurnInvocations(
    conversationId: string
  ): Promise<TurnInvocationTreeDto[]> {
    const url = `${API_BASE_URL}/conversations/${conversationId}/invocations`;
    
    const response = await fetch(url, withAuthFetchInit({
      method: 'GET',
      headers: getHeaders(),
    }));
    handleUnauthorized(response);
    
    if (!response.ok) {
      throw new Error(`Failed to fetch all turn invocations: ${response.status}`);
    }
    
    return response.json();
  },

  /**
   * Get conversation messages for a turn with AgentInvocation links for drill-down.
   */
  async getTurnMessages(
    conversationId: string,
    turnIndex: number
  ): Promise<TurnMessagesDto> {
    const url = `${API_BASE_URL}/conversations/${conversationId}/turns/${turnIndex}/messages`;
    
    const response = await fetch(url, withAuthFetchInit({
      method: 'GET',
      headers: getHeaders(),
    }));
    handleUnauthorized(response);
    
    if (!response.ok) {
      throw new Error(`Failed to fetch turn messages: ${response.status}`);
    }
    
    return response.json();
  },
};
