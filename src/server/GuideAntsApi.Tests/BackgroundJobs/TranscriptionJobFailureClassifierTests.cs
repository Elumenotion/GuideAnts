using FluentAssertions;
using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.DataModel.Media;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class TranscriptionJobFailureClassifierTests
{
    [TestMethod]
    public void Classify_ReturnsShutdownCancellation_WhenTokenCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = TranscriptionJobFailureClassifier.Classify(
            new OperationCanceledException("host stopping"),
            cts.Token);

        result.IsSuccess.Should().BeFalse();
        result.FailureClass.Should().Be(JobFailureClass.ShutdownCancellation);
    }

    [TestMethod]
    public void Classify_ReturnsPermanentMissingInput_ForMediaExtraction400()
    {
        var result = TranscriptionJobFailureClassifier.Classify(
            new InvalidOperationException("Media extraction API failed (400): output contains no stream"),
            CancellationToken.None);

        result.FailureClass.Should().Be(JobFailureClass.PermanentMissingInput);
    }

    [TestMethod]
    public void Classify_ReturnsPermanentMissingInput_ForEmptyOutputFile()
    {
        var result = TranscriptionJobFailureClassifier.Classify(
            new InvalidOperationException("Audio extraction failed - output file is empty"),
            CancellationToken.None);

        result.FailureClass.Should().Be(JobFailureClass.PermanentMissingInput);
    }

    [TestMethod]
    public void Classify_ReturnsPermanentMissingInput_ForFfmpegNoAudioStreamMessage()
    {
        var result = TranscriptionJobFailureClassifier.Classify(
            new InvalidOperationException(
                "Media extraction API failed (500): ffmpeg failed with exit code 234: [out#0/mp3 @ 0x1] Output file does not contain any stream"),
            CancellationToken.None);

        result.FailureClass.Should().Be(JobFailureClass.PermanentMissingInput);
    }

    [TestMethod]
    public void Classify_ReturnsPermanentMissingInput_ForNoAudioStreamErrorType()
    {
        var result = TranscriptionJobFailureClassifier.Classify(
            new InvalidOperationException(
                "Media extraction API failed (422): [NO_AUDIO_STREAM] Source file contains no audio stream: .system/media-extract/abc/input.mp4"),
            CancellationToken.None);

        result.FailureClass.Should().Be(JobFailureClass.PermanentMissingInput);
    }

    [TestMethod]
    public void Classify_ReturnsPermanentMissingInput_ForUnreadableMediaErrorType()
    {
        var result = TranscriptionJobFailureClassifier.Classify(
            new InvalidOperationException(
                "Media extraction API failed (422): [UNREADABLE_MEDIA] Source file could not be probed: Invalid data found when processing input"),
            CancellationToken.None);

        result.FailureClass.Should().Be(JobFailureClass.PermanentMissingInput);
    }

    [TestMethod]
    public void Classify_ReturnsPermanentMissingInput_ForNoAudioStreamExceptionType()
    {
        var result = TranscriptionJobFailureClassifier.Classify(
            new MediaNoAudioStreamException(".system/media-extract/abc/input.mp4"),
            CancellationToken.None);

        result.FailureClass.Should().Be(JobFailureClass.PermanentMissingInput);
    }

    [TestMethod]
    public void Classify_ReturnsRetryableTransient_ForTransientTransportFailure()
    {
        var result = TranscriptionJobFailureClassifier.Classify(
            new HttpRequestException("connection reset"),
            CancellationToken.None);

        result.FailureClass.Should().Be(JobFailureClass.RetryableTransient);
    }

    [TestMethod]
    public void CanRetry_ShutdownCancellationNeverRetries()
    {
        var policy = new JobRetryPolicy(new JobRetryOptions());
        var created = DateTime.UtcNow;

        policy.CanRetry(
                JobFailureClass.ShutdownCancellation,
                attemptsAfterFailure: 1,
                maxAttempts: 40,
                created,
                created.AddMinutes(1),
                TimeSpan.FromMinutes(2))
            .Should().BeFalse();
    }
}
