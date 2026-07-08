import { describe, expect, it, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen, waitFor } from '../../../../test/test-utils';
import { SandboxWireApiPanel } from '../SandboxWireApiPanel';
import { api } from '../../../../services/api';

vi.mock('../../../../services/api', () => ({
  api: {
    projects: {
      notebookTemplates: {
        getAssistants: vi.fn(),
      },
    },
    guides: {
      catalogs: {
        globalAssistants: vi.fn(),
      },
    },
  },
}));

describe('SandboxWireApiPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('loads crew assistants for a guide and patches config when enabled', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const onDirtyChange = vi.fn();

    vi.mocked(api.projects.notebookTemplates.getAssistants).mockResolvedValue([
      { id: 'guide-1', name: 'Owner' },
      { id: 'crew-1', name: 'Researcher' },
    ]);

    render(
      <SandboxWireApiPanel
        projectId="project-1"
        guideId="guide-1"
        crewMemberIds={[]}
        config={{ enabled: false }}
        onChange={onChange}
        onDirtyChange={onDirtyChange}
      />,
    );

    await waitFor(() => {
      expect(api.projects.notebookTemplates.getAssistants).toHaveBeenCalledWith('guide-1', 'project-1');
    });

    await user.click(screen.getByRole('checkbox'));
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ enabled: true }));
    expect(onDirtyChange).toHaveBeenCalled();
  });

  it('uses global assistants filtered by crew ids when no guide id is provided', async () => {
    vi.mocked(api.guides.catalogs.globalAssistants).mockResolvedValue([
      { id: 'crew-1', name: 'Analyst' },
      { id: 'crew-2', name: 'Writer' },
    ]);

    render(
      <SandboxWireApiPanel
        projectId="project-1"
        crewMemberIds={['crew-2']}
        config={{ enabled: true, targetAssistantId: 'crew-2' }}
        onChange={vi.fn()}
      />,
    );

    await waitFor(() => {
      expect(screen.getByRole('option', { name: 'Writer' })).toBeInTheDocument();
    });
    expect(screen.queryByRole('option', { name: 'Analyst' })).not.toBeInTheDocument();
  });

  it('shows warning when no target assistants are available', async () => {
    vi.mocked(api.projects.notebookTemplates.getAssistants).mockResolvedValue([
      { id: 'guide-1', name: 'Owner only' },
    ]);

    render(
      <SandboxWireApiPanel
        projectId="project-1"
        guideId="guide-1"
        crewMemberIds={[]}
        config={{ enabled: true }}
        onChange={vi.fn()}
      />,
    );

    expect(
      await screen.findByText(/Add crew members to this guide/i),
    ).toBeInTheDocument();
  });
});
