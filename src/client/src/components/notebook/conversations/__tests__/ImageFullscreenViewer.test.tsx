import React from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import '@testing-library/jest-dom';
import ImageFullscreenViewer from '../ImageFullscreenViewer';

describe('ImageFullscreenViewer', () => {
  const onClose = vi.fn();

  beforeEach(() => {
    onClose.mockClear();
    document.body.style.overflow = '';
  });

  afterEach(() => {
    document.body.style.overflow = '';
  });

  it('renders image in a modal dialog', () => {
    render(
      <ImageFullscreenViewer
        src="https://example.com/photo.png"
        alt="Test photo"
        onClose={onClose}
        fileName="photo.png"
      />
    );

    expect(screen.getByRole('dialog', { name: 'Image fullscreen viewer' })).toBeInTheDocument();
    expect(screen.getByRole('img', { name: 'Test photo' })).toHaveAttribute(
      'src',
      'https://example.com/photo.png'
    );
    expect(screen.getByText('Press ESC or click outside to close')).toBeInTheDocument();
  });

  it('calls onClose when close button is clicked', async () => {
    const user = userEvent.setup();
    render(
      <ImageFullscreenViewer src="https://example.com/photo.png" onClose={onClose} />
    );

    await user.click(screen.getByLabelText('Close viewer'));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('calls onClose when Escape is pressed', () => {
    render(
      <ImageFullscreenViewer src="https://example.com/photo.png" onClose={onClose} />
    );

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('calls onClose when backdrop is clicked', () => {
    render(
      <ImageFullscreenViewer src="https://example.com/photo.png" onClose={onClose} />
    );

    const dialog = screen.getByRole('dialog', { name: 'Image fullscreen viewer' });
    fireEvent.click(dialog);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('does not close when image container is clicked', async () => {
    const user = userEvent.setup();
    render(
      <ImageFullscreenViewer src="https://example.com/photo.png" alt="Photo" onClose={onClose} />
    );

    await user.click(screen.getByRole('img', { name: 'Photo' }));
    expect(onClose).not.toHaveBeenCalled();
  });

  it('triggers download with fileName', async () => {
    const user = userEvent.setup();
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    const appendSpy = vi.spyOn(document.body, 'appendChild');
    const removeSpy = vi.spyOn(document.body, 'removeChild');

    render(
      <ImageFullscreenViewer
        src="blob:https://example.com/photo"
        alt="Fallback name"
        fileName="saved.png"
        onClose={onClose}
      />
    );

    await user.click(screen.getByLabelText('Download image'));

    expect(clickSpy).toHaveBeenCalled();
    const link = appendSpy.mock.calls.find(
      (call) => call[0] instanceof HTMLAnchorElement
    )?.[0] as HTMLAnchorElement | undefined;
    expect(link?.download).toBe('saved.png');
    expect(link?.href).toContain('blob:https://example.com/photo');
    expect(removeSpy).toHaveBeenCalled();

    clickSpy.mockRestore();
    appendSpy.mockRestore();
    removeSpy.mockRestore();
  });

  it('locks body scroll while open and restores on unmount', () => {
    document.body.style.overflow = 'auto';

    const { unmount } = render(
      <ImageFullscreenViewer src="https://example.com/photo.png" onClose={onClose} />
    );

    expect(document.body.style.overflow).toBe('hidden');
    unmount();
    expect(document.body.style.overflow).toBe('auto');
  });
});
