import { describe, it, expect, vi, beforeEach } from 'vitest';

const mockBroadcastAuthExpired = vi.fn();

vi.mock('../authEvents', () => ({
  broadcastAuthExpired: (...args: unknown[]) => mockBroadcastAuthExpired(...args),
}));

vi.mock('../authService', () => ({
  withAuthFetchInit: (init: RequestInit) => ({ ...init, credentials: 'include' }),
  withAuthHeaders: () => new Headers(),
}));

import { transcribeAudio } from '../speechApi';

const mockFetch = vi.fn();

describe('speechApi', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // @ts-ignore
    global.fetch = mockFetch;
  });

  it('returns transcription result on success', async () => {
    const payload = { text: 'hello world', durationSeconds: 1.5 };
    mockFetch.mockResolvedValue({
      ok: true,
      status: 200,
      json: vi.fn().mockResolvedValue(payload),
    });

    const blob = new Blob(['audio'], { type: 'audio/webm' });
    const result = await transcribeAudio({ audioBlob: blob, language: 'en' });

    expect(result).toEqual(payload);
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining('/speech/transcribe'),
      expect.objectContaining({
        method: 'POST',
        credentials: 'include',
      })
    );

    const formData = mockFetch.mock.calls[0][1].body as FormData;
    expect(formData.get('language')).toBe('en');
    expect(formData.get('audio')).toBeInstanceOf(Blob);
  });

  it('broadcasts auth expired on 401', async () => {
    mockFetch.mockResolvedValue({
      ok: false,
      status: 401,
      json: vi.fn().mockResolvedValue({ error: 'Unauthorized' }),
    });

    const blob = new Blob(['audio'], { type: 'audio/webm' });
    await expect(transcribeAudio({ audioBlob: blob })).rejects.toThrow('Unauthorized');
    expect(mockBroadcastAuthExpired).toHaveBeenCalledWith('Authentication expired.');
  });

  it('throws error message from JSON response', async () => {
    mockFetch.mockResolvedValue({
      ok: false,
      status: 500,
      json: vi.fn().mockResolvedValue({ message: 'Server blew up' }),
    });

    const blob = new Blob(['audio'], { type: 'audio/ogg' });
    await expect(transcribeAudio({ audioBlob: blob })).rejects.toThrow('Server blew up');
  });

  it('uses default error message when JSON parsing fails', async () => {
    mockFetch.mockResolvedValue({
      ok: false,
      status: 502,
      json: vi.fn().mockRejectedValue(new Error('invalid json')),
    });

    const blob = new Blob(['audio'], { type: 'audio/wav' });
    await expect(transcribeAudio({ audioBlob: blob })).rejects.toThrow('Transcription failed');
  });

  it.each([
    ['audio/webm', 'webm'],
    ['audio/webm;codecs=opus', 'webm'],
    ['audio/ogg', 'ogg'],
    ['audio/wav', 'wav'],
    ['audio/mp3', 'mp3'],
    ['audio/mpeg', 'mp3'],
    ['audio/mp4', 'm4a'],
    ['audio/aac', 'aac'],
    ['audio/flac', 'flac'],
    ['audio/opus', 'opus'],
    ['audio/unknown', 'webm'],
    ['', 'webm'],
  ])('maps mime type %s to extension %s in uploaded filename', async (mimeType, expectedExt) => {
    mockFetch.mockResolvedValue({
      ok: true,
      status: 200,
      json: vi.fn().mockResolvedValue({ text: 'ok', durationSeconds: 0 }),
    });

    const blob = mimeType
      ? new Blob(['audio'], { type: mimeType })
      : new Blob(['audio']);

    await transcribeAudio({ audioBlob: blob });

    const formData = mockFetch.mock.calls[0][1].body as FormData;
    const audioEntry = formData.get('audio') as File;
    expect(audioEntry.name).toBe(`recording.${expectedExt}`);
  });
});
