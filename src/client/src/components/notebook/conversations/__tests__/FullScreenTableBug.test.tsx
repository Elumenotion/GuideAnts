import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';
import FullScreenEditor from '../FullScreenEditor';

/**
 * Test-First Strategy: FullScreenEditor Table & Image Issues
 * 
 * These tests reproduce the specific bugs:
 * 1. Table formatting is off in full-screen view
 * 2. Images do not display in full-screen state
 */

describe('FullScreenEditor Table & Image Bug Reproduction', () => {
  const tableWithImage = `| Column 1 | Image Column | Column 3 |
|----------|--------------|----------|
| Text | ![Test Image](https://via.placeholder.com/150x100) | More Text |`;

  it('should display images in table cells in full-screen mode', async () => {
    const mockOnSave = vi.fn();
    const mockOnCancel = vi.fn();

    render(
      <FullScreenEditor
        content={tableWithImage}
        onSave={mockOnSave}
        onCancel={mockOnCancel}
        mode="edit"
      />
    );

    // Wait for the editor to load
    await waitFor(() => {
      expect(screen.getByText('Save')).toBeInTheDocument();
    });

    // Check if the image is rendered in the table
    const imageElement = screen.getByAltText('Test Image');
    expect(imageElement).toBeInTheDocument();
    expect(imageElement).toHaveAttribute('src', 'https://via.placeholder.com/150x100');
  });

  it('BUG REPRODUCTION: Image loading in different rendering contexts', async () => {
    console.log('\n=== IMAGE LOADING CONTEXT TEST ===');
    
    const simpleImageMarkdown = '![Test Image](https://via.placeholder.com/150x100/ff0000/ffffff?text=TEST)';
    
    // Test in fullscreen context specifically
    render(
      <FullScreenEditor
        content={simpleImageMarkdown}
        onSave={() => {}}
        onCancel={() => {}}
        mode="edit"
        placeholder="Image test"
      />
    );

    await waitFor(() => {
      const overlay = document.querySelector('.fixed.inset-0.z-50');
      expect(overlay).toBeInTheDocument();
    }, { timeout: 2000 });

    // Look for the image in the fullscreen context
    const images = screen.queryAllByRole('img');
    console.log('Images found in fullscreen:', images.length);
    
    if (images.length > 0) {
      const image = images[0];
      console.log('Image src:', image.getAttribute('src'));
      console.log('Image alt:', image.getAttribute('alt'));
      console.log('Image classes:', image.className);
      
      // Check if image is visible (not hidden by CSS)
      const computedStyle = window.getComputedStyle(image);
      console.log('Image display:', computedStyle.display);
      console.log('Image visibility:', computedStyle.visibility);
      console.log('Image opacity:', computedStyle.opacity);
    }

    // This test should reveal why images don't show in fullscreen
    expect(images.length).toBeGreaterThan(0);
  });

  it('BUG REPRODUCTION: CSS isolation issues in portal overlay', async () => {
    console.log('\n=== CSS ISOLATION TEST ===');
    
    render(
      <FullScreenEditor
        content={tableWithImage}
        onSave={() => {}}
        onCancel={() => {}}
        mode="edit"
        placeholder="CSS test"
      />
    );

    await waitFor(() => {
      const overlay = document.querySelector('.fixed.inset-0.z-50');
      expect(overlay).toBeInTheDocument();
    }, { timeout: 2000 });

    // Check if the overlay container affects table/image styling
    const overlay = document.querySelector('.fixed.inset-0.z-50') as HTMLElement;
    const table = screen.queryByRole('table');
    const images = screen.queryAllByRole('img');

    console.log('Overlay classes:', overlay?.className);
    
    if (table) {
      console.log('Table parent classes:', table.parentElement?.className);
      console.log('Table grandparent classes:', table.parentElement?.parentElement?.className);
    }

    if (images.length > 0) {
      const image = images[0];
      console.log('Image parent classes:', image.parentElement?.className);
      console.log('Image container tree:');
      
      let parent = image.parentElement;
      let level = 1;
      while (parent && level <= 5) {
        console.log(`  Level ${level}: ${parent.tagName} - ${parent.className}`);
        parent = parent.parentElement;
        level++;
      }
    }

    // Check if CSS is properly applied in the portal context
    expect(overlay).toBeInTheDocument();
  });

  it('BUG REPRODUCTION: Lexical editor configuration differences', async () => {
    console.log('\n=== EDITOR CONFIGURATION TEST ===');
    
    // Compare the actual LexicalEditor props used in different contexts
    const mockOnSave = vi.fn();
    const mockOnCancel = vi.fn();

    render(
      <FullScreenEditor
        content={tableWithImage}
        onSave={mockOnSave}
        onCancel={mockOnCancel}
        mode="edit"
        placeholder="Config test"
      />
    );

    await waitFor(() => {
      const overlay = document.querySelector('.fixed.inset-0.z-50');
      expect(overlay).toBeInTheDocument();
    });

    // Look for the actual LexicalEditor within the fullscreen component
    const editorContainer = document.querySelector('.lexical-editor');
    const contentEditable = document.querySelector('.lexical-content-editable');
    
    console.log('Editor container found:', !!editorContainer);
    console.log('Content editable found:', !!contentEditable);
    
    if (editorContainer) {
      console.log('Editor classes:', editorContainer.className);
    }
    
    if (contentEditable) {
      console.log('Content editable classes:', contentEditable.className);
      console.log('Content editable styles:', window.getComputedStyle(contentEditable).cssText);
    }

    // This should help identify configuration differences
    expect(editorContainer).toBeInTheDocument();
    expect(contentEditable).toBeInTheDocument();
  });

  it('DIAGNOSTIC: Full content inspection in fullscreen', async () => {
    console.log('\n=== FULL CONTENT DIAGNOSTIC ===');
    
    render(
      <FullScreenEditor
        content={tableWithImage}
        onSave={() => {}}
        onCancel={() => {}}
        mode="edit"
      />
    );

    await waitFor(() => {
      const overlay = document.querySelector('.fixed.inset-0.z-50');
      expect(overlay).toBeInTheDocument();
    }, { timeout: 2000 });

    // Get the full content of the overlay
    const overlay = document.querySelector('.fixed.inset-0.z-50');
    
    if (overlay) {
      console.log('FULL OVERLAY HTML:');
      console.log(overlay.outerHTML);
      
      // Look for any markdown text that wasn't converted
      const textContent = overlay.textContent || '';
      const hasUnconvertedMarkdown = textContent.includes('![Test Image]');
      
      console.log('\nCONTENT ANALYSIS:');
      console.log('Has unconverted image markdown:', hasUnconvertedMarkdown);
      console.log('Text content preview:', textContent.substring(0, 300));
      
      if (hasUnconvertedMarkdown) {
        console.log('ERROR: Markdown not converted to HTML elements');
      }
    }

    // This test documents what's actually rendered
    expect(overlay).toBeInTheDocument();
  });
}); 