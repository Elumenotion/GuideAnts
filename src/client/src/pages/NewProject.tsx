import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { HeaderActionsBar } from '../components/common/HeaderActionsBar';
import { GuideAntsGuideButton } from '../features/guideantsGuide/GuideAntsGuideButton';
import { HomeButton } from '../components/common/HomeButton';
import { SettingsButton } from '../components/common/SettingsButton';
import { api } from '../services/api';
import { TourStartButton } from '../tour/TourStartButton';
import { useRegisterTour } from '../tour/useRegisterTour';

export default function NewProject() {
    const [title, setTitle] = useState('');
    const [description, setDescription] = useState('');
    const [error, setError] = useState<string | null>(null);
    const [isLoading, setIsLoading] = useState(false);
    const navigate = useNavigate();

    useRegisterTour('new-project', [
        {
            target: '[data-tour-id="new-project.title"]',
            content: 'Enter a clear <strong>project title</strong> to organize your work.'
        },
        {
            target: '[data-tour-id="new-project.description"]',
            content: 'Optionally add a <strong>description</strong> to provide context.'
        },
        {
            target: '[data-tour-id="new-project.submit"]',
            content: 'Click <strong>Create Project</strong> to finish.'
        }
    ]);

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        
        if (!title.trim()) {
            setError('Project title is required');
            return;
        }

        try {
            setIsLoading(true);
            setError(null);
            
            const newProject = await api.projects.create({
                title: title.trim(),
                description: description.trim() || undefined,
            });

            // Navigate to the newly created project
            navigate(`/projects/${newProject.id}`);
        } catch (err) {
            const errorMessage = err instanceof Error ? err.message : 'Failed to create project';
            setError(errorMessage);
            console.error('Create project error:', err);
        } finally {
            setIsLoading(false);
        }
    };

    return (
        <div className="min-h-screen bg-gray-50 p-8">
            <div className="max-w-2xl mx-auto">
                <div className="mb-2">
                    <HeaderActionsBar>
                        <GuideAntsGuideButton />
                        <HomeButton />
                        <SettingsButton />
                        <TourStartButton screenId="new-project" inline />
                    </HeaderActionsBar>
                </div>
                <div className="mb-8">
                    <h1 className="text-2xl font-bold text-gray-900">Create New Project</h1>
                    <p className="mt-2 text-sm text-gray-600">
                        Create a new project to start organizing your work
                    </p>
                </div>

                <form onSubmit={handleSubmit} className="space-y-6 bg-white p-6 rounded-lg shadow">
                    <div>
                        <label htmlFor="title" className="block text-sm font-medium text-gray-700">
                            Project Title
                        </label>
                        <input
                            type="text"
                            id="title"
                            value={title}
                            onChange={(e) => setTitle(e.target.value)}
                            className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                            placeholder="Enter project title"
                            data-tour-id="new-project.title"
                        />
                    </div>

                    <div>
                        <label htmlFor="description" className="block text-sm font-medium text-gray-700">
                            Description (Optional)
                        </label>
                        <textarea
                            id="description"
                            value={description}
                            onChange={(e) => setDescription(e.target.value)}
                            rows={3}
                            className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                            placeholder="Enter project description"
                            data-tour-id="new-project.description"
                        />
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

                    <div className="flex justify-end space-x-4">
                        <button
                            type="button"
                            onClick={() => navigate('/')}
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
                            data-tour-id="new-project.submit"
                        >
                            {isLoading ? 'Creating...' : 'Create Project'}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
} 
