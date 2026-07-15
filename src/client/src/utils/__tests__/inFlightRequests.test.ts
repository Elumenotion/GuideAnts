import { describe, it, expect, beforeEach, vi } from 'vitest';
import { dedupeInFlight, __resetInFlightRequests } from '../inFlightRequests';

describe('inFlightRequests', () => {
  beforeEach(() => {
    __resetInFlightRequests();
  });

  it('coalesces concurrent calls for the same key into one factory invocation', async () => {
    let resolveFn: (value: string) => void = () => {};
    const factory = vi.fn(() => new Promise<string>(resolve => { resolveFn = resolve; }));

    const p1 = dedupeInFlight('k', factory);
    const p2 = dedupeInFlight('k', factory);

    expect(factory).toHaveBeenCalledTimes(1);

    resolveFn('done');
    const [r1, r2] = await Promise.all([p1, p2]);

    expect(r1).toBe('done');
    expect(r2).toBe('done');
  });

  it('issues a fresh request after the previous one settles', async () => {
    const factory = vi.fn(async () => 'value');

    await dedupeInFlight('k', factory);
    await dedupeInFlight('k', factory);

    expect(factory).toHaveBeenCalledTimes(2);
  });

  it('does not coalesce calls with different keys', async () => {
    const factory = vi.fn(async () => 'value');

    await Promise.all([
      dedupeInFlight('a', factory),
      dedupeInFlight('b', factory),
    ]);

    expect(factory).toHaveBeenCalledTimes(2);
  });

  it('clears the in-flight entry when the request rejects', async () => {
    const failing = vi.fn(async () => { throw new Error('boom'); });

    await expect(dedupeInFlight('k', failing)).rejects.toThrow('boom');

    const succeeding = vi.fn(async () => 'ok');
    await expect(dedupeInFlight('k', succeeding)).resolves.toBe('ok');
    expect(succeeding).toHaveBeenCalledTimes(1);
  });
});
