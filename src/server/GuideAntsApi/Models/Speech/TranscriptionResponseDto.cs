namespace GuideAntsApi.Models.Speech;

/// <summary>
/// Response DTO for audio transcription results.
/// </summary>
public class TranscriptionResponseDto
{
    /// <summary>
    /// The transcribed text from the audio.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Duration of the audio in seconds.
    /// </summary>
    public long DurationSeconds { get; set; }
}

