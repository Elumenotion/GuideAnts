import { describe, it, expect } from 'vitest';
import { $getRoot, createEditor } from 'lexical';
import { $convertFromMarkdownString, $convertToMarkdownString, TRANSFORMERS } from '@lexical/markdown';
import { HeadingNode } from '@lexical/rich-text';

describe('Debug Lexical blank line behavior', () => {
  it('shows what nodes are created for heading + paragraph', () => {
    const editor = createEditor({
      onError: () => {},
      nodes: [HeadingNode]
    });

    const markdown = '# Heading\n\nParagraph';
    
    editor.update(() => {
      $convertFromMarkdownString(markdown, TRANSFORMERS);
      
      const root = $getRoot();
      const children = root.getChildren();
      
      console.log('=== NODES CREATED ===');
      children.forEach((child, i) => {
        const type = child.getType();
        const text = child.getTextContent();
        const childCount = (child as any).getChildrenSize?.() || 0;
        console.log(`[${i}] ${type}: "${text}" (${childCount} children)`);
      });
      
      // Now export back
      const exported = $convertToMarkdownString(TRANSFORMERS);
      console.log('\n=== EXPORTED MARKDOWN ===');
      console.log(JSON.stringify(exported));
      console.log('Newline count:', (exported.match(/\n/g) || []).length);
    });
  });
}); 