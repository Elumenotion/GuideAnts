import React from 'react';
import { describe, it, expect } from 'vitest';
import { render, screen } from '../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';

import PreviewContainer from '../PreviewContainer';


describe('PreviewContainer', () => {
  it('renders the provided children', () => {
    render(
      <PreviewContainer>
        <p>Child content</p>
      </PreviewContainer>
    );

    // Header text should be present
    expect(screen.getByText('Preview')).toBeInTheDocument();
    // Children should be rendered inside the container
    expect(screen.getByText('Child content')).toBeInTheDocument();
  });

  it('applies the custom contentClassName to the inner scroll area', () => {
    const customClass = 'custom-scroll-area';

    const { container } = render(
      <PreviewContainer contentClassName={customClass}>
        <p>Content</p>
      </PreviewContainer>
    );

    // The inner scroll container should have the custom class applied
    expect(container.querySelector(`.${customClass}`)).toBeInTheDocument();
  });

  it('toggles full-screen mode from the header button', async () => {
    const { container } = render(
      <PreviewContainer>
        <p>Content</p>
      </PreviewContainer>
    );

    await userEvent.click(screen.getByRole('button', { name: 'Full screen' }));
    expect(container.firstChild).toHaveClass('fixed');
    await userEvent.click(screen.getByRole('button', { name: 'Exit full screen' }));
    expect(container.firstChild).not.toHaveClass('fixed');
  });

  it('hides the header when hideHeader is true', () => {
    render(
      <PreviewContainer hideHeader>
        <p>Headerless</p>
      </PreviewContainer>
    );
    expect(screen.queryByText('Preview')).not.toBeInTheDocument();
    expect(screen.getByText('Headerless')).toBeInTheDocument();
  });

  it('renders header actions outside full-screen by default', () => {
    render(
      <PreviewContainer headerActions={<button type="button">Edit</button>}>
        <p>Content</p>
      </PreviewContainer>
    );

    expect(screen.getByRole('button', { name: 'Edit' })).toBeInTheDocument();
  });

  it('shows header actions only in full-screen when configured', async () => {
    render(
      <PreviewContainer headerActions={<button type="button">Edit</button>} showHeaderActionsOnlyWhenFull>
        <p>Content</p>
      </PreviewContainer>
    );

    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Full screen' }));
    expect(screen.getByRole('button', { name: 'Edit' })).toBeInTheDocument();
  });

}); 