using Cronos;
using GuideAntsApi.Models.Scheduling;

namespace GuideAntsApi.Services.Scheduling;

public interface ICronScheduleService
{
    bool TryValidate(string cronExpression, out string? errorMessage);

    DateTime? GetNextOccurrenceUtc(string cronExpression, string timeZoneId, DateTime afterUtc);

    string GetHumanReadableSummary(string cronExpression, string timeZoneId, FriendlyScheduleDto? friendly = null);
}

public sealed class CronScheduleService : ICronScheduleService
{
    public bool TryValidate(string cronExpression, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(cronExpression))
        {
            errorMessage = "Cron expression is required.";
            return false;
        }

        try
        {
            CronExpression.Parse(cronExpression.Trim(), CronFormat.Standard);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public DateTime? GetNextOccurrenceUtc(string cronExpression, string timeZoneId, DateTime afterUtc)
    {
        var expression = CronExpression.Parse(cronExpression.Trim(), CronFormat.Standard);
        var timeZone = ResolveTimeZone(timeZoneId);
        var after = new DateTimeOffset(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), TimeSpan.Zero);
        var next = expression.GetNextOccurrence(after, timeZone, inclusive: false);
        return next?.UtcDateTime;
    }

    public string GetHumanReadableSummary(string cronExpression, string timeZoneId, FriendlyScheduleDto? friendly = null)
    {
        if (friendly != null)
        {
            return friendly.Frequency switch
            {
                ScheduleFrequency.Hourly => friendly.HourlyIntervalMinutes is > 0 and <= 59
                    ? $"Every {friendly.HourlyIntervalMinutes} minutes ({timeZoneId})"
                    : $"Every hour ({timeZoneId})",
                ScheduleFrequency.Daily => $"Daily at {friendly.TimeOfDay ?? "00:00"} ({timeZoneId})",
                ScheduleFrequency.Weekly => $"Weekly at {friendly.TimeOfDay ?? "00:00"} ({timeZoneId})",
                ScheduleFrequency.Monthly => $"Monthly on day {friendly.DayOfMonth ?? 1} at {friendly.TimeOfDay ?? "00:00"} ({timeZoneId})",
                ScheduleFrequency.Custom => $"Custom: {cronExpression} ({timeZoneId})",
                _ => cronExpression
            };
        }

        return $"{cronExpression} ({timeZoneId})";
    }

    internal static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            throw new ArgumentException($"Unknown timezone '{timeZoneId}'.", nameof(timeZoneId));
        }
        catch (InvalidTimeZoneException)
        {
            throw new ArgumentException($"Invalid timezone '{timeZoneId}'.", nameof(timeZoneId));
        }
    }
}
