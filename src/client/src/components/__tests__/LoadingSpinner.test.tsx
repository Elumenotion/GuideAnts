import React from 'react';
import { describe, it, expect } from 'vitest';
import { render, screen } from '../../test/test-utils';
import LoadingSpinner from '../LoadingSpinner';

describe('LoadingSpinner', () => {
  it('renders with default message', () => {
    render(<LoadingSpinner />);
    expect(screen.getByText('Loading your content...')).toBeInTheDocument();
    expect(screen.getByAltText('Loading...')).toBeInTheDocument();
  });

  it('renders with custom message', () => {
    const customMessage = 'Custom loading message';
    render(<LoadingSpinner message={customMessage} />);
    expect(screen.getByText(customMessage)).toBeInTheDocument();
  });

  it('has correct styling classes', () => {
    render(<LoadingSpinner />);
    const container = screen.getByText('Loading your content...').parentElement;
    expect(container).toHaveClass('flex', 'flex-col', 'items-center', 'justify-center');
    
    const image = screen.getByAltText('Loading...');
    expect(image).toHaveClass('w-16', 'h-16', 'animate-bounce');
  });
}); 