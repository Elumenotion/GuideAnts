import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { useFileLineage } from '../useFileLineage';
import { FileLineageEvent } from '../../types/fileLineage';
import { api } from '../../services/api';

vi.mock('../../services/api', () => ({
  api: {
    projects: {
      getFileHistory: vi.fn(),
    },
  },
}));

// After vi.mock hoisting above, the imported `api` reference is already the mocked object.
const apiMocks = (): any => api as any;

const eventsFixture: FileLineageEvent[] = [
  {
    id: 'e1',
    action: 'Uploaded',
    fileKind: 'Project',
    fileId: 'f1',
    projectId: 'p1',
    timestamp: new Date().toISOString(),
    userId: 'u1',
    fileName: 'file.txt',
    versionNumber: 1,
  },
];

describe('useFileLineage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('loads data and sorts newest first', async () => {
    // Return events in reverse order to verify sorting
    const older = { ...eventsFixture[0], id: 'e0', timestamp: new Date(Date.now() - 100000).toISOString() };
    apiMocks().projects.getFileHistory.mockResolvedValue([older, eventsFixture[0]]);

    const { result } = renderHook(() => useFileLineage('p1', 'f1'));

    expect(result.current.isLoading).toBe(true);

    await waitFor(() => expect(result.current.isLoading).toBe(false));
    expect(result.current.error).toBeNull();
    expect(result.current.events[0].id).toBe('e1'); // newest first
  });

  it('exposes refresh method', async () => {
    apiMocks().projects.getFileHistory.mockResolvedValue(eventsFixture);
    const { result } = renderHook(() => useFileLineage('p1', 'f1'));
    await waitFor(() => expect(result.current.isLoading).toBe(false));

    apiMocks().projects.getFileHistory.mockResolvedValue([]);
    await act(async () => {
      await result.current.refresh();
    });
    expect(result.current.events).toHaveLength(0);
  });

  it('handles error state', async () => {
    apiMocks().projects.getFileHistory.mockRejectedValue(new Error('fail'));
    const { result } = renderHook(() => useFileLineage('p1', 'f1'));
    await waitFor(() => expect(result.current.isLoading).toBe(false));
    expect(result.current.error).toBe('fail');
  });
}); 