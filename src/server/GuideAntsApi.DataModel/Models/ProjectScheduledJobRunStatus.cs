namespace GuideAntsApi.DataModel.Models;

public enum ProjectScheduledJobRunStatus : byte
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    Cancelled = 3
}
