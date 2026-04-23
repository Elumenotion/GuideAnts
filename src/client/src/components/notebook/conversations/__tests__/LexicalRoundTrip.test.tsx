import React, { useRef, useEffect, useState } from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import '@testing-library/jest-dom';
import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

describe('Lexical Markdown Round-Trip Tests', () => {
  /**
   * Helper function to test that markdown input produces the expected markdown output
   */
  const testMarkdownPreservation = async (inputMarkdown: string, expectedMarkdown: string, testName: string) => {
    let actualOutput = '';
    let testCompleted = false;

    const TestComponent = () => {
      const editorRef = useRef<LexicalEditorRef>(null);
      const [step, setStep] = useState(0);

      useEffect(() => {
        if (!editorRef.current) return;

        const runTest = async () => {
          if (step === 0) {
            // Set the input markdown
            editorRef.current!.setValue(inputMarkdown);
            setTimeout(() => setStep(1), 50);
          } else if (step === 1) {
            // Get the output markdown
            actualOutput = editorRef.current!.getValue();
            testCompleted = true;
          }
        };

        runTest();
      }, [step]);

      return (
        <LexicalEditor
          ref={editorRef}
          placeholder="Test editor"
          showToolbar={false}
          autoFocus={false}
        />
      );
    };

    render(<TestComponent />);

    await waitFor(() => {
      expect(testCompleted).toBe(true);
    }, { timeout: 1000 });

    // This is the real test - does the output match what we expect?
    expect(actualOutput.trim()).toBe(expectedMarkdown.trim());

    return actualOutput;
  };

     /**
    * Helper for cases where we expect the input to be preserved exactly
    */
   const testExactPreservation = async (markdown: string, testName?: string) => {
     return testMarkdownPreservation(markdown, markdown, testName || 'preservation test');
   };

  describe('Text Formatting - Exact Preservation', () => {
    it('should preserve bold text exactly', async () => {
      await testExactPreservation('This is **bold text** in a sentence.');
    });

    it('should preserve italic text exactly', async () => {
      await testExactPreservation('This is *italic text* in a sentence.');
    });

    it('should preserve strikethrough text exactly', async () => {
      await testExactPreservation('This is ~~strikethrough text~~ in a sentence.');
    });

    it('should preserve inline code exactly', async () => {
      await testExactPreservation('This is `inline code` in a sentence.');
    });

    it('should preserve combined formatting exactly', async () => {
      await testExactPreservation('This has **bold**, *italic*, ~~strikethrough~~, and `code` all together.');
    });
  });

  describe('Headings - Exact Preservation', () => {
    it('should preserve H1 headings exactly', async () => {
      await testExactPreservation('# Main Heading\n\nSome content below.');
    });

    it('should preserve H2 headings exactly', async () => {
      await testExactPreservation('## Section Heading\n\nSome content below.');
    });

    it('should preserve H3 headings exactly', async () => {
      await testExactPreservation('### Subsection Heading\n\nSome content below.');
    });
  });

  describe('Lists - Exact Preservation', () => {
    it('should preserve unordered lists exactly', async () => {
      await testExactPreservation('- First item\n- Second item\n- Third item');
    });

    it('should preserve ordered lists exactly', async () => {
      await testExactPreservation('1. First item\n2. Second item\n3. Third item');
    });


  });

  describe('Links and Images - Exact Preservation', () => {
    it('should preserve simple links exactly', async () => {
      await testExactPreservation('Check out [this link](https://example.com) for more info.');
    });

    it('should preserve links with titles exactly', async () => {
      await testExactPreservation('Visit [Google](https://google.com "Search Engine") today.');
    });

    it('should preserve image references exactly', async () => {
      await testExactPreservation('Here is an image: ![Alt text](https://example.com/image.png)');
    });
  });

  describe('Code Blocks - Exact Preservation', () => {
    it('should preserve code blocks exactly', async () => {
      await testExactPreservation('```\nfunction hello() {\n  console.log("Hello world!");\n}\n```');
    });

    it('should preserve code blocks with language exactly', async () => {
      await testExactPreservation('```javascript\nfunction hello() {\n  console.log("Hello world!");\n}\n```');
    });
  });

  describe('Blockquotes - Exact Preservation', () => {
    it('should preserve simple blockquotes exactly', async () => {
      await testExactPreservation('> This is a blockquote\n> spanning multiple lines.');
    });

    it('should preserve blockquotes with formatting exactly', async () => {
      await testExactPreservation('> This blockquote has **bold** and *italic* text.');
    });
  });

  describe('Tables - Known Issues', () => {
    it('should preserve simple tables (may fail - known issue)', async () => {
      const input = '| Header 1 | Header 2 | Header 3 |\n|----------|----------|----------|\n| Cell 1   | Cell 2   | Cell 3   |\n| Cell 4   | Cell 5   | Cell 6   |';
      try {
        await testExactPreservation(input);
      } catch (error) {
        console.log('Table preservation failed as expected:', error);
        // Get the actual output to see what's happening
        const actual = await testMarkdownPreservation(input, 'IGNORE', 'table test');
        console.log('Expected:', input);
        console.log('Actual:', actual);
        throw error; // Re-throw to fail the test and show the issue
      }
    });
  });

  // Removed skipped investigative tests that were not part of the product validation
}); 