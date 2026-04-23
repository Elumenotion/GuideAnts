import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react';
import { useState } from 'react';
import type {
  HuggingFaceRepositoryFileDto,
  HuggingFaceRepositoryListingDto,
} from '../../../../types/settings';
import {
  RepositoryFilePicker,
  snapshotPreviewClassifier,
  llamaCppClassifier,
  LLAMA_MODEL_ROLE_ID,
  LLAMA_MMPROJ_ROLE_ID,
} from './index';
import type { RolePickerSpec } from './index';

vi.mock('../../../../services/api', () => ({
  api: {
    settings: {
      browseHuggingFaceRepository: vi.fn(),
    },
  },
}));

// eslint-disable-next-line @typescript-eslint/no-var-requires
import { api } from '../../../../services/api';

function makeFile(
  path: string,
  extra: Partial<HuggingFaceRepositoryFileDto> = {},
): HuggingFaceRepositoryFileDto {
  return {
    path,
    size: 1024,
    category: 'other',
    quantLabel: null,
    sharded: false,
    shardIndex: null,
    shardTotal: null,
    ...extra,
  };
}

function makeListing(
  repository: string,
  files: HuggingFaceRepositoryFileDto[],
  extra: Partial<HuggingFaceRepositoryListingDto> = {},
): HuggingFaceRepositoryListingDto {
  return {
    repository,
    gated: false,
    tokenUsed: false,
    modelCardUrl: null,
    files,
    ...extra,
  };
}

function RepoHarness(props: Omit<React.ComponentProps<typeof RepositoryFilePicker>, 'repository' | 'onRepositoryChange'> & {
  initialRepo?: string;
}) {
  const { initialRepo = '', ...rest } = props;
  const [repo, setRepo] = useState(initialRepo);
  return <RepositoryFilePicker {...rest} repository={repo} onRepositoryChange={setRepo} />;
}

describe('RepositoryFilePicker', () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  afterEach(() => {
    cleanup();
  });

  it('refuses to browse empty / malformed repo inputs with an inline error', async () => {
    render(
      <RepoHarness
        previewOnly
        classifyPreview={snapshotPreviewClassifier}
        serviceOrigin="Test"
        initialRepo="not-a-repo"
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));
    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toMatch(/owner\/repo/i);
    });
    expect(api.settings.browseHuggingFaceRepository).not.toHaveBeenCalled();
  });

  it('preview-only mode: browses, renders buckets, and forwards onBrowseResolved', async () => {
    const files = [
      makeFile('model.safetensors', { size: 100 }),
      makeFile('tokenizer.json', { size: 10 }),
      makeFile('config.json', { size: 5 }),
    ];
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce(
      makeListing('acme/model', files),
    );
    const onResolved = vi.fn();
    render(
      <RepoHarness
        previewOnly
        classifyPreview={snapshotPreviewClassifier}
        serviceOrigin="Test"
        initialRepo="acme/model"
        onBrowseResolved={onResolved}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));
    await waitFor(() => {
      expect(onResolved).toHaveBeenCalledTimes(1);
    });
    expect(api.settings.browseHuggingFaceRepository).toHaveBeenCalledWith(
      'acme/model',
      expect.objectContaining({ serviceOrigin: 'Test' }),
    );
    expect(screen.getByText(/model\.safetensors/)).toBeInTheDocument();
    expect(screen.getByText(/tokenizer\.json/)).toBeInTheDocument();
  });

  it('surfaces structured browse errors via onBrowseError and role="alert"', async () => {
    const err = Object.assign(new Error('Repo not found.'), {
      code: 'REPO_NOT_FOUND',
      status: 404,
    });
    (api.settings.browseHuggingFaceRepository as any).mockRejectedValueOnce(err);
    const onError = vi.fn();
    render(
      <RepoHarness
        previewOnly
        classifyPreview={snapshotPreviewClassifier}
        initialRepo="acme/missing"
        onBrowseError={onError}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));
    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toMatch(/Repo not found/);
    });
    expect(onError).toHaveBeenLastCalledWith(
      expect.objectContaining({ code: 'REPO_NOT_FOUND', message: expect.stringMatching(/not found/i) }),
    );
  });

  it('per-file mode: renders role dropdowns and applies auto-select from the classifier', async () => {
    const files = [
      makeFile('model-Q5_K_M.gguf', { category: 'gguf', quantLabel: 'Q5_K_M', size: 1_000 }),
      makeFile('model-Q4_K_M.gguf', { category: 'gguf', quantLabel: 'Q4_K_M', size: 800 }),
      makeFile('mmproj-F16.gguf', { category: 'mmproj', size: 500 }),
    ];
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce(
      makeListing('acme/llama', files),
    );
    const onChange = vi.fn();
    const roles: RolePickerSpec[] = [
      { id: LLAMA_MODEL_ROLE_ID, label: 'Model file (GGUF)' },
      { id: LLAMA_MMPROJ_ROLE_ID, label: 'Vision projector (mmproj)' },
    ];
    render(
      <RepoHarness
        roles={roles}
        classify={llamaCppClassifier}
        onChange={onChange}
        initialRepo="acme/llama"
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));
    await waitFor(() => {
      expect(onChange).toHaveBeenCalled();
    });
    const last = onChange.mock.calls[onChange.mock.calls.length - 1][0] as Record<string, string>;
    expect(last[LLAMA_MODEL_ROLE_ID]).toBe('model-Q5_K_M.gguf');
    expect(last[LLAMA_MMPROJ_ROLE_ID]).toBe('mmproj-F16.gguf');
  });
});

