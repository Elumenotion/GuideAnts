import React, { useRef, useEffect, useState } from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';
import FullScreenEditor from '../FullScreenEditor';
import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

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

  it('should preserve table formatting in full-screen mode', async () => {
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

    await waitFor(() => {
      expect(screen.getByText('Save')).toBeInTheDocument();
    });

    // Check that table structure is preserved
    const tableElement = document.querySelector('table');
    expect(tableElement).toBeInTheDocument();
    
    // Check for table headers
    const headerCells = document.querySelectorAll('th');
    expect(headerCells).toHaveLength(3);
    expect(headerCells[0]).toHaveTextContent('Column 1');
    expect(headerCells[1]).toHaveTextContent('Image Column');
    expect(headerCells[2]).toHaveTextContent('Column 3');
  });

  it('BUG REPRODUCTION: Table formatting differs between inline and fullscreen', async () => {
    console.log('\n=== TABLE FORMATTING COMPARISON ===');
    
    // Test 1: Render inline editor with table
    const InlineTest = () => {
      const editorRef = useRef<LexicalEditorRef>(null);
      const [initialized, setInitialized] = useState(false);

      useEffect(() => {
        if (editorRef.current && !initialized) {
          editorRef.current.setValue(tableWithImage);
          setInitialized(true);
        }
      }, [initialized]);

      return (
        <div data-testid="inline-container">
          <LexicalEditor
            ref={editorRef}
            placeholder="Inline test"
            showToolbar={false}
            className="inline-editor"
          />
        </div>
      );
    };

    const { rerender } = render(<InlineTest />);
    
    // Wait for inline editor to initialize
    await waitFor(() => {
      expect(screen.getByTestId('inline-container')).toBeInTheDocument();
    }, { timeout: 2000 });

    // Check inline table structure
    const inlineTable = screen.queryByRole('table');
    const inlineImages = screen.queryAllByRole('img');
    
    console.log('INLINE EDITOR:');
    console.log('- Table found:', !!inlineTable);
    console.log('- Images found:', inlineImages.length);
    if (inlineTable) {
      console.log('- Table classes:', inlineTable.className);
      console.log('- Table HTML preview:', inlineTable.outerHTML.substring(0, 200) + '...');
    }

    // Test 2: Render fullscreen editor with same content
    const FullScreenTest = () => (
      <FullScreenEditor
        content={tableWithImage}
        onSave={() => {}}
        onCancel={() => {}}
        mode="edit"
        placeholder="Fullscreen test"
      />
    );

    rerender(<FullScreenTest />);

    // Wait for fullscreen editor to initialize
    await waitFor(() => {
      const fullscreenOverlay = document.querySelector('.fixed.inset-0.z-50');
      expect(fullscreenOverlay).toBeInTheDocument();
    }, { timeout: 2000 });

    // Check fullscreen table structure
    const fullscreenTable = screen.queryByRole('table');
    const fullscreenImages = screen.queryAllByRole('img');

    console.log('\nFULLSCREEN EDITOR:');
    console.log('- Table found:', !!fullscreenTable);
    console.log('- Images found:', fullscreenImages.length);
    if (fullscreenTable) {
      console.log('- Table classes:', fullscreenTable.className);
      console.log('- Table HTML preview:', fullscreenTable.outerHTML.substring(0, 200) + '...');
    }

    console.log('\n=== COMPARISON RESULTS ===');
    console.log('Table rendering consistent:', !!inlineTable === !!fullscreenTable);
    console.log('Image count consistent:', inlineImages.length === fullscreenImages.length);

    // These assertions should identify the exact differences
    expect(fullscreenTable).toBeTruthy(); // Should find table in fullscreen
    expect(fullscreenImages.length).toBeGreaterThan(0); // Should find images in fullscreen
    
    if (inlineTable && fullscreenTable) {
      // Compare table styling
      expect(fullscreenTable.className).toBe(inlineTable.className);
    }
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