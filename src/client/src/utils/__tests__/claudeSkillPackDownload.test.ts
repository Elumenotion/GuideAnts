import JSZip from 'jszip';
import { describe, expect, it } from 'vitest';
import {
  patchClaudeSkillPackEnv,
  patchEnvInZipBuffer,
  sanitizeClaudeSkillDownloadFileName,
} from '../claudeSkillPackDownload';

describe('claudeSkillPackDownload', () => {
  it('sanitizeClaudeSkillDownloadFileName removes unsafe characters', () => {
    expect(sanitizeClaudeSkillDownloadFileName('Architecture Guide!')).toBe('Architecture-Guide');
    expect(sanitizeClaudeSkillDownloadFileName('')).toBe('guide');
  });

  it('patchEnvInZipBuffer replaces placeholder API key in .env', async () => {
    const zip = new JSZip();
    zip.file(
      'architecture-guide/.env',
      'GUIDEANTS_API_BASE=https://app.example.com/api\nGUIDEANTS_PUB_ID=abc\nGUIDEANTS_API_KEY=gak_REPLACE_ME\n'
    );
    const inputBuffer = await zip.generateAsync({ type: 'arraybuffer' });

    const patchedBuffer = await patchEnvInZipBuffer(inputBuffer, 'gak_real_key_123');
    const outZip = await JSZip.loadAsync(patchedBuffer);
    const env = await outZip.file('architecture-guide/.env')!.async('string');

    expect(env).toContain('GUIDEANTS_API_KEY=gak_real_key_123');
    expect(env).not.toContain('gak_REPLACE_ME');
  });

  it('patchClaudeSkillPackEnv leaves zip unchanged when no api key provided', async () => {
    const zip = new JSZip();
    zip.file('skill/.env', 'GUIDEANTS_API_KEY=gak_REPLACE_ME\n');
    const inputBuffer = await zip.generateAsync({ type: 'arraybuffer' });

    const patched = await patchClaudeSkillPackEnv(inputBuffer, null);
    const outZip = await JSZip.loadAsync(inputBuffer);
    const env = await outZip.file('skill/.env')!.async('string');

    expect(env).toContain('gak_REPLACE_ME');
    expect(patched).toBeInstanceOf(Blob);
  });
});
