import React from 'react';
import { describe, it, expect, vi } from 'vitest';
import { render, waitFor } from '../../../test/test-utils';
import MermaidRenderer from '../MermaidRenderer';
import '@testing-library/jest-dom';

// Mock the mermaid module used in dynamic import within the component
vi.mock('mermaid', () => ({
  default: {
    initialize: vi.fn(),
    render: vi.fn().mockResolvedValue({ svg: '<svg id="diagram"></svg>' }),
    parse: vi.fn().mockResolvedValue(true),
  },
}));

// Mock the utility functions
vi.mock('../../../utils/mermaidCleanup', () => ({
  cleanupOrphanedMermaidElements: vi.fn(),
  validateMermaidSyntax: vi.fn().mockReturnValue({ isValid: true }),
}));

describe('MermaidRenderer', () => {
  it('renders SVG produced by mermaid.render', async () => {
    const { container } = render(<MermaidRenderer chart={'graph TD; A-->B'} />);

    await waitFor(() => {
      const svg = container.querySelector('svg');
      expect(svg).toBeInTheDocument();
      expect(svg?.id).toBe('diagram');
    });
  });

  it('displays error message when chart syntax is invalid', async () => {
    const { validateMermaidSyntax } = await import('../../../utils/mermaidCleanup');
    vi.mocked(validateMermaidSyntax).mockReturnValue({ 
      isValid: false, 
      error: 'Invalid diagram type' 
    });

    const { container } = render(<MermaidRenderer chart={'invalid chart'} />);

    await waitFor(() => {
      expect(container.textContent).toContain('Mermaid Diagram Error');
      expect(container.textContent).toContain('Invalid diagram type');
    });
  });

}); 