using GuideAntsApi.Models.Scheduling;
using GuideAntsApi.Services.Scheduling;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Scheduling;

[TestClass]
public sealed class ScheduleBuilderServiceTests
{
    private readonly ScheduleBuilderService _service = new();

    [TestMethod]
    public void BuildCron_Daily_ReturnsExpectedExpression()
    {
        var result = _service.BuildCron(new FriendlyScheduleDto(
            ScheduleFrequency.Daily,
            "09:30",
            null,
            null,
            null,
            null));

        result.IsValid.Should().BeTrue();
        result.CronExpression.Should().Be("30 9 * * *");
    }

    [TestMethod]
    public void ParseToFriendly_Daily_RoundTrips()
    {
        var friendly = _service.ParseToFriendly("30 9 * * *");
        friendly.Frequency.Should().Be(ScheduleFrequency.Daily);
        friendly.TimeOfDay.Should().Be("09:30");
    }

    [TestMethod]
    public void BuildCron_Weekly_IncludesSelectedDays()
    {
        var result = _service.BuildCron(new FriendlyScheduleDto(
            ScheduleFrequency.Weekly,
            "08:00",
            [1, 3, 5],
            null,
            null,
            null));

        result.IsValid.Should().BeTrue();
        result.CronExpression.Should().Be("0 8 * * 1,3,5");
    }

    [TestMethod]
    public void BuildCron_Monthly_ReturnsExpectedExpression()
    {
        var result = _service.BuildCron(new FriendlyScheduleDto(
            ScheduleFrequency.Monthly,
            "06:15",
            null,
            15,
            null,
            null));

        result.IsValid.Should().BeTrue();
        result.CronExpression.Should().Be("15 6 15 * *");
    }

    [TestMethod]
    public void BuildCron_Monthly_NullDayOfMonth_IsInvalid()
    {
        var result = _service.BuildCron(new FriendlyScheduleDto(
            ScheduleFrequency.Monthly,
            "06:15",
            null,
            null,
            null,
            null));

        result.IsValid.Should().BeFalse();
        result.CronExpression.Should().BeNull();
        result.ErrorMessage.Should().Contain("Day of month");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(32)]
    [DataRow(45)]
    [DataRow(-1)]
    public void BuildCron_Monthly_OutOfRangeDayOfMonth_IsInvalid(int dayOfMonth)
    {
        var result = _service.BuildCron(new FriendlyScheduleDto(
            ScheduleFrequency.Monthly,
            "06:15",
            null,
            dayOfMonth,
            null,
            null));

        result.IsValid.Should().BeFalse();
        result.CronExpression.Should().BeNull();
        result.ErrorMessage.Should().Contain("Day of month");
    }

    [TestMethod]
    public void ParseToFriendly_Monthly_RoundTrips()
    {
        var friendly = _service.ParseToFriendly("15 6 15 * *");
        friendly.Frequency.Should().Be(ScheduleFrequency.Monthly);
        friendly.TimeOfDay.Should().Be("06:15");
        friendly.DayOfMonth.Should().Be(15);
    }
}
