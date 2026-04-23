import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useDownloadLineage } from '../useDownloadLineage';
import { api } from '../../services/api';

vi.mock('../../services/api', () => ({
  api: {
    lineage: {
      download: vi.fn(),
    },
  },
}));

const apiMocks = (): any => api as any;

// Ensure URL.createObjectURL exists in jsdom environment
if (!(URL as any).createObjectURL) {
  // @ts-ignore
  URL.createObjectURL = () => 'blob:url';
}
const createObjectURLSpy = vi.spyOn(URL, 'createObjectURL');
createObjectURLSpy.mockReturnValue('blob:url');

// Ensure revokeObjectURL exists
if (!(URL as any).revokeObjectURL) {
  // @ts-ignore
  URL.revokeObjectURL = () => {};
}
const revokeSpy = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});

// Spy on anchor click to avoid navigation errors
vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

// Stub alert
window.alert = vi.fn();

describe('useDownloadLineage', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.clearAllMocks();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('calls api and sets downloading state', async () => {
    const blob = new Blob(['data']);
    apiMocks().lineage.download.mockResolvedValue({ blob, contentType: 'text/plain', fileName: 'file.txt' });
    const { result } = renderHook(() => useDownloadLineage());

    expect(result.current.isDownloading).toBe(false);

    await act(async () => {
      await result.current.download('event1', 'suggested.txt');
    });

    // Fast-forward timers to trigger revokeObjectURL timeout
    vi.runAllTimers();

    expect(apiMocks().lineage.download).toHaveBeenCalledWith('event1');
    expect(createObjectURLSpy).toHaveBeenCalledWith(blob);
    expect(revokeSpy).toHaveBeenCalled();
    expect(result.current.isDownloading).toBe(false);
  });

  it('blocks concurrent downloads', async () => {
    let resolve!: (value?: any) => void;
    const promise = new Promise<any>(r => (resolve = r));
    apiMocks().lineage.download.mockReturnValue(promise);
    const { result } = renderHook(() => useDownloadLineage());

    act(() => {
      result.current.download('event1');
    });
    // second call ignored while downloading
    act(() => {
      result.current.download('event1');
    });
    expect(apiMocks().lineage.download).toHaveBeenCalledTimes(1);

    // finish
    await act(async () => resolve());
  });
}); 