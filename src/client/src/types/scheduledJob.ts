export type ScheduledJobType = 'NewConversation' | 'RunPythonScript';

export type ScheduleFrequency = 'Hourly' | 'Daily' | 'Weekly' | 'Monthly' | 'Custom';

export type ScheduledJobRunStatus = 'Running' | 'Succeeded' | 'Failed' | 'Cancelled';

export type ScheduledJobTrigger = 'Schedule' | 'Manual';

export interface FriendlyScheduleDto {
  frequency: ScheduleFrequency;
  timeOfDay?: string | null;
  daysOfWeek?: number[] | null;
  dayOfMonth?: number | null;
  hourlyIntervalMinutes?: number | null;
  customCronExpression?: string | null;
}

export interface ProjectScheduledJobSummaryDto {
  id: string;
  name: string;
  jobType: ScheduledJobType;
  notebookId: string;
  notebookTitle: string;
  isEnabled: boolean;
  cronExpression: string;
  timeZoneId: string;
  scheduleSummary: string;
  friendlySchedule: FriendlyScheduleDto;
  nextRunUtc?: string | null;
  lastRunUtc?: string | null;
  lastRunStatus?: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface ProjectScheduledJobDetailDto extends ProjectScheduledJobSummaryDto {
  conversationTitle?: string | null;
  prompt?: string | null;
  assistantName?: string | null;
  scriptNotebookFileId?: string | null;
  scriptRelativePath?: string | null;
  exposeSandboxWireApi: boolean;
  wireTargetAssistantId?: string | null;
  wireAttributionConversationTitle?: string | null;
  wireCreateAttributionConversationPerRun: boolean;
  wireDailyLimitUsd?: number | null;
  wireMonthlyLimitUsd?: number | null;
  createdByUserId: string;
}

export interface CreateProjectScheduledJobRequest {
  name: string;
  jobType: ScheduledJobType;
  notebookId: string;
  isEnabled: boolean;
  timeZoneId: string;
  schedule: FriendlyScheduleDto;
  conversationTitle?: string | null;
  prompt?: string | null;
  assistantName?: string | null;
  scriptNotebookFileId?: string | null;
  exposeSandboxWireApi?: boolean;
  wireTargetAssistantId?: string | null;
  wireAttributionConversationTitle?: string | null;
  wireCreateAttributionConversationPerRun?: boolean;
  wireDailyLimitUsd?: number | null;
  wireMonthlyLimitUsd?: number | null;
}

export type UpdateProjectScheduledJobRequest = CreateProjectScheduledJobRequest;

export interface ProjectScheduledJobRunSummaryDto {
  id: string;
  triggeredBy: ScheduledJobTrigger;
  startedUtc: string;
  completedUtc?: string | null;
  status: ScheduledJobRunStatus;
  errorMessage?: string | null;
  createdConversationId?: string | null;
  exitCode?: number | null;
}

export interface ProjectScheduledJobRunDetailDto extends ProjectScheduledJobRunSummaryDto {
  standardOutput?: string | null;
  standardError?: string | null;
}

export interface PagedProjectScheduledJobRunsDto {
  items: ProjectScheduledJobRunSummaryDto[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export const DAY_LABELS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'] as const;

export function defaultFriendlySchedule(): FriendlyScheduleDto {
  return {
    frequency: 'Daily',
    timeOfDay: '09:00',
    daysOfWeek: [1, 2, 3, 4, 5],
    dayOfMonth: 1,
    hourlyIntervalMinutes: 60,
    customCronExpression: '',
  };
}

export function buildScheduleSummary(schedule: FriendlyScheduleDto, timeZoneId: string): string {
  switch (schedule.frequency) {
    case 'Hourly':
      return schedule.hourlyIntervalMinutes && schedule.hourlyIntervalMinutes < 60
        ? `Every ${schedule.hourlyIntervalMinutes} minutes (${timeZoneId})`
        : `Every hour (${timeZoneId})`;
    case 'Daily':
      return `Daily at ${schedule.timeOfDay ?? '09:00'} (${timeZoneId})`;
    case 'Weekly':
      return `Weekly at ${schedule.timeOfDay ?? '09:00'} (${timeZoneId})`;
    case 'Monthly':
      return `Monthly on day ${schedule.dayOfMonth ?? 1} at ${schedule.timeOfDay ?? '09:00'} (${timeZoneId})`;
    case 'Custom':
      return `Custom: ${schedule.customCronExpression ?? ''} (${timeZoneId})`;
    default:
      return timeZoneId;
  }
}
