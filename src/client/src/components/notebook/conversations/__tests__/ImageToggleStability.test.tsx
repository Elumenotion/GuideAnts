import React from 'react';
import { render, screen, waitFor } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';
import DraftUserCell from '../DraftUserCell';

describe('Image Toggle Stability in Lexical Editor', () => {
  it('preserves images when toggling fullscreen mode', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const onSend = vi.fn();
    
    // Markdown with an image
    const initialMarkdown = `
Here is an image:

![Test Image](https://example.com/test.jpg)

Some text after the image.`;

    const { rerender } = render(
      <DraftUserCell
        value={initialMarkdown}
        onChange={onChange}
        onSend={onSend}
        userId="test-user"
        userName="Test User"
        userEmail="test@example.com"
      />
    );

    // Wait for initial render
    await waitFor(() => {
      const editorContent = screen.getByRole('textbox');
      expect(editorContent).toBeInTheDocument();
    });

    // Verify image is rendered initially
    const initialEditor = screen.getByRole('textbox');
    const initialImage = initialEditor.querySelector('img[alt="Test Image"]');
    expect(initialImage).toBeInTheDocument();
    expect(initialImage).toHaveAttribute('src', 'https://example.com/test.jpg');

    // Click fullscreen button
    const fullscreenButton = screen.getByLabelText('Full screen');
    await user.click(fullscreenButton);

    // Wait for fullscreen editor
    await waitFor(() => {
      expect(screen.getByLabelText('Exit full screen')).toBeInTheDocument();
    });

    // Exit fullscreen
    const exitButton = screen.getByLabelText('Exit full screen');
    await user.click(exitButton);

    // Wait for inline editor to re-render
    await waitFor(() => {
      const editorContent = screen.getByRole('textbox');
      
      // Check if image is rendered in the DOM (not as plain text)
      const imageElement = editorContent.querySelector('img[alt="Test Image"]');
      expect(imageElement).toBeInTheDocument();
      expect(imageElement).toHaveAttribute('src', 'https://example.com/test.jpg');
      
      // Make sure the image markdown syntax is NOT visible as plain text
      const editorText = editorContent.textContent || '';
      expect(editorText).not.toContain('![Test Image]');
      expect(editorText).not.toContain('(https://example.com/test.jpg)');
    });
  });

  it('preserves multiple images and mixed content when toggling', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const onSend = vi.fn();
    
    const complexMarkdown = `# Document with Images

First paragraph with **bold** text.

![First Image](https://example.com/image1.jpg)

Some text between images with a [regular link](https://example.com).

![Second Image](https://example.com/image2.png)

## Another heading

Final paragraph.`;

    render(
      <DraftUserCell
        value={complexMarkdown}
        onChange={onChange}
        onSend={onSend}
        userId="test-user"
        userName="Test User"
        userEmail="test@example.com"
      />
    );

    // Toggle to fullscreen
    const fullscreenButton = await screen.findByLabelText('Full screen');
    await user.click(fullscreenButton);

    // Wait for fullscreen
    await waitFor(() => {
      expect(screen.getByLabelText('Exit full screen')).toBeInTheDocument();
    });

    // Exit fullscreen
    const exitButton = screen.getByLabelText('Exit full screen');
    await user.click(exitButton);

    // Check that both images are still rendered properly
    await waitFor(() => {
      const editorContent = screen.getByRole('textbox');
      
      const firstImage = editorContent.querySelector('img[alt="First Image"]');
      expect(firstImage).toBeInTheDocument();
      expect(firstImage).toHaveAttribute('src', 'https://example.com/image1.jpg');
      
      const secondImage = editorContent.querySelector('img[alt="Second Image"]');
      expect(secondImage).toBeInTheDocument();
      expect(secondImage).toHaveAttribute('src', 'https://example.com/image2.png');
      
      // Verify no markdown syntax is visible
      const editorText = editorContent.textContent || '';
      expect(editorText).not.toContain('![First Image]');
      expect(editorText).not.toContain('![Second Image]');
    });
  });
}); 