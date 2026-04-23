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
}); 