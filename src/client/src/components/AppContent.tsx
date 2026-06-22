import { Routes, Route } from 'react-router-dom';
import NewProject from '../pages/NewProject';
import EditProject from '../pages/EditProject';
import EditNotebook from '../pages/EditNotebook';
import ProjectDetails from '../pages/ProjectDetails';
import NotebookDetails from '../pages/NotebookDetails';
import Projects from '../pages/Projects';
import Conversations from '../pages/Conversations';
import { ProjectProvider } from '../contexts/ProjectContext';
import { useParams } from 'react-router-dom';
import Home from '../pages/Home';
import Usage from '../pages/Usage';
import Terms from '@/pages/Terms';
import Privacy from '@/pages/Privacy';
import GuidesDashboard from '../pages/GuidesDashboard';
import GuideEditor from '../pages/GuideEditor';
import AssistantEditor from '../pages/AssistantEditor';
import PublicGuide from '../pages/PublicGuide';
import GuideUsagePage from '../pages/GuideUsagePage';
import FilePreviewPage from '../pages/FilePreviewPage';
import Settings from '../pages/Settings';
import SystemGuidesWorkspace from '../pages/SystemGuidesWorkspace';
import Login from '../pages/Login';
import Register from '../pages/Register';
import Pending from '../pages/Pending';
import ChangePassword from '../pages/ChangePassword';
import OAuthCallback from '../pages/OAuthCallback';
import { ProtectedRoute } from './ProtectedRoute';

function ProjectProviderWrapper({ children }: { children: React.ReactNode }) {
  const { projectId } = useParams<{ projectId: string }>();
  return (
    <ProjectProvider projectId={projectId}>
      {children}
    </ProjectProvider>
  );
}

const AppContent = () => {
  const withProjectProvider = (component: React.ReactNode) => (
    <ProjectProviderWrapper>
      {component}
    </ProjectProviderWrapper>
  );

  const withProtection = (component: React.ReactNode) => (
    <ProtectedRoute>
      {component}
    </ProtectedRoute>
  );
  const withAdminProtection = (component: React.ReactNode, adminFallbackPath = '/') => (
    <ProtectedRoute requireAdmin adminFallbackPath={adminFallbackPath}>
      {component}
    </ProtectedRoute>
  );
  const withEditorProtection = (component: React.ReactNode) => (
    <ProtectedRoute requireEditor>
      {component}
    </ProtectedRoute>
  );

  return (
    <Routes>
      <Route path="/login" element={<Login />} />
      <Route path="/register" element={<Register />} />
      <Route path="/oauth/callback" element={<OAuthCallback />} />
      <Route path="/redirect" element={<OAuthCallback />} />
      <Route path="/terms" element={<Terms />} />
      <Route path="/privacy" element={<Privacy />} />
      <Route path="/public/:friendlyName" element={<PublicGuide />} />
      <Route path="/pending" element={withProtection(<Pending />)} />
      <Route path="/change-password" element={withProtection(<ChangePassword />)} />

      <Route path="/" element={withProtection(<Home />)} />
      <Route path="/projects" element={withProtection(<Projects />)} />
      <Route path="/conversations" element={withProtection(<Conversations />)} />
      <Route path="/usage" element={withProtection(<Usage />)} />
      <Route path="/settings" element={withProtection(<Settings />)} />
      <Route
        path="/settings/system-guides"
        element={withAdminProtection(<SystemGuidesWorkspace />, '/settings')}
      />
      <Route path="/new-project" element={withEditorProtection(<NewProject />)} />
      <Route path="/projects/:projectId" element={withProtection(withProjectProvider(<ProjectDetails />))} />
      <Route path="/projects/:projectId/edit" element={withEditorProtection(withProjectProvider(<EditProject />))} />
      <Route path="/projects/:projectId/notebooks/:notebookId" element={withProtection(withProjectProvider(<NotebookDetails />))} />
      <Route path="/projects/:projectId/notebooks/:notebookId/edit" element={withEditorProtection(withProjectProvider(<EditNotebook />))} />
      <Route path="/projects/:projectId/notebooks/:notebookId/files/preview" element={withProtection(withProjectProvider(<FilePreviewPage />))} />
      <Route path="/projects/:projectId/guides" element={withAdminProtection(withProjectProvider(<GuidesDashboard />))} />
      <Route path="/projects/:projectId/guides/guide/new" element={withAdminProtection(withProjectProvider(<GuideEditor />))} />
      <Route path="/projects/:projectId/guides/guide/:guideId" element={withAdminProtection(withProjectProvider(<GuideEditor />))} />
      <Route path="/projects/:projectId/guides/guide/:guideId/usage" element={withAdminProtection(withProjectProvider(<GuideUsagePage />))} />
      <Route path="/projects/:projectId/guides/assistant/:assistantId/usage" element={withAdminProtection(withProjectProvider(<GuideUsagePage />))} />
      <Route path="/projects/:projectId/guides/assistant/new" element={withAdminProtection(withProjectProvider(<AssistantEditor />))} />
      <Route path="/projects/:projectId/guides/assistant/:assistantId" element={withAdminProtection(withProjectProvider(<AssistantEditor />))} />
    </Routes>
  );
};

export default AppContent;
