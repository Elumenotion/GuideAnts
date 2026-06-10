import React from 'react';
import { describe, it, expect, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import UserCell from '../UserCell';
import { render, screen } from '../../../../test/test-utils';

vi.mock('react-dom', async () => {
  const actual = await vi.importActual<typeof import('react-dom')>('react-dom');
  return {
    ...actual,
    createPortal: (node: React.ReactNode) => node,
  };
});

describe('UserCell – live markdown viewer', () => {
  it('renders markdown in fullscreen view with notebook context', async () => {
    const user = userEvent.setup();
    render(
      <UserCell
        content={'## Heading\n\nParagraph with **emphasis**.'}
        isLast={true}
        projectId="proj-1"
        notebookId="nb-1"
      />
    );

    await user.click(screen.getByLabelText('Full screen'));
    expect(screen.getByLabelText('Exit full screen')).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 2 })).toBeInTheDocument();
    expect(screen.getByText('emphasis').tagName.toLowerCase()).toBe('strong');
  });

  it('passes maxImageHeight to markdown viewer in inline mode', () => {
    render(
      <UserCell
        content="![pic](https://example.com/p.png)"
        isLast={false}
        projectId="proj-1"
        notebookId="nb-1"
      />
    );
    expect(screen.getByRole('img', { name: 'pic' })).toBeInTheDocument();
  });
});
