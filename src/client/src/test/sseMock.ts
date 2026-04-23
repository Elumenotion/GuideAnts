/*
 * sseMock.ts – Helper to simulate a Server-Sent Events (SSE) stream in tests.
 *
 * Usage example:
 *   const stream = createSseStream([
 *     { event: 'token', data: 'Hel' },
 *     { event: 'token', data: 'lo' },
 *     { event: 'complete', data: '' },
 *   ]);
 *
 *   const response = new Response(stream, { headers: { 'Content-Type': 'text/event-stream' } });
 */

interface SseEvent {
  /** `event:` field – custom event name */
  event?: string;
  /** `data:` payload; will be JSON.stringify()'d if object */
  data: string | Record<string, unknown>;
  /** Optional `id:` line */
  id?: string;
  /** Optional `retry:` line */
  retry?: number;
}

/**
 * Turns an array of event objects into a Web-stream mimicking an SSE response.
 */
export function createSseStream(events: SseEvent[], delayMs = 0): ReadableStream<Uint8Array> {
  const encoder = new TextEncoder();

  return new ReadableStream<Uint8Array>({
    async start(controller) {
      for (const evt of events) {
        // Compose SSE lines
        if (evt.id) controller.enqueue(encoder.encode(`id: ${evt.id}\n`));
        if (evt.event) controller.enqueue(encoder.encode(`event: ${evt.event}\n`));
        const dataLine =
          typeof evt.data === 'string' ? evt.data : JSON.stringify(evt.data);
        controller.enqueue(encoder.encode(`data: ${dataLine}\n\n`));

        if (delayMs) {
          await new Promise((r) => setTimeout(r, delayMs));
        }
      }
      controller.close();
    },
  });
} 