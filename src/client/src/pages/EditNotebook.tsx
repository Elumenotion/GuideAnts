import { useState, useEffect } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { api } from '../services/api';
import { ProjectNotebook } from '../types/project';
import { useProject } from '../contexts/ProjectContext';
import LoadingSpinner from '../components/LoadingSpinner';
import ErrorScreen from '../components/ErrorScreen';

export default function EditNotebook() {
    const { projectId, notebookId } = useParams<{ projectId: string; notebookId: string }>();
    const { project, isLoading: projectLoading, error: projectError, refreshProject } = useProject();
    const [title, setTitle] = useState('');
    const [description, setDescription] = useState('');
    const [error, setError] = useState<string | null>(null);
    const [isLoading, setIsLoading] = useState(false);
    const navigate = useNavigate();

    useEffect(() => {
        if (project && notebookId) {
            const notebook = project.notebooks.find((n: ProjectNotebook) => n.id === notebookId);
            if (notebook) {
                setTitle(notebook.title);
                setDescription(notebook.description || '');
            } else {
                setError('Notebook not found');
            }
        }
    }, [project, notebookId]);

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        
        if (!projectId || !notebookId || !title.trim()) {
            setError('Notebook title is required');
            return;
        }

        try {
            setIsLoading(true);
            setError(null);
            
            await api.projects.updateNotebook(projectId, notebookId, {
                title: title.trim(),
                description: description.trim() || undefined,
            });

            // Refresh the project to show updated notebook title
            await refreshProject();

            // Navigate back to notebook details page
            navigate(`/projects/${projectId}/notebooks/${notebookId}`);
        } catch (err) {
            const errorMessage = err instanceof Error ? err.message : 'Failed to update notebook';
            setError(errorMessage);
            console.error('Update notebook error:', err);
        } finally {
            setIsLoading(false);
        }
    };

    if (projectLoading) {
        return <LoadingSpinner message="Loading notebook..." />;
    }

    // Show error screen if there's a critical error during initial load
    const hasCriticalError = projectError || (error && error.toLowerCase().includes('failed to fetch'));
    if (hasCriticalError) {
        const criticalError = projectError || error || 'Failed to load notebook';
        return (
            <ErrorScreen
                error={criticalError}
                title="Failed to Load Notebook"
                onRetry={() => window.location.reload()}
                onBack={() => navigate(`/projects/${projectId}/notebooks/${notebookId}`)}
            />
        );
    }

    return (
        <div className="min-h-screen bg-gray-50 p-8">
            <div className="max-w-2xl mx-auto">
                <div className="mb-8">
                    <h1 className="text-2xl font-bold text-gray-900">Edit Notebook</h1>
                    <p className="mt-2 text-sm text-gray-600">
                        Update your notebook details
                    </p>
                </div>

                <form onSubmit={handleSubmit} className="space-y-6 bg-white p-6 rounded-lg shadow">
                    <div>
                        <label htmlFor="title" className="block text-sm font-medium text-gray-700">
                            Notebook Title
                        </label>
                        <input
                            type="text"
                            id="title"
                            value={title}
                            onChange={(e) => setTitle(e.target.value)}
                            className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                            placeholder="Enter notebook title"
                        />
                    </div>

                    <div>
                        <label htmlFor="description" className="block text-sm font-medium text-gray-700">
                            Description <span className="text-gray-500 text-xs">(optional)</span>
                        </label>
                        <textarea
                            id="description"
                            value={description}
                            onChange={(e) => setDescription(e.target.value)}
                            className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 resize-none"
                            placeholder="Describe what this notebook is used for..."
                            rows={3}
                            maxLength={1000}
                        />
                        <div className="text-xs text-gray-500 text-right mt-1">
                            {description.length}/1000 characters
                        </div>
                    </div>



                    {error && (
                        <div className="rounded-md bg-red-50 p-4">
                            <div className="flex">
                                <div className="ml-3">
                                    <h3 className="text-sm font-medium text-red-800">Error</h3>
                                    <div className="mt-2 text-sm text-red-700">{error}</div>
                                </div>
                            </div>
                        </div>
                    )}

                    <div className="flex justify-end space-x-3">
                        <button
                            type="button"
                            onClick={() => navigate(`/projects/${projectId}/notebooks/${notebookId}`)}
                            className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-md shadow-sm hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500"
                        >
                            Cancel
                        </button>
                        <button
                            type="submit"
                            disabled={isLoading}
                            className={`px-4 py-2 text-sm font-medium text-white bg-blue-600 rounded-md shadow-sm hover:bg-blue-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500 ${
                                isLoading ? 'opacity-50 cursor-not-allowed' : ''
                            }`}
                        >
                            {isLoading ? 'Saving...' : 'Save Changes'}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
} 