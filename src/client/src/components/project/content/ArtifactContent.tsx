import { ProjectArtifact } from '../../../types/project';

interface ArtifactContentProps {
    artifact: ProjectArtifact;
    canEdit?: boolean;
}

export function ArtifactContent({ artifact, canEdit = false }: ArtifactContentProps) {
    return (
        <div>
            <h2 className="text-xl font-bold mb-4">Project Artifact</h2>
            <div className="space-y-4">
                <div className="border rounded p-4">
                    <h3 className="font-medium mb-2">Artifact Details</h3>
                    <div className="space-y-2">
                        <p className="text-gray-600">
                            {artifact.placeholder}
                        </p>
                    </div>
                </div>
                <div className="border rounded p-4">
                    <h3 className="font-medium mb-2">Metadata</h3>
                    <div className="space-y-2">
                        <p className="text-gray-600">
                            ID: {artifact.id}
                        </p>
                        {/* Add more metadata fields here */}
                    </div>
                </div>
                <div className="border rounded p-4">
                    <h3 className="font-medium mb-2">Actions</h3>
                    <div className="flex gap-2">
                        {canEdit && (
                            <button className="px-4 py-2 text-sm bg-blue-600 text-white rounded">
                                Edit Artifact
                            </button>
                        )}
                        <button className="px-4 py-2 text-sm border rounded">
                            View History
                        </button>
                    </div>
                </div>
            </div>
        </div>
    );
} 