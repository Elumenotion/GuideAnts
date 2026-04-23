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

function ProjectProviderWrapper({ children }: { children: React.ReactNode }) {
  const { projectId } = useParams<{ projectId: string }>();
  return (
    <ProjectProvider projectId={projectId}>
      {children}
    </ProjectProvider>
  );
}

const AppContent = () => {
  return (
    <Routes>
      <Route path="/terms" element={<Terms />} />
      <Route path="/privacy" element={<Privacy />} />
      <Route path="/public/:friendlyName" element={<PublicGuide />} />
      <Route path="/" element={<Home />} />
      <Route path="/projects" element={<Projects />} />
      <Route path="/conversations" element={<Conversations />} />
      <Route path="/usage" element={<Usage />} />
      <Route path="/settings" element={<Settings />} />
      <Route path="/new-project" element={<NewProject />} />
      <Route path="/projects/:projectId" element={
        <ProjectProviderWrapper>
          <ProjectDetails />
        </ProjectProviderWrapper>
      } />
      <Route path="/projects/:projectId/edit" element={
        <ProjectProviderWrapper>
          <EditProject />
        </ProjectProviderWrapper>
      } />
      <Route path="/projects/:projectId/notebooks/:notebookId" element={
        <ProjectProviderWrapper>
          <NotebookDetails />
        </ProjectProviderWrapper>
      } />
      <Route path="/projects/:projectId/notebooks/:notebookId/edit" element={
        <ProjectProviderWrapper>
          <EditNotebook />
        </ProjectProviderWrapper>
      } />
      <Route path="/projects/:projectId/notebooks/:notebookId/files/preview" element={
        <ProjectProviderWrapper>
          <FilePreviewPage />
        </ProjectProviderWrapper>
      } />
      <Route path="/projects/:projectId/guides" element={
        <ProjectProviderWrapper>
          <GuidesDashboard />
        </ProjectProviderWrapper>
      } />
      <Route path="/projects/:projectId/guides/guide/new" element={
        <ProjectProviderWrapper>
          <GuideEditor />
        </ProjectProviderWrapper>
      } />
      <Route path="/projects/:projectId/guides/guide/:guideId" element={
        <ProjectProviderWrapper>
          <GuideEditor />
        </ProjectProviderWrapper>
      } />
      <Route path="/projects/:projectId/guides/guide/:guideId/usage" element={
        <ProjectProviderWrapper>
          <GuideUsagePage />
        </ProjectProviderWrapper>
      } />
      <Route path="/projects/:projectId/guides/assistant/:assistantId/usage" element={
        <ProjectProviderWrapper>
          <GuideUsagePage />
        </ProjectProviderWrapper>
      } />
      <Route path="/projects/:projectId/guides/assistant/new" element={
        <ProjectProviderWrapper>
          <AssistantEditor />
        </ProjectProviderWrapper>
      } />
      <Route path="/projects/:projectId/guides/assistant/:assistantId" element={
        <ProjectProviderWrapper>
          <AssistantEditor />
        </ProjectProviderWrapper>
      } />
    </Routes>
  );
};

export default AppContent;
