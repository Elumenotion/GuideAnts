import React from 'react';
import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import MessageDiff from '../MessageDiff';

describe('MessageDiff', () => {
  it('preserves line breaks in word-level diff for small changes', () => {
    const original = 'Hello world.\nThis is a test.\nAnother line.';
    const edited = 'Hello universe.\nThis is a test.\nAnother line.';
    
    render(<MessageDiff original={original} edited={edited} />);
    
    // Should preserve the structure and show the diff
    expect(screen.getByText('world')).toBeInTheDocument();
    expect(screen.getByText('universe')).toBeInTheDocument();
  });

  it('shows line-based diff for significant changes', () => {
    const original = `Line 1
Line 2
Line 3
Line 4
Line 5`;
    
    const edited = `Line 1
Modified Line 2
New Line 2.5
Line 3
Line 4
Different Line 5`;
    
    render(<MessageDiff original={original} edited={edited} />);
    
    // Should show line-based diff with proper styling
    expect(screen.getByText('Line 1')).toBeInTheDocument();
    expect(screen.getByText('+ Modified Line 2')).toBeInTheDocument();
    expect(screen.getByText('+ New Line 2.5')).toBeInTheDocument();
    expect(screen.getByText('- Line 2')).toBeInTheDocument();
  });

  it('handles empty strings gracefully', () => {
    render(<MessageDiff original="" edited="New content" />);
    
    expect(screen.getByText('New content')).toBeInTheDocument();
  });

  it('handles identical content', () => {
    const content = 'Same content\nMultiple lines\nNo changes';
    const { container } = render(<MessageDiff original={content} edited={content} />);
    
    // With identical content, it should show as unchanged text with preserved line breaks
    expect(container.querySelector('.prose-sm')).toBeInTheDocument();
    expect(container.textContent).toContain('Same content');
    expect(container.textContent).toContain('Multiple lines');
  });

  it('preserves markdown-like formatting', () => {
    const original = `# Heading
- List item 1
- List item 2

Some paragraph text.`;
    
    const edited = `# Modified Heading
- List item 1
- List item 2
- New list item

Some paragraph text.`;
    
    render(<MessageDiff original={original} edited={edited} />);
    
    // Should preserve structure and show diffs
    expect(screen.getByText('- # Heading')).toBeInTheDocument();
    expect(screen.getByText('+ # Modified Heading')).toBeInTheDocument();
    expect(screen.getByText('+ - New list item')).toBeInTheDocument();
  });

  it('handles code blocks with proper formatting', () => {
    const original = `Here's some code:

\`\`\`javascript
function hello() {
  console.log("Hello");
}
\`\`\`

End of code.`;
    
    const edited = `Here's some code:

\`\`\`javascript
function hello() {
  console.log("Hello World");
}
\`\`\`

End of code.`;
    
    const { container } = render(<MessageDiff original={original} edited={edited} />);
    
    // Should preserve code block structure and show the diff
    // This is a small change, so it uses word-level diff with preserved formatting  
    expect(container.textContent).toContain('```javascript');
    expect(container.textContent).toContain('function hello()');
    
    // Should show the actual diff - " World" added (with leading space)
    expect(container.textContent).toContain('World');
  });
}); 