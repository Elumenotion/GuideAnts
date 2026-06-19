import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { MapHostFolderDialog } from '../MapHostFolderDialog';

describe('MapHostFolderDialog', () => {
  const onClose = vi.fn();
  const onSubmit = vi.fn(async () => undefined);

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows host path input with helper text', () => {
    render(<MapHostFolderDialog isOpen onClose={onClose} onSubmit={onSubmit} />);

    expect(screen.getByTestId('host-mount-path-input')).toBeInTheDocument();
    expect(screen.getByText(/Enter the full absolute path on the Docker host/i)).toBeInTheDocument();
    expect(screen.queryByTestId('host-mount-browse-button')).not.toBeInTheDocument();
  });

  it('requires host path on submit', async () => {
    const user = userEvent.setup();

    render(<MapHostFolderDialog isOpen onClose={onClose} onSubmit={onSubmit} />);
    await user.click(screen.getByTestId('host-mount-create-submit'));

    expect(screen.getByTestId('host-mount-create-error')).toHaveTextContent('Host path is required.');
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('submits typed host path', async () => {
    const user = userEvent.setup();

    render(<MapHostFolderDialog isOpen onClose={onClose} onSubmit={onSubmit} />);
    await user.type(screen.getByTestId('host-mount-path-input'), 'D:\\repos\\GuideAnts');
    await user.click(screen.getByTestId('host-mount-create-submit'));

    expect(onSubmit).toHaveBeenCalledWith({
      hostPath: 'D:\\repos\\GuideAnts',
      scope: 'Notebook',
      leafName: '',
    });
  });
});
