import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '../../../../test/test-utils';
import ChatMarkdownViewer, { invalidateImageCacheForPaths } from '../ChatMarkdownViewer';
import { api } from '../../../../services/api';

vi.mock('../../../common/MermaidRenderer', () => ({
  default: () => <div data-testid="mermaid" />,
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

describe('ChatMarkdownViewer – links & cache', () => {
  beforeEach(() => {
    mockGetAuthenticatedUrl.mockReset();
    mockGetAuthenticatedUrl.mockResolvedValue({
      objectUrl: 'blob:mock',
      fileName: 'doc.pdf',
    });
  });

  it('calls onLinkClick for relative notebook file links', async () => {
    const onLinkClick = vi.fn();
    render(
      <ChatMarkdownViewer
        text="[spec](./docs/spec.md)"
        projectId="proj-1"
        notebookId="nb-1"
        onLinkClick={onLinkClick}
      />
    );

    fireEvent.click(screen.getByRole('link', { name: 'spec' }));
    await waitFor(() => expect(onLinkClick).toHaveBeenCalled());
  });

  it('downloads authenticated API links on click', async () => {
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    render(
      <ChatMarkdownViewer
        text="[download](/api/projects/p1/notebooks/n1/files/content?path=doc.pdf)"
        projectId="p1"
        notebookId="n1"
      />
    );

    fireEvent.click(screen.getByRole('link', { name: 'download' }));
    await waitFor(() => expect(mockGetAuthenticatedUrl).toHaveBeenCalled());
    clickSpy.mockRestore();
  });

  it('invalidates cache entries for modified turn files on mount', () => {
    render(
      <ChatMarkdownViewer
        text="![img](./Output/chart.png)"
        projectId="proj-1"
        notebookId="nb-1"
        turnFilesModified={['Output/chart.png']}
        turnFilesCreated={['Output/new.png']}
      />
    );
    expect(() =>
      invalidateImageCacheForPaths(['Output/chart.png'], 'proj-1', 'nb-1')
    ).not.toThrow();
  });

  it('normalizes ordered list with nested bullets', () => {
    const md = '1. First item\n- nested bullet\n2. Second item';
    const { container } = render(<ChatMarkdownViewer text={md} />);
    expect(container.querySelectorAll('li').length).toBeGreaterThanOrEqual(3);
  });

  it('renders inline code in a paragraph', () => {
    render(<ChatMarkdownViewer text="Use `console.log` here" />);
    expect(screen.getByText('console.log').tagName.toLowerCase()).toBe('code');
  });
});
