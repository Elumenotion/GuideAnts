type FileWithPath = File & { path?: string };

type DirectoryPickerHandle = {
  values: () => AsyncIterableIterator<{
    kind: string;
    getFile: () => Promise<File>;
  }>;
};

type DirectoryPickerWindow = Window & {
  showDirectoryPicker?: (options?: { mode?: 'read' | 'readwrite' }) => Promise<DirectoryPickerHandle>;
};

export type PickHostFolderResult =
  | { ok: true; path: string }
  | { ok: false; reason: 'cancelled' | 'unavailable' | 'no-path' };

export function canPickHostFolder(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  const pickerWindow = window as DirectoryPickerWindow;
  return typeof pickerWindow.showDirectoryPicker === 'function'
    || typeof document !== 'undefined';
}

export async function pickHostFolder(): Promise<PickHostFolderResult> {
  const pickerWindow = window as DirectoryPickerWindow;

  if (typeof pickerWindow.showDirectoryPicker === 'function') {
    try {
      const handle = await pickerWindow.showDirectoryPicker({ mode: 'read' });
      const path = await resolveAbsolutePathFromDirectoryHandle(handle);
      if (path) {
        return { ok: true, path };
      }
      return { ok: false, reason: 'no-path' };
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') {
        return { ok: false, reason: 'cancelled' };
      }
      throw error;
    }
  }

  const path = await pickFolderViaWebkitDirectoryInput();
  if (!path) {
    return { ok: false, reason: 'unavailable' };
  }

  return { ok: true, path };
}

async function resolveAbsolutePathFromDirectoryHandle(
  handle: DirectoryPickerHandle,
): Promise<string | null> {
  for await (const entry of handle.values()) {
    if (entry.kind !== 'file') {
      continue;
    }

    const file = await entry.getFile();
    const directoryPath = deriveDirectoryFromFilePath(file as FileWithPath, file.name);
    if (directoryPath) {
      return directoryPath;
    }
  }

  return null;
}

function pickFolderViaWebkitDirectoryInput(): Promise<string | null> {
  return new Promise((resolve) => {
    const input = document.createElement('input');
    input.type = 'file';
    input.style.display = 'none';
    input.setAttribute('webkitdirectory', '');
    input.setAttribute('directory', '');

    const cleanup = () => {
      input.remove();
    };

    input.addEventListener('change', () => {
      const file = input.files?.[0] as FileWithPath | undefined;
      cleanup();
      if (!file) {
        resolve(null);
        return;
      }

      resolve(deriveDirectoryFromFilePath(file, file.name));
    });

    input.addEventListener('cancel', () => {
      cleanup();
      resolve(null);
    });

    document.body.appendChild(input);
    input.click();
  });
}

function deriveDirectoryFromFilePath(file: FileWithPath, fileName: string): string | null {
  const filePath = file.path?.trim();
  if (!filePath) {
    return null;
  }

  const normalized = filePath.replace(/[\\/]+$/, '');
  if (normalized.endsWith(fileName)) {
    const parent = normalized.slice(0, -fileName.length).replace(/[\\/]+$/, '');
    return parent || null;
  }

  const lastSeparator = Math.max(normalized.lastIndexOf('\\'), normalized.lastIndexOf('/'));
  if (lastSeparator > 0) {
    return normalized.slice(0, lastSeparator);
  }

  return null;
}
