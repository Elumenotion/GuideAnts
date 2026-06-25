using System.Text.RegularExpressions;
using GuideAntsApi.Models.Scheduling;

namespace GuideAntsApi.Services.Scheduling;

public interface IScheduleBuilderService
{
    ScheduleValidationResult BuildCron(FriendlyScheduleDto schedule);

    FriendlyScheduleDto ParseToFriendly(string cronExpression);
}

public sealed class ScheduleBuilderService : IScheduleBuilderService
{
    private static readonly Regex DailyPattern = new(@"^(\d{1,2}) (\d{1,2}) \* \* \*$", RegexOptions.Compiled);
    private static readonly Regex HourlyPattern = new(@"^0 \* \* \* \*$", RegexOptions.Compiled);
    private static readonly Regex HourlyIntervalPattern = new(@"^\*/(\d{1,2}) \* \* \* \*$", RegexOptions.Compiled);
    private static readonly Regex WeeklyPattern = new(@"^(\d{1,2}) (\d{1,2}) \* \* ([0-6](?:,[0-6])*)$", RegexOptions.Compiled);
    private static readonly Regex MonthlyPattern = new(@"^(\d{1,2}) (\d{1,2}) (\d{1,2}) \* \*$", RegexOptions.Compiled);

    public ScheduleValidationResult BuildCron(FriendlyScheduleDto schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return schedule.Frequency switch
        {
            ScheduleFrequency.Hourly => BuildHourly(schedule),
            ScheduleFrequency.Daily => BuildDaily(schedule),
            ScheduleFrequency.Weekly => BuildWeekly(schedule),
            ScheduleFrequency.Monthly => BuildMonthly(schedule),
            ScheduleFrequency.Custom => BuildCustom(schedule),
            _ => new ScheduleValidationResult(false, "Unsupported schedule frequency.", null)
        };
    }

    public FriendlyScheduleDto ParseToFriendly(string cronExpression)
    {
        var cron = cronExpression.Trim();

        if (HourlyPattern.IsMatch(cron))
        {
            return new FriendlyScheduleDto(ScheduleFrequency.Hourly, null, null, null, null, null);
        }

        var hourlyInterval = HourlyIntervalPattern.Match(cron);
        if (hourlyInterval.Success && int.TryParse(hourlyInterval.Groups[1].Value, out var interval))
        {
            return new FriendlyScheduleDto(ScheduleFrequency.Hourly, null, null, null, interval, null);
        }

        var daily = DailyPattern.Match(cron);
        if (daily.Success)
        {
            return new FriendlyScheduleDto(
                ScheduleFrequency.Daily,
                FormatTimeOfDay(daily.Groups[2].Value, daily.Groups[1].Value),
                null,
                null,
                null,
                null);
        }

        var weekly = WeeklyPattern.Match(cron);
        if (weekly.Success)
        {
            var days = weekly.Groups[3].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse)
                .ToArray();
            return new FriendlyScheduleDto(
                ScheduleFrequency.Weekly,
                FormatTimeOfDay(weekly.Groups[2].Value, weekly.Groups[1].Value),
                days,
                null,
                null,
                null);
        }

        var monthly = MonthlyPattern.Match(cron);
        if (monthly.Success)
        {
            return new FriendlyScheduleDto(
                ScheduleFrequency.Monthly,
                FormatTimeOfDay(monthly.Groups[2].Value, monthly.Groups[1].Value),
                null,
                int.Parse(monthly.Groups[3].Value),
                null,
                null);
        }

        return new FriendlyScheduleDto(ScheduleFrequency.Custom, null, null, null, null, cron);
    }

    private static ScheduleValidationResult BuildHourly(FriendlyScheduleDto schedule)
    {
        if (schedule.HourlyIntervalMinutes is > 0 and <= 59)
        {
            return Valid($"*/{schedule.HourlyIntervalMinutes} * * * *");
        }

        return Valid("0 * * * *");
    }

    private static ScheduleValidationResult BuildDaily(FriendlyScheduleDto schedule)
    {
        if (!TryParseTimeOfDay(schedule.TimeOfDay, out var hour, out var minute, out var error))
        {
            return Invalid(error);
        }

        return Valid($"{minute} {hour} * * *");
    }

    private static ScheduleValidationResult BuildWeekly(FriendlyScheduleDto schedule)
    {
        if (schedule.DaysOfWeek is not { Length: > 0 })
        {
            return Invalid("Select at least one day of the week.");
        }

        if (schedule.DaysOfWeek.Any(d => d is < 0 or > 6))
        {
            return Invalid("Days of week must be between 0 (Sunday) and 6 (Saturday).");
        }

        if (!TryParseTimeOfDay(schedule.TimeOfDay, out var hour, out var minute, out var error))
        {
            return Invalid(error);
        }

        var days = string.Join(',', schedule.DaysOfWeek.Distinct().OrderBy(d => d));
        return Valid($"{minute} {hour} * * {days}");
    }

    private static ScheduleValidationResult BuildMonthly(FriendlyScheduleDto schedule)
    {
        if (schedule.DayOfMonth is not int dayOfMonth || dayOfMonth is < 1 or > 31)
        {
            return Invalid("Day of month must be between 1 and 31.");
        }

        if (!TryParseTimeOfDay(schedule.TimeOfDay, out var hour, out var minute, out var error))
        {
            return Invalid(error);
        }

        return Valid($"{minute} {hour} {dayOfMonth} * *");
    }

    private static ScheduleValidationResult BuildCustom(FriendlyScheduleDto schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule.CustomCronExpression))
        {
            return Invalid("Custom cron expression is required.");
        }

        var cron = schedule.CustomCronExpression.Trim();
        try
        {
            Cronos.CronExpression.Parse(cron, Cronos.CronFormat.Standard);
            return Valid(cron);
        }
        catch (Exception ex)
        {
            return Invalid(ex.Message);
        }
    }

    private static bool TryParseTimeOfDay(string? timeOfDay, out int hour, out int minute, out string? error)
    {
        hour = 0;
        minute = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(timeOfDay))
        {
            error = "Time of day is required.";
            return false;
        }

        var parts = timeOfDay.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out hour) ||
            !int.TryParse(parts[1], out minute) ||
            hour is < 0 or > 23 ||
            minute is < 0 or > 59)
        {
            error = "Time of day must use HH:mm format.";
            return false;
        }

        return true;
    }

    private static string FormatTimeOfDay(string hour, string minute) =>
        $"{int.Parse(hour):00}:{int.Parse(minute):00}";

    private static ScheduleValidationResult Valid(string cron) =>
        new(true, null, cron);

    private static ScheduleValidationResult Invalid(string? error) =>
        new(false, error, null);
}
