namespace GuideAntsApi.BackgroundJobs.Scheduling;

public static class ScheduledJobOutputTruncator
{
    public const int MaxCharacters = 65_536;
    public const int MaxErrorMessageCharacters = 4000;
    private const string TruncationSuffix = "\n[... output truncated for length ...]";
    private const string ErrorMessageTruncationSuffix = "\n[... error message truncated for length ...]";

    public static string? Truncate(string? output)
    {
        if (string.IsNullOrEmpty(output) || output.Length <= MaxCharacters)
        {
            return output;
        }

        return output[..MaxCharacters] + TruncationSuffix;
    }

    public static string? TruncateErrorMessage(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage) || errorMessage.Length <= MaxErrorMessageCharacters)
        {
            return errorMessage;
        }

        var keepLength = MaxErrorMessageCharacters - ErrorMessageTruncationSuffix.Length;
        return errorMessage[..keepLength] + ErrorMessageTruncationSuffix;
    }
}
