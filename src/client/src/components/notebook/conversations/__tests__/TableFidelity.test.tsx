import React, { useRef, useEffect, useState } from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import '@testing-library/jest-dom';
import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';
import ChatMarkdownViewer from '../ChatMarkdownViewer';

/**
 * Test-First Strategy: Table Cell Content Fidelity
 * 
 * These tests demonstrate the current bug where rich content (images, formatting)
 * in table cells gets stripped during markdown round-trips, causing content loss
 * on reload/view toggle.
 */

async function roundTripTest(inputMarkdown: string): Promise<string> {
  return new Promise((resolve) => {
    let output = '';
    let completed = false;

    const TestComponent = () => {
      const editorRef = useRef<LexicalEditorRef>(null);
      const [step, setStep] = useState(0);

      useEffect(() => {
        if (!editorRef.current || completed) return;

        const runTest = async () => {
          if (step === 0) {
            // Step 1: Set the input markdown
            console.log(`[RoundTrip] Setting input: "${inputMarkdown}"`);
            editorRef.current!.setValue(inputMarkdown);
            setStep(1);
          } else if (step === 1) {
            // Step 2: Wait for Lexical to process, then get value back
            setTimeout(() => {
              output = editorRef.current!.getValue();
              console.log(`[RoundTrip] Got output: "${output}"`);
              completed = true;
              setStep(2);
            }, 50);
          }
        };

        runTest();
      }, [step]);

      return (
        <div>
          <div data-testid="step">{step}</div>
          <LexicalEditor
            ref={editorRef}
            placeholder="Table fidelity test"
            showToolbar={false}
            autoFocus={false}
          />
        </div>
      );
    };

    render(<TestComponent />);

    const checkCompletion = () => {
      if (completed) {
        resolve(output);
      } else {
        setTimeout(checkCompletion, 10);
      }
    };
    checkCompletion();
  });
}

describe('View Toggle Fidelity - REAL BUG INVESTIGATION', () => {
  it('CRITICAL: Compare LexicalEditor vs ChatMarkdownViewer table rendering', async () => {
    const tableWithImage = '| Cell 1 | ![Test Image](https://example.com/test.jpg) | Cell 3 |\n|--------|--------|\n| Text | More | Content |';
    
    console.log('\n=== COMPONENT COMPARISON TEST ===');
    console.log('Testing markdown:', tableWithImage);
    
    // Test 1: Render with ChatMarkdownViewer (display mode)
    const { rerender } = render(<ChatMarkdownViewer text={tableWithImage} />);
    
    // Look for image elements in the rendered output
    const renderedImages = screen.queryAllByRole('img');
    const hasRenderedImage = renderedImages.length > 0;
    
    console.log('ChatMarkdownViewer - Images found:', renderedImages.length);
    console.log('ChatMarkdownViewer - Image alt texts:', renderedImages.map(img => img.getAttribute('alt')));
    
    // Test 2: Check if the markdown round-trips correctly through LexicalEditor
    const roundTripped = await roundTripTest(tableWithImage);
    const imagePreservedInRoundTrip = roundTripped.includes('![Test Image](https://example.com/test.jpg)');
    
    console.log('LexicalEditor round-trip preserved image:', imagePreservedInRoundTrip);
    
    // Test 3: Re-render ChatMarkdownViewer with the round-tripped content
    rerender(<ChatMarkdownViewer text={roundTripped} />);
    
    const finalImages = screen.queryAllByRole('img');
    const finalHasImages = finalImages.length > 0;
    
    console.log('ChatMarkdownViewer (after round-trip) - Images found:', finalImages.length);
    
    console.log('\n=== ANALYSIS ===');
    console.log('Round-trip preserves image markdown:', imagePreservedInRoundTrip);
    console.log('ChatMarkdownViewer renders images:', hasRenderedImage);
    console.log('After round-trip, still renders:', finalHasImages);
    
    // The bug is likely here - ChatMarkdownViewer should render images in tables
    expect(hasRenderedImage).toBe(true);
    expect(finalHasImages).toBe(true);
  });

  it('DIAGNOSIS: Test react-markdown table cell image rendering', () => {
    // Test simpler cases to isolate the issue
    const simpleImage = '![Test](https://example.com/test.jpg)';
    const imageInParagraph = 'Text with ![Test](https://example.com/test.jpg) image';
    const imageInTable = '| ![Test](https://example.com/test.jpg) |\n|--------|';
    
    console.log('\n=== REACT-MARKDOWN COMPONENT ISOLATION ===');
    
    // Test 1: Simple image
    const { rerender } = render(<ChatMarkdownViewer text={simpleImage} />);
    let images = screen.queryAllByRole('img');
    console.log('Simple image - Images found:', images.length);
    
    // Test 2: Image in paragraph  
    rerender(<ChatMarkdownViewer text={imageInParagraph} />);
    images = screen.queryAllByRole('img');
    console.log('Image in paragraph - Images found:', images.length);
    
    // Test 3: Image in table cell
    rerender(<ChatMarkdownViewer text={imageInTable} />);
    images = screen.queryAllByRole('img');
    console.log('Image in table cell - Images found:', images.length);
    
    // Check DOM structure
    const tableElement = screen.queryByRole('table');
    if (tableElement) {
      console.log('Table HTML:', tableElement.innerHTML);
    }
    
    // This should help identify if react-markdown handles table cell images correctly
    expect(images.length).toBeGreaterThan(0);
  });

  it('DEEPER: Examine actual DOM output for table with images', () => {
    const complexTable = `| Header | Image Cell | Text |
|--------|-----------|------|
| Text | ![Alt](test.jpg) | More |`;

    render(<ChatMarkdownViewer text={complexTable} />);
    
    // Get the table element and examine its structure
    const table = screen.queryByRole('table');
    
    console.log('\n=== DOM STRUCTURE ANALYSIS ===');
    if (table) {
      console.log('Table HTML:');
      console.log(table.outerHTML);
      
      // Look for image elements
      const images = table.querySelectorAll('img');
      console.log('Images in table:', images.length);
      
      // Look for image markdown that wasn't rendered
      const textContent = table.textContent || '';
      const hasImageMarkdown = textContent.includes('![Alt](test.jpg)');
      console.log('Raw image markdown found in text:', hasImageMarkdown);
      
      if (hasImageMarkdown) {
        console.log('BUG CONFIRMED: Image markdown not converted to <img> elements');
      }
    } else {
      console.log('No table found in DOM');
    }
  });
});

