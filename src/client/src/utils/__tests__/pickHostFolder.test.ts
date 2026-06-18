import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { canPickHostFolder, pickHostFolder } from '../pickHostFolder';

describe('pickHostFolder', () => {
  type PickerWindow = Window & {
    showDirectoryPicker?: (options?: { mode?: 'read' | 'readwrite' }) => Promise<unknown>;
  };

  const pickerWindow = window as PickerWindow;
  const originalShowDirectoryPicker = pickerWindow.showDirectoryPicker;

  beforeEach(() => {
    vi.restoreAllMocks();
  });

  afterEach(() => {
    pickerWindow.showDirectoryPicker = originalShowDirectoryPicker;
  });

  it('reports picker availability when showDirectoryPicker exists', () => {
    pickerWindow.showDirectoryPicker = vi.fn();
    expect(canPickHostFolder()).toBe(true);
  });

  it('uses showDirectoryPicker and derives directory path from file.path', async () => {
    const file = {
      name: 'readme.txt',
      path: 'D:\\repos\\GuideAnts\\readme.txt',
    } as File & { path: string };

    type DirectoryPickerHandle = {
      values: () => AsyncIterableIterator<{
        kind: string;
        getFile: () => Promise<File>;
      }>;
    };

    const handle: DirectoryPickerHandle = {
      values: async function* () {
        yield {
          kind: 'file',
          async getFile() {
            return file;
          },
        };
      },
    };

    (window as Window & { showDirectoryPicker?: (options?: { mode?: 'read' | 'readwrite' }) => Promise<DirectoryPickerHandle> })
      .showDirectoryPicker = vi.fn(async () => handle);

    const result = await pickHostFolder();

    expect(result).toEqual({ ok: true, path: 'D:\\repos\\GuideAnts' });
  });

  it('returns cancelled when picker is aborted', async () => {
    pickerWindow.showDirectoryPicker = vi.fn(async () => {
      throw new DOMException('Aborted', 'AbortError');
    });

    const result = await pickHostFolder();

    expect(result).toEqual({ ok: false, reason: 'cancelled' });
  });
});
