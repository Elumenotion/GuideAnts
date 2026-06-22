import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { ProjectProvider, useProject } from '../ProjectContext';
import { api } from '../../services/api';
import { useAuth } from '../AuthContext';

vi.mock('../../services/api', () => ({
  api: {
    projects: {
      getProjectDetails: vi.fn(),
      renameContentFile: vi.fn(),
      folders: {
        getFolders: vi.fn(),
        getFolderTree: vi.fn(),
      },
    },
  },
}));

vi.mock('../AuthContext', () => ({
  useAuth: vi.fn(),
}));

const mockApi = api as unknown as {
  projects: {
    getProjectDetails: ReturnType<typeof vi.fn>;
    renameContentFile: ReturnType<typeof vi.fn>;
    folders: {
      getFolders: ReturnType<typeof vi.fn>;
      getFolderTree: ReturnType<typeof vi.fn>;
    };
  };
};

const mockedUseAuth = vi.mocked(useAuth);

const PROJECT_ID = 'project-1';

const projectFixture = {
  id: PROJECT_ID,
  name: 'GuideAnts Demo',
  description: 'A test project',
};

const folderTreeFixture = {
  id: 'root',
  name: 'root',
  children: [],
};

function wrapper({ children }: { children: React.ReactNode }) {
  return <ProjectProvider projectId={PROJECT_ID}>{children}</ProjectProvider>;
}

