import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen } from '../../../test/test-utils';
import { ImageViewer } from '../ImageViewer';
import AudioPlayer from '../AudioPlayer';
import VideoPlayer from '../VideoPlayer';
import PdfViewer from '../PdfViewer';
import TextViewer from '../TextViewer';
import '@testing-library/jest-dom';

// Helpers
function createDummyBlob(type = 'application/octet-stream') {
  return new Blob(['dummy'], { type });
}

let originalCreateObjectURL: typeof URL.createObjectURL;
let originalRevokeObjectURL: typeof URL.revokeObjectURL;

beforeEach(() => {
  // Preserve originals to restore after each test file
  originalCreateObjectURL = URL.createObjectURL;
  originalRevokeObjectURL = URL.revokeObjectURL;

  global.URL.createObjectURL = vi.fn(() => 'blob:dummy');
  global.URL.revokeObjectURL = vi.fn();
});

afterEach(() => {
  global.URL.createObjectURL = originalCreateObjectURL;
  global.URL.revokeObjectURL = originalRevokeObjectURL;
});

describe('Media viewers', () => {
  it('TextViewer displays text and PreviewContainer', () => {
    render(<TextViewer text="Hello world" />);
    expect(screen.getByText('Hello world')).toBeInTheDocument();
    expect(screen.getByText('Preview')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Full screen' })).toBeInTheDocument();
  });

  it('ImageViewer renders img and PreviewContainer', () => {
    const blob = createDummyBlob('image/png');
    const objectUrl = URL.createObjectURL(blob);
    render(<ImageViewer src={objectUrl} alt="image.png" />);
    const img = screen.getByRole('img');
    expect(img).toHaveAttribute('src', 'blob:dummy');
    expect(screen.getByText('Preview')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Full screen' })).toBeInTheDocument();
  });

  it('PdfViewer renders embed and PreviewContainer', () => {
    const blob = createDummyBlob('application/pdf');
    const { container } = render(<PdfViewer blob={blob} />);
    const embed = container.querySelector('embed');
    expect(embed).toBeInTheDocument();
    expect(embed).toHaveAttribute('src', 'blob:dummy');
    expect(screen.getByText('Preview')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Full screen' })).toBeInTheDocument();
  });

  it('AudioPlayer renders audio tag and PreviewContainer', () => {
    const blob = createDummyBlob('audio/mpeg');
    const { container } = render(<AudioPlayer blob={blob} />);
    const audio = container.querySelector('audio');
    expect(audio).toBeInTheDocument();
    expect(audio).toHaveAttribute('src', 'blob:dummy');
    expect(screen.getByText('Preview')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Full screen' })).toBeInTheDocument();
  });

  it('VideoPlayer renders video tag and PreviewContainer', () => {
    const blob = createDummyBlob('video/mp4');
    const { container } = render(<VideoPlayer blob={blob} />);
    const video = container.querySelector('video');
    expect(video).toBeInTheDocument();
    expect(video).toHaveAttribute('src', 'blob:dummy');
    expect(screen.getByText('Preview')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Full screen' })).toBeInTheDocument();
  });
}); 