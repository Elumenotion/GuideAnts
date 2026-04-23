import React from 'react';
import { describe, it, expect } from 'vitest';
import { render, screen, fireEvent } from '../../../test/test-utils';
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

  it('toggles between normal and full-screen modes', () => {
    render(
      <PreviewContainer>
        <p>Toggle test</p>
      </PreviewContainer>
    );

    // Locate essential elements
    const toggleButton = screen.getByRole('button', { name: /full screen/i });
    const headerDiv = screen.getByText('Preview').closest('div') as HTMLElement;
    const outerContainer = headerDiv.parentElement as HTMLElement;

    // Initial (collapsed) state expectations
    expect(toggleButton).toHaveTextContent('Full screen');
    expect(outerContainer).toHaveClass('w-full flex-1 flex-col');
    expect(outerContainer).not.toHaveClass('fixed');

    // First click – expand to full-screen
    fireEvent.click(toggleButton);

    expect(toggleButton).toHaveTextContent('Collapse');
    expect(outerContainer).toHaveClass('fixed');
    expect(outerContainer).not.toHaveClass('w-full flex-1 flex-col');

    // Second click – collapse back to normal
    fireEvent.click(toggleButton);

    expect(toggleButton).toHaveTextContent('Full screen');
    expect(outerContainer).toHaveClass('w-full flex-1 flex-col');
    expect(outerContainer).not.toHaveClass('fixed');
  });
}); 