describe('ProjectContext', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockedUseAuth.mockReturnValue({
      user: {
        id: 'u1',
        name: 'Admin User',
        email: 'admin@example.com',
        role: 'Admin',
        mustChangePassword: false,
        lastLoginAt: null,
      },
      role: 'Admin',
      status: 'authenticated',
      isAuthenticated: true,
      login: vi.fn(),
      register: vi.fn(),
      changePassword: vi.fn(),
      refresh: vi.fn(),
      logout: vi.fn(),
    });
    mockApi.projects.getProjectDetails.mockResolvedValue(projectFixture);
    mockApi.projects.folders.getFolders.mockResolvedValue([]);
    mockApi.projects.folders.getFolderTree.mockResolvedValue(folderTreeFixture);
  });

  it('throws when useProject is used outside ProjectProvider', () => {
    expect(() => renderHook(() => useProject())).toThrow(
      'useProject must be used within a ProjectProvider',
    );
  });

  it('loads project details and folder tree on mount', async () => {
    const { result } = renderHook(() => useProject(), { wrapper });

    await waitFor(() => {
      expect(result.current.isLoading).toBe(false);
    });

    expect(mockApi.projects.getProjectDetails).toHaveBeenCalledWith(PROJECT_ID);
    expect(mockApi.projects.folders.getFolders).toHaveBeenCalledWith(PROJECT_ID);
    expect(mockApi.projects.folders.getFolderTree).toHaveBeenCalledWith(PROJECT_ID);
    expect(result.current.project?.name).toBe('GuideAnts Demo');
    expect(result.current.folderTree).toEqual(folderTreeFixture);
    expect(result.current.currentUserEmail).toBe('admin@example.com');
  });

  it('exposes role-based edit permissions', async () => {
    const { result } = renderHook(() => useProject(), { wrapper });

    await waitFor(() => {
      expect(result.current.isLoading).toBe(false);
    });

    expect(result.current.canEdit()).toBe(true);
    expect(result.current.isOwner()).toBe(true);
  });

  it('denies edit permissions for read-only roles', async () => {
    mockedUseAuth.mockReturnValue({
      user: {
        id: 'u2',
        name: 'Reader',
        email: 'reader@example.com',
        role: 'Reader',
        mustChangePassword: false,
        lastLoginAt: null,
      },
      role: 'Reader',
      status: 'authenticated',
      isAuthenticated: true,
      login: vi.fn(),
      register: vi.fn(),
      changePassword: vi.fn(),
      refresh: vi.fn(),
      logout: vi.fn(),
    });

    const { result } = renderHook(() => useProject(), { wrapper });

    await waitFor(() => {
      expect(result.current.isLoading).toBe(false);
    });

    expect(result.current.canEdit()).toBe(false);
    expect(result.current.isOwner()).toBe(false);
  });

  it('sets error when project load fails', async () => {
    mockApi.projects.getProjectDetails.mockRejectedValueOnce(new Error('Forbidden'));

    const { result } = renderHook(() => useProject(), { wrapper });

    await waitFor(() => {
      expect(result.current.error).toBe('Forbidden');
    });
    expect(result.current.isLoading).toBe(false);
  });

  it('blocks direct navigation to a hidden system project for contributors', async () => {
    mockedUseAuth.mockReturnValue({
      user: {
        id: 'u2',
        name: 'Contributor',
        email: 'contrib@example.com',
        role: 'Contributor',
        mustChangePassword: false,
        lastLoginAt: null,
      },
      role: 'Contributor',
      status: 'authenticated',
      isAuthenticated: true,
      login: vi.fn(),
      register: vi.fn(),
      changePassword: vi.fn(),
      refresh: vi.fn(),
      logout: vi.fn(),
    });
    mockApi.projects.getProjectDetails.mockRejectedValueOnce(new Error('Project not found'));

    const systemProjectWrapper = ({ children }: { children: React.ReactNode }) => (
      <ProjectProvider projectId="aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee">{children}</ProjectProvider>
    );
    const { result } = renderHook(() => useProject(), { wrapper: systemProjectWrapper });

    await waitFor(() => {
      expect(result.current.error).toBe('Project not found');
    });
    expect(result.current.project).toBeNull();
  });

  it('renames a content file through the API', async () => {
    mockApi.projects.renameContentFile.mockResolvedValueOnce(undefined);
    const { result } = renderHook(() => useProject(), { wrapper });

    await waitFor(() => {
      expect(result.current.project).not.toBeNull();
    });

    await act(async () => {
      await result.current.renameFile('file-1', 'renamed.md');
    });

    expect(mockApi.projects.renameContentFile).toHaveBeenCalledWith(
      PROJECT_ID,
      'file-1',
      'renamed.md',
    );
  });

  it('toggles expanded sections', async () => {
    const { result } = renderHook(() => useProject(), { wrapper });

    await waitFor(() => {
      expect(result.current.isLoading).toBe(false);
    });

    expect(result.current.expandedSections.has('notebooks')).toBe(true);

    act(() => {
      result.current.toggleSection('notebooks');
    });
    expect(result.current.expandedSections.has('notebooks')).toBe(false);

    act(() => {
      result.current.toggleSection('notebooks');
    });
    expect(result.current.expandedSections.has('notebooks')).toBe(true);
  });

  it('updates selected item and folder tree', async () => {
    const { result } = renderHook(() => useProject(), { wrapper });

    await waitFor(() => {
      expect(result.current.isLoading).toBe(false);
    });

    const selected = { type: 'notebook' as const, id: 'nb-1', name: 'Notebook' };

    act(() => {
      result.current.setSelectedItem(selected);
    });
    expect(result.current.selectedItem).toEqual(selected);

    const nextTree = { id: 'root', name: 'root', children: [{ id: 'child', name: 'child', children: [] }] };
    act(() => {
      result.current.setFolderTree(nextTree as any);
    });
    expect(result.current.folderTree).toEqual(nextTree);
  });

  it('refreshes project silently without toggling loading', async () => {
    const { result } = renderHook(() => useProject(), { wrapper });

    await waitFor(() => {
      expect(result.current.isLoading).toBe(false);
    });

    mockApi.projects.getProjectDetails.mockClear();

    await act(async () => {
      await result.current.refreshProject(true);
    });

    expect(mockApi.projects.getProjectDetails).toHaveBeenCalledWith(PROJECT_ID);
    expect(result.current.isLoading).toBe(false);
  });

  it('resets state when projectId is cleared', async () => {
    const { result, rerender } = renderHook(() => useProject(), {
      wrapper: ({ children }) => (
        <ProjectProvider projectId={undefined}>{children}</ProjectProvider>
      ),
    });

    await waitFor(() => {
      expect(result.current.project).toBeNull();
    });

    rerender();
    expect(mockApi.projects.getProjectDetails).not.toHaveBeenCalled();
  });

  it('surfaces rename failures', async () => {
    mockApi.projects.renameContentFile.mockRejectedValueOnce(new Error('Rename denied'));
    const { result } = renderHook(() => useProject(), { wrapper });

    await waitFor(() => {
      expect(result.current.project).not.toBeNull();
    });

    await expect(result.current.renameFile('file-1', 'bad.md')).rejects.toThrow('Rename denied');

    await waitFor(() => {
      expect(result.current.error).toBe('Rename denied');
    });
  });

  it('allows contributors to edit but not own the project', async () => {
    mockedUseAuth.mockReturnValue({
      user: {
        id: 'u3',
        name: 'Contributor',
        email: 'contrib@example.com',
        role: 'Contributor',
        mustChangePassword: false,
        lastLoginAt: null,
      },
      role: 'Contributor',
      status: 'authenticated',
      isAuthenticated: true,
      login: vi.fn(),
      register: vi.fn(),
      changePassword: vi.fn(),
      refresh: vi.fn(),
      logout: vi.fn(),
    });

    const { result } = renderHook(() => useProject(), { wrapper });

    await waitFor(() => {
      expect(result.current.isLoading).toBe(false);
    });

    expect(result.current.canEdit()).toBe(true);
    expect(result.current.isOwner()).toBe(false);
  });
});
