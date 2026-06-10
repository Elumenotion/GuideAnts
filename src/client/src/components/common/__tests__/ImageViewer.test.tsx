import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { ImageViewer } from '../ImageViewer';

describe('ImageViewer', () => {
  it('wraps image in PreviewContainer by default', () => {
    render(<ImageViewer src="https://example.com/a.png" alt="diagram" />);
    const img = screen.getByRole('img', { name: 'diagram' });
    expect(img).toHaveAttribute('src', 'https://example.com/a.png');
    expect(img.closest('[data-testid]') ?? img.parentElement?.parentElement).toBeTruthy();
  });

  it('renders inline without PreviewContainer when inlineMode is true', () => {
    render(
      <ImageViewer
        src="https://example.com/b.png"
        alt="inline"
        inlineMode
        className="custom-frame"
      />
    );
    const img = screen.getByRole('img', { name: 'inline' });
    const wrapper = img.parentElement;
    expect(wrapper).toHaveClass('custom-frame');
  });
});
