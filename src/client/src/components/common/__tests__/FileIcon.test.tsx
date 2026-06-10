import React from 'react';
import { describe, it, expect } from 'vitest';
import { render } from '../../../test/test-utils';
import { FileIcon } from '../FileIcon';

interface Case {
  description: string;
  contentType: string;
  fileName?: string;
  expectedColorClass: string;
}

const cases: Case[] = [
  {
    description: 'Word document by MIME',
    contentType: 'application/msword',
    fileName: 'report.doc',
    expectedColorClass: 'text-blue-600',
  },
  {
    description: 'Image by MIME',
    contentType: 'image/png',
    fileName: 'photo.png',
    expectedColorClass: 'text-blue-500',
  },
  {
    description: 'PDF by MIME',
    contentType: 'application/pdf',
    expectedColorClass: 'text-red-500',
  },
  {
    description: 'JavaScript by extension',
    contentType: 'text/plain',
    fileName: 'script.js',
    expectedColorClass: 'text-yellow-400',
  },
  {
    description: 'Python by extension',
    contentType: 'text/plain',
    fileName: 'program.py',
    expectedColorClass: 'text-blue-500',
  },
  {
    description: 'Markdown by extension',
    contentType: 'text/markdown',
    fileName: 'readme.md',
    expectedColorClass: 'text-blue-500',
  },
  {
    description: 'Fallback to default icon',
    contentType: 'application/unknown',
    fileName: 'file.unknown',
    expectedColorClass: 'text-gray-400',
  },
  {
    description: 'Excel spreadsheet by extension',
    contentType: 'application/octet-stream',
    fileName: 'budget.xlsx',
    expectedColorClass: 'text-green-600',
  },
  {
    description: 'PowerPoint by extension',
    contentType: 'application/octet-stream',
    fileName: 'deck.pptx',
    expectedColorClass: 'text-orange-600',
  },
  {
    description: 'Audio by MIME',
    contentType: 'audio/mpeg',
    fileName: 'track.mp3',
    expectedColorClass: 'text-purple-500',
  },
  {
    description: 'Video by MIME',
    contentType: 'video/mp4',
    fileName: 'clip.mp4',
    expectedColorClass: 'text-blue-500',
  },
  {
    description: 'Zip archive by extension',
    contentType: 'application/octet-stream',
    fileName: 'bundle.zip',
    expectedColorClass: 'text-yellow-600',
  },
  {
    description: 'TypeScript by extension',
    contentType: 'text/plain',
    fileName: 'component.ts',
    expectedColorClass: 'text-blue-600',
  },
  {
    description: 'TSX by extension',
    contentType: 'text/plain',
    fileName: 'Widget.tsx',
    expectedColorClass: 'text-blue-600',
  },
  {
    description: 'JSON by extension',
    contentType: 'text/plain',
    fileName: 'config.json',
    expectedColorClass: 'text-yellow-500',
  },
  {
    description: 'HTML by extension',
    contentType: 'text/plain',
    fileName: 'index.html',
    expectedColorClass: 'text-orange-500',
  },
  {
    description: 'CSS by extension',
    contentType: 'text/plain',
    fileName: 'styles.css',
    expectedColorClass: 'text-blue-500',
  },
  {
    description: 'Dockerfile by filename',
    contentType: 'text/plain',
    fileName: 'Dockerfile',
    expectedColorClass: 'text-blue-600',
  },
  {
    description: 'SQLite database by extension',
    contentType: 'application/octet-stream',
    fileName: 'app.db',
    expectedColorClass: 'text-gray-600',
  },
  {
    description: 'Plain text by MIME',
    contentType: 'text/plain',
    fileName: 'notes.txt',
    expectedColorClass: 'text-gray-500',
  },
  {
    description: 'JSX by extension',
    contentType: 'text/plain',
    fileName: 'App.jsx',
    expectedColorClass: 'text-blue-400',
  },
  {
    description: 'Java by extension',
    contentType: 'text/plain',
    fileName: 'Main.java',
    expectedColorClass: 'text-red-500',
  },
  {
    description: 'SQL by extension',
    contentType: 'text/plain',
    fileName: 'schema.sql',
    expectedColorClass: 'text-blue-400',
  },
  {
    description: 'YAML by extension',
    contentType: 'text/plain',
    fileName: 'config.yml',
    expectedColorClass: 'text-purple-400',
  },
  {
    description: 'RTF by extension',
    contentType: 'application/rtf',
    fileName: 'brief.rtf',
    expectedColorClass: 'text-blue-500',
  },
];

describe('FileIcon', () => {
  cases.forEach(({ description, contentType, fileName, expectedColorClass }) => {
    it(`renders correct icon for ${description}`, () => {
      const { container } = render(
        <FileIcon contentType={contentType} fileName={fileName ?? ''} />
      );
      const svg = container.querySelector('svg');
      expect(svg).not.toBeNull();
      expect(svg?.getAttribute('class')).toContain(expectedColorClass);
    });
  });

  it('applies custom className to rendered icon', () => {
    const { container } = render(
      <FileIcon contentType="application/pdf" className="w-8 h-8 custom-icon" />
    );
    const svg = container.querySelector('svg');
    expect(svg?.getAttribute('class')).toContain('custom-icon');
    expect(svg?.getAttribute('class')).toContain('text-red-500');
  });
}); 