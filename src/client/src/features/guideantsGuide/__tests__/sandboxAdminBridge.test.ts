import { afterEach, describe, expect, it, vi } from 'vitest';
import { sandboxAdminApply, sandboxAdminApplyApt } from '../sandboxAdminBridge';

describe('sandboxAdminBridge apply targets', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('sandboxAdminApply sends scoped pip and installScripts targets', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 202,
      text: async () => '{"jobId":"abc"}',
      headers: new Headers({ 'content-type': 'application/json' }),
    });
    vi.stubGlobal('fetch', fetchMock);

    await sandboxAdminApply({
      projectId: '11111111-1111-1111-1111-111111111111',
      guideId: '22222222-2222-2222-2222-222222222222',
    });

    expect(fetchMock).toHaveBeenCalledOnce();
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(init.method).toBe('POST');
    expect(init.body).toBe(JSON.stringify({ targets: ['pip', 'installScripts'] }));
    expect(String(init.body)).not.toContain('apt');
  });

  it('sandboxAdminApplyApt sends global apt-only targets', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 202,
      text: async () => '{"jobId":"abc"}',
      headers: new Headers({ 'content-type': 'application/json' }),
    });
    vi.stubGlobal('fetch', fetchMock);

    await sandboxAdminApplyApt();

    expect(fetchMock).toHaveBeenCalledOnce();
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(init.method).toBe('POST');
    expect(init.body).toBe(JSON.stringify({ targets: ['apt'] }));
  });
});
