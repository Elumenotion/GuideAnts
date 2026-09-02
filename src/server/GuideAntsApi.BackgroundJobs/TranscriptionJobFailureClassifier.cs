namespace GuideAntsApi.BackgroundJobs;

/// <summary>
/// Classifies transcription job failures using structured exception signals instead of log substring matching.
/// </summary>
public static class TranscriptionJobFailureClassifier
{
    public static JobExecutionResult Classify(Exception ex, CancellationToken cancellationToken)
    {
        if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return JobExecutionResult.ShutdownCancellation(ex.Message);
        }

        if (IsPermanentMediaInputFailure(ex))
        {
            return JobExecutionResult.PermanentMissingInput(ex.Message);
        }

        return JobExecutionResult.RetryableTransient(ex.Message);
    }

    public static bool IsPermanentMediaInputFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is ArgumentException)
            {
                return true;
            }

            if (current is not InvalidOperationException invalidOperation)
            {
                continue;
            }

            var message = invalidOperation.Message;
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            if (ContainsPermanentMediaStatusCode(message)
                || message.Contains("output file is empty", StringComparison.OrdinalIgnoreCase)
                || message.Contains("output contains no stream", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Audio extraction failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsPermanentMediaStatusCode(string message)
    {
        const string prefix = "Media extraction API failed (";
        var start = message.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += prefix.Length;
        var end = message.IndexOf(')', start);
        if (end <= start)
        {
            return false;
        }

        if (!int.TryParse(message[start..end], out var statusCode))
        {
            return false;
        }

        return statusCode is 400 or 404 or 422;
    }
}