describe('Table Cell Content Fidelity - DEEPER INVESTIGATION', () => {
  it('PRECISE TEST: Image node in table cell - examine exact behavior', async () => {
    // More comprehensive image in table test
    const inputWithImageInTable = '| Normal Cell | ![Test Image](https://example.com/test.png) | Another Cell |\n|-------------|-------------|-------------|\n| Row 2       | Text        | More Text   |';
    
    console.log('\n=== PRECISE IMAGE IN TABLE TEST ===');
    console.log('Input markdown:');
    console.log(inputWithImageInTable);
    
    const output = await roundTripTest(inputWithImageInTable);
    
    console.log('\nOutput markdown:');
    console.log(output);
    
    // Check if the image markdown is preserved exactly
    const inputImagePattern = /!\[Test Image\]\(https:\/\/example\.com\/test\.png\)/;
    const outputImagePattern = /!\[Test Image\]\(https:\/\/example\.com\/test\.png\)/;
    
    const imageInInput = inputImagePattern.test(inputWithImageInTable);
    const imageInOutput = outputImagePattern.test(output);
    
    console.log('Image in input:', imageInInput);
    console.log('Image in output:', imageInOutput);
    console.log('Image preserved:', imageInOutput);
    
    // Also check if only alt text remains (the actual bug)
    const altTextOnly = output.includes('Test Image') && !output.includes('![Test Image]');
    console.log('Only alt text remains (BUG):', altTextOnly);
    
    // This should fail if the bug exists
    expect(output).toContain('![Test Image](https://example.com/test.png)');
    expect(output).not.toContain('Test Image |'); // Should not be just alt text
  });

  it('MULTIPLE ROUND-TRIPS: Test degradation over multiple saves', async () => {
    let content = '| Cell 1 | ![Image](test.jpg) |\n|--------|--------|\n| Text   | **Bold**       |';
    
    console.log('\n=== MULTIPLE ROUND-TRIP TEST ===');
    console.log('Initial:', content);
    
    // Simulate multiple save/load cycles
    for (let i = 1; i <= 3; i++) {
      content = await roundTripTest(content);
      console.log(`Round ${i}:`, content);
      
      // Check if content is degrading
      const hasImage = content.includes('![Image](test.jpg)');
      const hasBold = content.includes('**Bold**');
      console.log(`  After round ${i}: Image=${hasImage}, Bold=${hasBold}`);
    }
    
    // After 3 rounds, formatting should still be intact
    expect(content).toContain('![Image](test.jpg)');
    expect(content).toContain('**Bold**');
  });

  it('EDGE CASE: Complex nested content in table cells', async () => {
    // Test more complex content that might break the transformer
    const complexContent = '| Header | Content |\n|--------|--------|\n| Cell 1 | **Bold** with ![img](url) and [link](href) |';
    
    const output = await roundTripTest(complexContent);
    
    console.log('\n=== COMPLEX NESTED CONTENT ===');
    console.log('Input: ', complexContent);
    console.log('Output:', output);
    
    expect(output).toContain('**Bold**');
    expect(output).toContain('![img](url)');
    expect(output).toContain('[link](href)');
  });

  it('CURRENT BUG: Images in table cells are lost on round-trip', async () => {
    const inputWithImage = '| Text Cell | ![Alt Text](https://example.com/image.png) |\n|-----------|------------|\n| More Text | Regular Text |';
    
    console.log('\n=== TESTING IMAGE IN TABLE CELL ===');
    console.log('Input:', inputWithImage);
    
    const output = await roundTripTest(inputWithImage);
    
    console.log('Output:', output);
    console.log('Image preserved?', output.includes('![Alt Text](https://example.com/image.png)'));
    console.log('Image lost?', output.includes('Alt Text') && !output.includes('!['));
    
    // This test SHOULD FAIL initially - demonstrating the bug
    expect(output).toContain('![Alt Text](https://example.com/image.png)');
    expect(output).not.toContain('Alt Text |'); // Should not be plain text
  });

  it('CURRENT BUG: Bold formatting in table cells is lost', async () => {
    const inputWithBold = '| **Bold Text** | Regular Text |\n|--------------|--------------|';
    
    console.log('\n=== TESTING BOLD IN TABLE CELL ===');
    console.log('Input:', inputWithBold);
    
    const output = await roundTripTest(inputWithBold);
    
    console.log('Output:', output);
    console.log('Bold preserved?', output.includes('**Bold Text**'));
    
    // This test SHOULD FAIL initially
    expect(output).toContain('**Bold Text**');
    expect(output).not.toContain('Bold Text |'); // Should not be plain text
  });

  it('CURRENT BUG: Links in table cells are lost', async () => {
    const inputWithLink = '| [Link Text](https://example.com) | Regular Text |\n|----------------------------------|--------------|';
    
    console.log('\n=== TESTING LINK IN TABLE CELL ===');
    console.log('Input:', inputWithLink);
    
    const output = await roundTripTest(inputWithLink);
    
    console.log('Output:', output);
    console.log('Link preserved?', output.includes('[Link Text](https://example.com)'));
    
    // This test SHOULD FAIL initially
    expect(output).toContain('[Link Text](https://example.com)');
    expect(output).not.toContain('Link Text |'); // Should not be plain text
  });

  it('CURRENT BUG: Mixed formatting in table cells is lost', async () => {
    const inputMixed = '| **Bold** and *italic* | `code` text | ![img](url) |\n|------------------------|-------------|-------------|\n| Normal | Text | Here |';
    
    console.log('\n=== TESTING MIXED FORMATTING IN TABLE CELL ===');
    console.log('Input:', inputMixed);
    
    const output = await roundTripTest(inputMixed);
    
    console.log('Output:', output);
    console.log('Bold preserved?', output.includes('**Bold**'));
    console.log('Italic preserved?', output.includes('*italic*'));
    console.log('Code preserved?', output.includes('`code`'));
    console.log('Image preserved?', output.includes('![img](url)'));
    
    // This test SHOULD FAIL initially - showing all formatting is lost
    expect(output).toContain('**Bold**');
    expect(output).toContain('*italic*');
    expect(output).toContain('`code`');
    expect(output).toContain('![img](url)');
  });

  it('BASELINE: Simple table without formatting should work', async () => {
    const simpleTable = '| Header 1 | Header 2 |\n|----------|----------|\n| Cell 1   | Cell 2   |';
    
    const output = await roundTripTest(simpleTable);
    
    // This should pass - basic tables work
    expect(output.trim()).toBe(simpleTable.trim());
  });
});

