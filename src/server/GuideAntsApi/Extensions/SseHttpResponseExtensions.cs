using System.Text;

namespace GuideAntsApi;

public static class SseHttpResponseExtensions
{
    public static async Task WriteSseEventAsync(this HttpResponse response, string eventType, string data, CancellationToken cancellationToken = default)
    {
        var eventData = $"event: {eventType}\ndata: {data}\n\n";
        var bytes = Encoding.UTF8.GetBytes(eventData);
        await response.Body.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
} 