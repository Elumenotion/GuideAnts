namespace GuideAntsApi.Services.Components;

/// <summary>
/// Abstraction over Azure Speech synthesis so it can be mocked and reused.
/// </summary>
public interface ISpeechSynthesisService
{
    /// <summary>
    /// Result of a speech synthesis operation.
    /// </summary>
    public record SpeechSynthesisResult(bool Success, long DurationSeconds, string? ErrorMessage = null);

    /// <summary>
    /// Synthesize the provided SSML (or plain text) to a WAV file.
    /// </summary>
    /// <param name="ssml">SSML or plain-text input.</param>
    /// <param name="outputPath">Absolute file path where the WAV file should be written.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A <see cref="SpeechSynthesisResult"/> describing the outcome, including duration in seconds when successful.</returns>
    Task<SpeechSynthesisResult> SynthesizeToWavAsync(string ssml, string outputPath, CancellationToken cancellationToken = default);
}
