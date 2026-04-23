import React, { useState } from 'react';

interface AddLinkFormProps {
    onAdd: (url: string) => Promise<void>;
    onCancel: () => void;
}

function isValidUrl(urlString: string): boolean {
    try {
        const url = new URL(urlString);
        return url.protocol === 'http:' || url.protocol === 'https:';
    } catch {
        return false;
    }
}

export function AddLinkForm({ onAdd, onCancel }: AddLinkFormProps) {
    const [url, setUrl] = useState('');
    const [isAdding, setIsAdding] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        
        if (!url.trim()) {
            setError('Please enter a URL.');
            return;
        }

        if (!isValidUrl(url)) {
            setError('Please enter a valid URL starting with http:// or https://');
            return;
        }

        setError(null);
        setIsAdding(true);
        try {
            await onAdd(url.trim());
            onCancel();
        } catch (error) {
            console.error('Failed to add link:', error);
            setError('Failed to add link. Please try again.');
        } finally {
            setIsAdding(false);
        }
    };

    const handleUrlChange = (e: React.ChangeEvent<HTMLInputElement>) => {
        const newUrl = e.target.value;
        setUrl(newUrl);
        setError(null);
    };

    const openInBrowser = async () => {
        try {
            // @ts-ignore - Electron API is injected at runtime
            if (window.electron?.openExternal) {
                // @ts-ignore - Electron API is injected at runtime
                await window.electron.openExternal(url);
            } else {
                window.open(url, '_blank', 'noopener,noreferrer');
            }
        } catch (error) {
            console.error('Failed to open link:', error);
        }
    };

    return (
        <div className="h-full flex flex-col">
            <div className="border-b pb-4 mb-4">
                <div className="flex items-center justify-between mb-4">
                    <h2 className="text-xl font-semibold">Add New Link</h2>
                </div>

                {error && (
                    <div className="mb-4 p-4 bg-red-50 text-red-600 rounded">
                        {error}
                    </div>
                )}

                <form onSubmit={handleSubmit} className="space-y-4">
                    <div className="border rounded p-4">
                        <h3 className="font-medium mb-2">Link Details</h3>
                        <div className="space-y-4">
                            <div>
                                <label htmlFor="url" className="block text-sm font-medium text-gray-700 mb-1">
                                    URL
                                </label>
                                <input
                                    type="url"
                                    id="url"
                                    value={url}
                                    onChange={handleUrlChange}
                                    className="w-full px-3 py-2 border rounded focus:outline-none focus:ring-2 focus:ring-blue-500"
                                    placeholder="Enter URL (e.g., https://example.com)"
                                    disabled={isAdding}
                                    required
                                />
                            </div>
                        </div>
                    </div>

                    <div className="flex gap-2 justify-end">
                        <button
                            type="button"
                            onClick={onCancel}
                            disabled={isAdding}
                            className="px-4 py-2 text-sm border rounded hover:bg-gray-50 disabled:opacity-50"
                        >
                            Cancel
                        </button>
                        <button
                            type="submit"
                            disabled={isAdding || !url.trim()}
                            className="px-4 py-2 text-sm bg-blue-600 text-white rounded hover:bg-blue-700 disabled:opacity-50"
                        >
                            {isAdding ? 'Adding...' : 'Add Link'}
                        </button>
                    </div>
                </form>
            </div>

            <div className="flex-1 border rounded-lg overflow-hidden">
                <div className="bg-gray-100 px-4 py-2 border-b flex items-center justify-between">
                    <span className="text-sm font-medium">Open Link</span>
                    {url && isValidUrl(url) && (
                        <button
                            onClick={openInBrowser}
                            className="text-sm text-blue-600 hover:underline"
                        >
                            Open in new tab
                        </button>
                    )}
                </div>
                <div className="w-full h-full flex flex-col items-center justify-center p-6 text-center text-gray-500 space-y-3">
                    {isValidUrl(url) ? (
                        <p>Website previews are unavailable. Open the link in a browser to inspect it.</p>
                    ) : (
                        <p>{url ? 'Enter a valid URL starting with http:// or https://.' : 'Enter a valid URL to enable opening it in a new tab.'}</p>
                    )}
                </div>
            </div>
        </div>
    );
} 
