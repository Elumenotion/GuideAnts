using FluentAssertions;
using GuideAnts.Usage;

namespace GuideAntsApi.Tests.Usage;

[TestClass]
public sealed class SpeechUsageMetricsTests
{
    [TestMethod]
    public void ForTranscription_bills_on_duration_seconds_not_file_bytes()
    {
        var metrics = SpeechUsageMetrics.ForTranscription(durationSeconds: 9, transcriptLength: 180);

        metrics.ValueOther.Should().Be(9);
        metrics.ValueInput.Should().Be(9);
        metrics.ValueOutput.Should().Be(180);
    }

    [TestMethod]
    public void ForSynthesis_bills_on_character_count_not_audio_bytes()
    {
        var metrics = SpeechUsageMetrics.ForSynthesis(characterCount: 55, durationSeconds: 2);

        metrics.ValueOther.Should().Be(55);
        metrics.ValueInput.Should().Be(55);
        metrics.ValueOutput.Should().Be(2);
    }
}
