/**
 * In-flight request coalescing.
 *
 * When several components request the same resource at the same time (e.g. the
 * notebook page mounts a sidebar, a cell list, and the page shell, each with its
 * own folder-tree poller), they would otherwise fire identical concurrent HTTP
 * requests. `dedupeInFlight` collapses those into a single underlying request:
 * callers that arrive while a request for the same key is pending share its
 * promise, and the entry is cleared as soon as the request settles so the next
 * poll issues a fresh request.
 *
 * This only coalesces overlapping calls; it is not a cache and never returns
 * stale data to a later, non-overlapping call.
 */
const inFlight = new Map<string, Promise<unknown>>();

export function dedupeInFlight<T>(key: string, factory: () => Promise<T>): Promise<T> {
    const existing = inFlight.get(key);
    if (existing) {
        return existing as Promise<T>;
    }

    const promise = (async () => factory())().finally(() => {
        // Only clear if we are still the owner of this key.
        if (inFlight.get(key) === promise) {
            inFlight.delete(key);
        }
    });

    inFlight.set(key, promise);
    return promise as Promise<T>;
}

/** Test helper: drop any tracked in-flight requests. */
export function __resetInFlightRequests(): void {
    inFlight.clear();
}
