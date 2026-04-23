import { useParams } from 'react-router-dom';
import BaseEntityEditor from '../components/guides/editor/BaseEntityEditor';

/**
 * GuideEditor - Thin wrapper around BaseEntityEditor for editing guides.
 * 
 * A Guide is an Assistant with additional features:
 * - Home page markdown
 * - Crew members
 * - Export functionality
 */
export default function GuideEditor() {
  const { projectId, guideId } = useParams<{ projectId: string; guideId?: string }>();

  if (!projectId) {
    throw new Error('Project ID is required');
  }

  return <BaseEntityEditor entityType="guide" entityId={guideId} projectId={projectId} />;
}

