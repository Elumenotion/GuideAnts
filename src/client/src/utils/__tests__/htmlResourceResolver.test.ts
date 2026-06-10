import { describe, it, expect, vi, beforeEach } from 'vitest';

const mockGetAuthenticatedUrl = vi.fn();

vi.mock('../../services/api', () => ({
  api: {
    utils: {
      getAuthenticatedUrl: (...args: unknown[]) => mockGetAuthenticatedUrl(...args),
    },
  },
}));

import { resolveHtmlResources, cleanupBlobUrls } from '../htmlResourceResolver';

describe('htmlResourceResolver', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockGetAuthenticatedUrl.mockImplementation(async () => ({
      objectUrl: 'blob:https://example.com/resource',
    }));
  });

  it('injects navigation script and skips absolute URLs', async () => {
    const html = '<html><body><img src="https://cdn.example.com/logo.png"></body></html>';

    const result = await resolveHtmlResources({
      html,
      projectId: 'proj-1',
      notebookId: 'nb-1',
      basePath: 'pages',
    });

    expect(mockGetAuthenticatedUrl).not.toHaveBeenCalled();
    expect(result.blobUrls.size).toBe(0);
    expect(result.discoveredRoot).toBeNull();
    expect(result.html).toContain('html-preview-navigate');
    expect(result.html).toContain('https://cdn.example.com/logo.png');
  });

  it('resolves notebook relative resources to blob URLs', async () => {
    const html = '<html><body><img src="images/logo.png"></body></html>';

    const result = await resolveHtmlResources({
      html,
      projectId: 'proj-1',
      notebookId: 'nb-1',
      basePath: 'site/dist',
    });

    expect(mockGetAuthenticatedUrl).toHaveBeenCalledWith(
      expect.stringContaining('/projects/proj-1/notebooks/nb-1/files/content?path=')
    );
    expect(result.blobUrls.has('blob:https://example.com/resource')).toBe(true);
    expect(result.html).toContain('blob:https://example.com/resource');
    expect(result.html).not.toContain('images/logo.png');
  });

  it('resolves project files via resolveProjectFilePath', async () => {
    const html = '<html><body><link href="styles/main.css" rel="stylesheet"></body></html>';

    const result = await resolveHtmlResources({
      html,
      projectId: 'proj-1',
      basePath: 'docs',
      resolveProjectFilePath: (path) => (path === 'docs/styles/main.css' ? 'file-99' : undefined),
    });

    expect(mockGetAuthenticatedUrl).toHaveBeenCalledWith(
      expect.stringContaining('/projects/proj-1/files/file-99/content')
    );
    expect(result.blobUrls.size).toBe(1);
    expect(result.html).toContain('blob:https://example.com/resource');
  });

  it('resolves root-relative paths using fileExists and discovered root', async () => {
    const html = '<html><body><img src="/assets/icon.png"></body></html>';

    const result = await resolveHtmlResources({
      html,
      projectId: 'proj-1',
      notebookId: 'nb-1',
      basePath: 'site/dist/pages',
      fileExists: (path) => path === 'site/dist/assets/icon.png',
    });

    expect(result.discoveredRoot).toBe('site/dist');
    expect(mockGetAuthenticatedUrl).toHaveBeenCalledWith(
      expect.stringContaining('path=site%2Fdist%2Fassets%2Ficon.png')
    );
    expect(result.blobUrls.size).toBe(1);
  });

  it('continues when resource fetch fails', async () => {
    mockGetAuthenticatedUrl.mockRejectedValueOnce(new Error('fetch failed'));
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});

    const html = '<html><body><img src="missing.png"></body></html>';
    const result = await resolveHtmlResources({
      html,
      projectId: 'proj-1',
      notebookId: 'nb-1',
      basePath: 'pages',
    });

    expect(result.blobUrls.size).toBe(0);
    expect(result.html).toContain('missing.png');
    warnSpy.mockRestore();
  });

  it('replaces css url() references and injects script without body tag', async () => {
    const html = '<html><style>.hero { background: url("images/bg.png"); }</style></html>';

    const result = await resolveHtmlResources({
      html,
      projectId: 'proj-1',
      notebookId: 'nb-1',
      basePath: 'site',
    });

    expect(mockGetAuthenticatedUrl).toHaveBeenCalled();
    expect(result.html).toContain('blob:https://example.com/resource');
    expect(result.html).toContain('html-preview-navigate');
  });

  it('revokes blob URLs on cleanup', () => {
    const revokeSpy = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
    const blobUrls = new Set(['blob:one', 'blob:two']);

    cleanupBlobUrls(blobUrls);

    expect(revokeSpy).toHaveBeenCalledTimes(2);
    revokeSpy.mockRestore();
  });

  it('ignores revoke failures during cleanup', () => {
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {
      throw new Error('already revoked');
    });

    expect(() => cleanupBlobUrls(new Set(['blob:bad']))).not.toThrow();
  });

  it('resolves media tags and relative paths with parent navigation', async () => {
    const html = [
      '<html><body>',
      '<video src="../videos/clip.mp4"></video>',
      '<audio src="audio/track.ogg"></audio>',
      '<source src="/sounds/beep.wav">',
      '<video poster="/posters/frame.png"></video>',
      '<object data="docs/guide.pdf"></object>',
      '<embed src="flash.swf">',
      '<iframe src="/pages/home"></iframe>',
      '</body></html>',
    ].join('');

    const result = await resolveHtmlResources({
      html,
      projectId: 'proj-1',
      notebookId: 'nb-1',
      basePath: 'site/dist/pages/about',
      fileExists: (path) =>
        path === 'site/dist/videos/clip.mp4'
        || path === 'site/dist/pages/about/audio/track.ogg'
        || path === 'site/dist/sounds/beep.wav'
        || path === 'site/dist/posters/frame.png'
        || path === 'site/dist/pages/about/docs/guide.pdf'
        || path === 'site/dist/pages/about/flash.swf'
        || path === 'site/dist/pages/home/index.html',
    });

    expect(mockGetAuthenticatedUrl).toHaveBeenCalled();
    expect(result.blobUrls.size).toBeGreaterThan(0);
    expect(result.discoveredRoot).toBe('site/dist');
  });

  it('reuses discovered root for subsequent root-relative paths', async () => {
    const html = '<html><body><img src="/a.png"><img src="/b.png"></body></html>';

    const result = await resolveHtmlResources({
      html,
      projectId: 'proj-1',
      notebookId: 'nb-1',
      basePath: 'site/dist',
      fileExists: (path) => path === 'site/dist/a.png' || path === 'site/dist/b.png',
    });

    expect(result.discoveredRoot).toBe('site/dist');
    expect(mockGetAuthenticatedUrl).toHaveBeenCalledTimes(2);
  });

  it('appends navigation script when html has no body or html closing tag', async () => {
    const result = await resolveHtmlResources({
      html: '<div>fragment</div>',
      projectId: 'proj-1',
      notebookId: 'nb-1',
      basePath: 'pages',
    });

    expect(result.html).toContain('html-preview-navigate');
    expect(result.html.endsWith('</script>')).toBe(true);
  });

  it('includes query parameters when resolving notebook files', async () => {
    const html = '<html><body><img src="images/logo.png?v=2"></body></html>';

    await resolveHtmlResources({
      html,
      projectId: 'proj-1',
      notebookId: 'nb-1',
      basePath: 'site',
    });

    expect(mockGetAuthenticatedUrl).toHaveBeenCalledWith(
      expect.stringContaining('path=site%2Fimages%2Flogo.png&v=2')
    );
  });

  it('skips project files when resolveProjectFilePath returns undefined', async () => {
    const html = '<html><body><img src="missing.png"></body></html>';

    const result = await resolveHtmlResources({
      html,
      projectId: 'proj-1',
      basePath: 'docs',
      resolveProjectFilePath: () => undefined,
    });

    expect(mockGetAuthenticatedUrl).not.toHaveBeenCalled();
    expect(result.blobUrls.size).toBe(0);
    expect(result.html).toContain('missing.png');
  });
});
