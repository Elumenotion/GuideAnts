import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AssistantSkillDto } from '../../../../../types/guides';
import {
  buildCreateAssistantFromSkillPayload,
  resolveSkillMarkdown,
} from '../createFromSkillHelpers';

vi.mock('../../../../../services/api', () => ({
  api: {
    guides: {
      assistants: {
        downloadFile: vi.fn(),
      },
    },
  },
}));

import { api } from '../../../../../services/api';

function encodePendingContent(text: string): string {
  return btoa(unescape(encodeURIComponent(text)));
}

const primarySkill: AssistantSkillDto = {
  name: 'demo-skill',
  description: 'Demo',
  requiresToolsets: ['terminal'],
  requiresTools: [],
  files: [
    { id: 'file-1', relativePath: 'skills/demo-skill/SKILL.md', contentType: 'text/markdown' },
    { id: 'file-2', relativePath: 'skills/demo-skill/helper.py', contentType: 'text/plain' },
  ],
};

describe('createFromSkillHelpers extended', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('resolves markdown from pending skill uploads before persisted files', async () => {
    const markdown = '# Demo Skill\n\nUse the terminal.';

    const resolved = await resolveSkillMarkdown(
      primarySkill,
      [
        {
          name: 'demo-skill',
          filesToAdd: [
            {
              folderKind: 'Skill',
              relativePath: 'skills/demo-skill/SKILL.md',
              contentBytes: encodePendingContent(markdown),
              contentType: 'text/markdown',
            },
          ],
        },
      ],
    );

    expect(resolved).toBe(markdown);
    expect(api.guides.assistants.downloadFile).not.toHaveBeenCalled();
  });

  it('downloads persisted manifest markdown when no pending upload exists', async () => {
    vi.mocked(api.guides.assistants.downloadFile).mockResolvedValue({
      text: async () => '# Saved skill',
    } as Blob);

    const resolved = await resolveSkillMarkdown(primarySkill, [], 'assistant-1');

    expect(resolved).toBe('# Saved skill');
    expect(api.guides.assistants.downloadFile).toHaveBeenCalledWith('assistant-1', 'file-1');
  });

  it('throws when a saved skill manifest is still pending', async () => {
    await expect(
      resolveSkillMarkdown(
        {
          ...primarySkill,
          files: [{ id: 'pending-1', relativePath: 'skills/demo-skill/SKILL.md', contentType: 'text/markdown' }],
        },
        [],
        'assistant-1',
      ),
    ).rejects.toThrow(/not saved yet/i);
  });

  it('builds create-assistant payload with instructions and sandbox placeholder', async () => {
    const markdown = '# Demo Skill\n\nBody';

    const payload = await buildCreateAssistantFromSkillPayload(
      'project-1',
      [primarySkill],
      [
        {
          name: 'demo-skill',
          filesToAdd: [
            {
              folderKind: 'Skill',
              relativePath: 'skills/demo-skill/SKILL.md',
              contentBytes: encodePendingContent(markdown),
              contentType: 'text/markdown',
            },
            {
              folderKind: 'Skill',
              relativePath: 'skills/demo-skill/helper.py',
              contentBytes: encodePendingContent('print("ok")'),
              contentType: 'text/plain',
            },
          ],
        },
      ],
      undefined,
      { primarySkillName: 'demo-skill', selectedSkillNames: ['demo-skill'] },
    );

    expect(payload.projectId).toBe('project-1');
    expect(payload.name).toBe('demo-skill');
    expect(payload.instructions).toBe(markdown);
    expect(payload.toolIds).toEqual(
      expect.arrayContaining([
        'b0000000-0000-0000-0000-000000000008',
        'b0000000-0000-0000-0000-000000000009',
      ]),
    );
    expect(payload.files).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ relativePath: 'skills/demo-skill/helper.py' }),
        expect.objectContaining({ folderKind: 'CodeInterpreter' }),
      ]),
    );
    expect(payload.contextOptions).toBeDefined();
  });

  it('throws when assistant id is required for saved skills', async () => {
    await expect(resolveSkillMarkdown(primarySkill, [], undefined)).rejects.toThrow(
      /save the guide before creating an assistant/i,
    );
  });

  it('downloads persisted payload files when building create-assistant payload', async () => {
    vi.mocked(api.guides.assistants.downloadFile).mockImplementation(async (_assistantId, fileId) => {
      if (fileId === 'file-1') {
        return { text: async () => '# Saved skill' } as Blob;
      }

      return {
        arrayBuffer: async () => new TextEncoder().encode('helper body').buffer,
      } as Blob;
    });

    const payload = await buildCreateAssistantFromSkillPayload(
      'project-1',
      [primarySkill],
      [],
      'assistant-1',
      { primarySkillName: 'demo-skill', selectedSkillNames: ['demo-skill'] },
    );

    expect(api.guides.assistants.downloadFile).toHaveBeenCalledWith('assistant-1', 'file-1');
    expect(api.guides.assistants.downloadFile).toHaveBeenCalledWith('assistant-1', 'file-2');
    expect(payload.files).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ relativePath: 'skills/demo-skill/helper.py' }),
      ]),
    );
  });

  it('throws when the primary skill cannot be found', async () => {
    await expect(
      buildCreateAssistantFromSkillPayload(
        'project-1',
        [],
        [],
        undefined,
        { primarySkillName: 'missing', selectedSkillNames: ['missing'] },
      ),
    ).rejects.toThrow(/primary skill not found/i);
  });
});
