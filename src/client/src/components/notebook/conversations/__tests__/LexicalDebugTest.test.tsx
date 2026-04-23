import React, { useRef, useEffect, useState } from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import '@testing-library/jest-dom';
import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

describe('Lexical Image Conversion Debug', () => {
  it('converts image markdown correctly', async () => {
    const TestComponent = () => {
      const editorRef = useRef<LexicalEditorRef>(null);
      const [output, setOutput] = useState('');
      const [step, setStep] = useState(0);

      useEffect(() => {
        if (!editorRef.current) return;

        const runTest = async () => {
          if (step === 0) {
            // Step 1: Set markdown with image
            const input = '![Test Image](https://example.com/test.jpg)';
            console.log('Setting markdown:', input);
            editorRef.current!.setValue(input);
            setStep(1);
            
            // Give time for Lexical to process
            setTimeout(() => {
              // Step 2: Get the value back
              const result = editorRef.current!.getValue();
              console.log('Got back:', result);
              setOutput(result);
              setStep(2);
            }, 100);
          }
        };

        runTest();
      }, [step]);

      return (
        <div>
          <div data-testid="step">{step}</div>
          <div data-testid="output">{output}</div>
          <LexicalEditor
            ref={editorRef}
            placeholder="Test editor"
            showToolbar={false}
          />
        </div>
      );
    };

    render(<TestComponent />);

    await waitFor(() => {
      expect(screen.getByTestId('step')).toHaveTextContent('2');
    }, { timeout: 2000 });

    const output = screen.getByTestId('output').textContent;
    console.log('Final output:', output);
    
    // Should be exactly the same
    expect(output).toBe('![Test Image](https://example.com/test.jpg)');
  });

  it('handles complex markdown with images', async () => {
    const TestComponent = () => {
      const editorRef = useRef<LexicalEditorRef>(null);
      const [output, setOutput] = useState('');
      const [step, setStep] = useState(0);

      useEffect(() => {
        if (!editorRef.current) return;

        const runTest = async () => {
          if (step === 0) {
            const input = `# Title

Here is an image:

![My Image](https://example.com/image.jpg)

And a [regular link](https://example.com).`;
            
            console.log('Setting complex markdown:', input);
            editorRef.current!.setValue(input);
            setStep(1);
            
            setTimeout(() => {
              const result = editorRef.current!.getValue();
              console.log('Got back complex:', result);
              setOutput(result);
              setStep(2);
            }, 100);
          }
        };

        runTest();
      }, [step]);

      return (
        <div>
          <div data-testid="step">{step}</div>
          <div data-testid="output">{output}</div>
          <LexicalEditor
            ref={editorRef}
            placeholder="Test editor"
            showToolbar={false}
          />
        </div>
      );
    };

    render(<TestComponent />);

    await waitFor(() => {
      expect(screen.getByTestId('step')).toHaveTextContent('2');
    }, { timeout: 2000 });

    const output = screen.getByTestId('output').textContent!;
    
    // Check that image syntax is preserved
    expect(output).toContain('![My Image](https://example.com/image.jpg)');
    // Check that regular link is preserved
    expect(output).toContain('[regular link](https://example.com)');
    // Image should NOT be converted to a regular link (missing the !)
    // This checks that we don't have BOTH image and link syntax for the same item
    const imageAsLink = output.includes('[My Image](https://example.com/image.jpg)') && 
                        !output.includes('![My Image](https://example.com/image.jpg)');
    expect(imageAsLink).toBe(false);
  });
}); 