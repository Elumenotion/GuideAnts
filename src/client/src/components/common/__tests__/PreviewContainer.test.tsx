import React from 'react';
import { describe, it, expect } from 'vitest';
import { render, screen } from '../../../test/test-utils';
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

}); 