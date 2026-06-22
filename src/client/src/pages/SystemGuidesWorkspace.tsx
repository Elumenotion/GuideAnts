import { useCallback, useEffect, useState } from 'react';
import { Navigate, useNavigate } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';
import { ProjectProvider } from '../contexts/ProjectContext';
import ProjectDetails from './ProjectDetails';
import LoadingSpinner from '../components/LoadingSpinner';
import ErrorScreen from '../components/ErrorScreen';
import { api } from '../services/api';

function getWorkspaceErrorMessage(error: unknown): string {
  if (error instanceof Error && error.message) {
    return error.message;
  }
  return 'Failed to load the system guides workspace.';
}

export default function SystemGuidesWorkspace() {
  const { role } = useAuth();
  const navigate = useNavigate();
  const [projectId, setProjectId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const loadWorkspace = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const workspace = await api.systemGuide.getWorkspace();
      setProjectId(workspace.projectId);
    } catch (err) {
      setProjectId(null);
      setError(getWorkspaceErrorMessage(err));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    if (role !== 'Admin') {
      return;
    }
    void loadWorkspace();
  }, [loadWorkspace, role]);

  if (role !== 'Admin') {
    return <Navigate to="/settings" replace />;
  }

  if (loading) {
    return <LoadingSpinner message="Loading system guides workspace..." />;
  }

  if (error || !projectId) {
    return (
      <ErrorScreen
        title="Unable to open system guides"
        message={error ?? 'The system guides workspace is unavailable.'}
        onRetry={() => void loadWorkspace()}
        onBack={() => navigate('/settings')}
      />
    );
  }

  return (
    <ProjectProvider projectId={projectId}>
      <ProjectDetails />
    </ProjectProvider>
  );
}
