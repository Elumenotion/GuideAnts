import { describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { PublishGuideDialog } from '../PublishGuideDialog';

vi.mock('../../../services/api', () => ({
  api: {
    guides: {
      guides: {
        validateFriendlyName: vi.fn().mockResolvedValue({ available: true }),
      },
    },
  },
}));

describe('PublishGuideDialog general flow', () => {
  it('walks publish tabs and submits a new published guide', async () => {
    const user = userEvent.setup();
    const onPublish = vi.fn();

    render(
      <PublishGuideDialog
        guideName="Demo Guide"
        guideId="guide-1"
        onPublish={onPublish}
        onCancel={vi.fn()}
      />,
    );

    expect(screen.getByText(/Publish "Demo Guide"/i)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Interface' }));
    await user.click(screen.getByRole('radio', { name: 'Last Turn Only' }));

    await user.click(screen.getByRole('button', { name: 'Features' }));
    await user.click(screen.getByRole('button', { name: 'Limits' }));
    await user.click(screen.getByRole('button', { name: 'Auth' }));

    await user.click(screen.getByRole('button', { name: 'Publish Guide' }));
    expect(onPublish).toHaveBeenCalledWith(
      expect.objectContaining({
        displayMode: 'last-turn',
      }),
    );
  });

  it('supports deactivate confirmation in edit mode', async () => {
    const user = userEvent.setup();
    const onDeactivate = vi.fn();

    render(
      <PublishGuideDialog
        guideName="Demo Guide"
        guideId="guide-1"
        publishedGuide={{
          id: 'pub-1',
          guideId: 'guide-1',
          guideName: 'Demo Guide',
          notebookId: 'notebook-1',
          projectId: 'project-1',
          created: '2026-01-01T00:00:00Z',
          active: true,
        }}
        onUpdate={vi.fn()}
        onDeactivate={onDeactivate}
        onCancel={vi.fn()}
      />,
    );

    await user.click(screen.getAllByRole('button', { name: 'Deactivate' })[0]);
    await user.click(screen.getAllByRole('button', { name: 'Deactivate' })[1]);
    expect(onDeactivate).toHaveBeenCalled();
  });
});