describe('Table Cell Analysis - Diagnostic Info', () => {
  it('shows what actually happens to complex table content', async () => {
    const complexTable = `| **Header 1** | *Header 2* | \`Header 3\` |
|--------------|------------|-------------|
| ![img](test.jpg) | [link](url) | **bold** text |
| Normal text | *italic* | \`code\` |`;

    console.log('\n' + '='.repeat(80));
    console.log('COMPLEX TABLE CONTENT ANALYSIS');
    console.log('='.repeat(80));
    
    const output = await roundTripTest(complexTable);
    
    console.log('\nINPUT:');
    console.log(complexTable);
    console.log('\nOUTPUT:');
    console.log(output);
    
    console.log('\nFORMATTING ANALYSIS:');
    const checkFormatting = (format: string, pattern: RegExp) => {
      const inputHas = pattern.test(complexTable);
      const outputHas = pattern.test(output);
      console.log(`${format}: Input=${inputHas}, Output=${outputHas}, Lost=${inputHas && !outputHas}`);
    };
    
    checkFormatting('Bold', /\*\*[^*]+\*\*/);
    checkFormatting('Italic', /\*[^*]+\*/);
    checkFormatting('Code', /`[^`]+`/);
    checkFormatting('Images', /!\[[^\]]*\]\([^)]+\)/);
    checkFormatting('Links', /\[[^\]]+\]\([^)]+\)/);
    
    console.log('\n' + '='.repeat(80));
    
    // This is a diagnostic test - we expect it to show losses
    // Don't fail it, just document what happens
  });
}); 