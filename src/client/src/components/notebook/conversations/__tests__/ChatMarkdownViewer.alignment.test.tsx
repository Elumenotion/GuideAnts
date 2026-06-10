import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import ChatMarkdownViewer from '../ChatMarkdownViewer';
import { api } from '../../../../services/api';

vi.mock('../../../common/MermaidRenderer', () => ({
  default: () => null,
}));

vi.mock('../ImageFullscreenViewer', () => ({
  default: () => null,
}));

vi.mock('../../../../services/api', () => ({
  api: {
    utils: {
      getAuthenticatedUrl: vi.fn(),
    },
  },
}));

const mockGetAuthenticatedUrl = vi.mocked(api.utils.getAuthenticatedUrl);

describe('ChatMarkdownViewer – alignment & cache', () => {
  beforeEach(() => {
    mockGetAuthenticatedUrl.mockReset();
    mockGetAuthenticatedUrl.mockResolvedValue({
      objectUrl: 'blob:cached-image',
      fileName: 'aligned.png',
    });
  });

  it('strips alignment pipe tokens from image URLs', async () => {
    const md = '![aligned](./chart.png|align=right)';
    render(
      <ChatMarkdownViewer text={md} projectId="proj-1" notebookId="nb-1" />
    );

    await waitFor(() => expect(mockGetAuthenticatedUrl).toHaveBeenCalled());
    const img = await screen.findByRole('img', { name: 'aligned' });
    expect(img.className).toContain('float-right');
  });

  it('reuses cached authenticated blob URLs for duplicate paths', async () => {
    const md = '![one](./a.png) ![two](./a.png)';
    render(
      <ChatMarkdownViewer text={md} projectId="proj-1" notebookId="nb-1" />
    );

    await waitFor(() => expect(mockGetAuthenticatedUrl).toHaveBeenCalled());
    const images = await screen.findAllByRole('img');
    expect(images[0]).toHaveAttribute('src', 'blob:cached-image');
    expect(images[1]).toHaveAttribute('src', 'blob:cached-image');
  });

  it('renders block headings h4 and h5', () => {
    const md = '#### Heading four\n\n##### Heading five';
    const { container } = render(<ChatMarkdownViewer text={md} />);
    expect(container.querySelector('h4')).toHaveTextContent('Heading four');
    expect(container.querySelector('h5')).toHaveTextContent('Heading five');
  });
});
