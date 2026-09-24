import { useParams } from 'react-router';
import BaseEntityEditor from '../components/guides/editor/BaseEntityEditor';

/**
 * AssistantEditor - Thin wrapper around BaseEntityEditor for editing assistants.
 * 
 * An Assistant is a specialized agent that can be:
 * - Used as a crew member in guides
 * - Configured with specific tools, files, and instructions
 * - Shared across multiple guides
 * 
 * Unlike Guides, Assistants do not have:
 * - Home page markdown
 * - Their own crew
 * - Export functionality
 */
export default function AssistantEditor() {
  const { projectId, assistantId } = useParams<{ projectId?: string; assistantId?: string }>();

  // Global (no-project) assistant editor: env form manages the assistant default
  // environment; save routes by ProjectId presence on the server.
  return <BaseEntityEditor entityType="assistant" entityId={assistantId} projectId={projectId} />;
}