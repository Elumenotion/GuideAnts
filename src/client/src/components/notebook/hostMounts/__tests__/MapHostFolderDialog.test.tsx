import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { MapHostFolderDialog } from './MapHostFolderDialog';

vi.mock('../../../utils/pickHostFolder', () => ({
  canPickHostFolder: vi.fn(() => true),
  pickHostFolder: vi.fn(),
}));

import { canPickHostFolder, pickHostFolder } from '../../../utils/pickHostFolder';

describe('MapHostFolderDialog', () => {
  const onClose = vi.fn();
  const onSubmit = vi.fn(async () => undefined);

  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(canPickHostFolder).mockReturnValue(true);
  });

  it('shows browse button when folder picker is available', () => {
    render(<MapHostFolderDialog isOpen onClose={onClose} onSubmit={onSubmit} />);
    expect(screen.getByTestId('host-mount-browse-button')).toBeInTheDocument();
  });

  it('fills host path from folder picker selection', async () => {
    const user = userEvent.setup();
    vi.mocked(pickHostFolder).mockResolvedValue({ ok: true, path: 'D:\\repos\\GuideAnts' });

    render(<MapHostFolderDialog isOpen onClose={onClose} onSubmit={onSubmit} />);
    await user.click(screen.getByTestId('host-mount-browse-button'));

    expect(screen.getByTestId('host-mount-path-input')).toHaveValue('D:\\repos\\GuideAnts');
  });

  it('shows unavailable hint when folder picker is not supported', () => {
    vi.mocked(canPickHostFolder).mockReturnValue(false);

    render(<MapHostFolderDialog isOpen onClose={onClose} onSubmit={onSubmit} />);

    expect(screen.queryByTestId('host-mount-browse-button')).not.toBeInTheDocument();
    expect(screen.getByText(/Folder picker is not available/i)).toBeInTheDocument();
  });
});
