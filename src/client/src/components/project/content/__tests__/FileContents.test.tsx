import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '../../../../test/test-utils';
import { FileContents } from '../FileContents';
import '@testing-library/jest-dom';

// USE THE SHARED MOCKING PATTERN
vi.mock('../../../../services/api', () => {
    const mocks = {
      // NOTE: This will also mock functions needed by other tests if they run in the same worker
      getContentFile: vi.fn(),
      updateContentFile: vi.fn(),
      deleteContentFile: vi.fn(),
      // This is the one we care about for this test file
      getContentFileContent: vi.fn(),
    };
    (globalThis as any).__apiMocks = mocks;
    return {
      api: {
        projects: mocks,
      },
    };
});
  
// Helper to access the shared mock
const apiMocks = () => (globalThis as any).__apiMocks as {
    getContentFile: ReturnType<typeof vi.fn>;
    updateContentFile: ReturnType<typeof vi.fn>;
    deleteContentFile: ReturnType<typeof vi.fn>;
    getContentFileContent: ReturnType<typeof vi.fn>;
};
const getContentFileContent = () => apiMocks().getContentFileContent;


global.URL.createObjectURL = vi.fn(() => 'blob:http://localhost/mock-url');
global.URL.revokeObjectURL = vi.fn();

describe('FileContents', () => {
    beforeEach(() => {
        // Use clearAllMocks to reset call counts, but the mock implementation
        // will be set explicitly in each test.
        vi.clearAllMocks();
    });

    it('shows loading spinner initially', () => {
        // Mock doesn't need to resolve for this test
        render(<FileContents projectId="p1" fileId="f1" contentType="text/plain" />);
        expect(screen.getByText('Loading file content...')).toBeInTheDocument();
    });

    it('shows error message when fetching content fails', async () => {
        const consoleErrorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
        getContentFileContent().mockRejectedValueOnce(new Error('Fetch failed'));
        
        render(<FileContents projectId="p1" fileId="f1" contentType="text/plain" />);

        await waitFor(() => {
            expect(screen.getByText('Failed to load file content. Please try again.')).toBeInTheDocument();
        });

        expect(consoleErrorSpy).toHaveBeenCalledWith('Failed to fetch file content:', expect.any(Error));
        consoleErrorSpy.mockRestore();
    });

    it('shows error message when reading text content fails', async () => {
        const consoleErrorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
        
        const mockBlob = {
            text: vi.fn().mockRejectedValueOnce(new Error('Read failed')),
            type: 'text/plain',
            size: 12,
        };

        getContentFileContent().mockResolvedValueOnce({ 
            blob: mockBlob as unknown as Blob,
            contentType: 'text/plain', 
            fileName: 'test.txt' 
        });

        render(<FileContents projectId="p1" fileId="f1" contentType="text/plain" />);

        await waitFor(() => {
            expect(screen.getByText('Failed to read text content. Please try again.')).toBeInTheDocument();
        });
        expect(consoleErrorSpy).toHaveBeenCalledWith('Failed to read text content:', expect.any(Error));
        consoleErrorSpy.mockRestore();
    });

    it('renders text content for text/plain', async () => {
        // Create a mock blob-like object that has a text() method
        const mockBlob = {
            text: async () => 'hello world',
            type: 'text/plain',
            size: 12,
        };
        getContentFileContent().mockResolvedValueOnce({ 
            blob: mockBlob as unknown as Blob, 
            contentType: 'text/plain', 
            fileName: 'test.txt' 
        });
        
        render(<FileContents projectId="p1" fileId="f1" contentType="text/plain" />);

        expect(await screen.findByText('hello world')).toBeInTheDocument();
    });

    it('renders image content for image/png', async () => {
        const blob = new Blob([''], { type: 'image/png' });
        getContentFileContent().mockResolvedValueOnce({ blob, contentType: 'image/png', fileName: 'test.png' });

        render(<FileContents projectId="p1" fileId="f1" contentType="image/png" />);

        // Wait for the final image by its alt text, not just any image role
        const img = await screen.findByAltText('test.png');
        expect(img).toBeInTheDocument();
        expect(img.getAttribute('src')).toMatch(/^blob:/);
    });

    it('renders PDF content for application/pdf', async () => {
        const blob = new Blob([''], { type: 'application/pdf' });
        getContentFileContent().mockResolvedValueOnce({ blob, contentType: 'application/pdf', fileName: 'test.pdf' });

        render(<FileContents projectId="p1" fileId="f1" contentType="application/pdf" />);

        const embed = await screen.findByTitle('PDF preview');
        expect(embed).toHaveAttribute('type', 'application/pdf');
        // Use a less strict check that is more reliable
        expect(embed.getAttribute('src')).toContain('blob:');
    });

    it('shows fallback for unsupported content type', async () => {
        const blob = new Blob([], { type: 'application/octet-stream' });
        getContentFileContent().mockResolvedValueOnce({ blob, contentType: 'application/octet-stream', fileName: 'test.bin' });

        render(<FileContents projectId="p1" fileId="f1" contentType="application/octet-stream" />);

        await waitFor(() => {
            expect(screen.getByText('Preview not available for this file type (application/octet-stream)')).toBeInTheDocument();
        });
    });
});
