import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { 
  cleanupOrphanedMermaidElements, 
  validateMermaidSyntax, 
  setupMermaidCleanupInterval 
} from '../mermaidCleanup';

describe('mermaidCleanup utilities', () => {
  beforeEach(() => {
    // Clean up any existing elements
    document.body.innerHTML = '';
  });

  afterEach(() => {
    // Clean up after each test
    document.body.innerHTML = '';
  });

  describe('cleanupOrphanedMermaidElements', () => {
    it('removes orphaned mermaid SVG elements from body', () => {
      // Create orphaned elements
      const orphanedSvg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
      orphanedSvg.id = 'mermaid-test123';
      document.body.appendChild(orphanedSvg);

      const orphanedDiv = document.createElement('div');
      orphanedDiv.id = 'mermaid-test456';
      document.body.appendChild(orphanedDiv);

      expect(document.body.children).toHaveLength(2);

      cleanupOrphanedMermaidElements();

      expect(document.body.children).toHaveLength(0);
    });

    it('does not remove elements that are not direct children of body', () => {
      const container = document.createElement('div');
      const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
      svg.id = 'mermaid-test123';
      container.appendChild(svg);
      document.body.appendChild(container);

      expect(document.body.children).toHaveLength(1);
      expect(container.children).toHaveLength(1);

      cleanupOrphanedMermaidElements();

      // Container should still be there, and SVG should still be in container
      expect(document.body.children).toHaveLength(1);
      expect(container.children).toHaveLength(1);
    });
  });

  describe('validateMermaidSyntax', () => {
    it('validates basic flowchart syntax', () => {
      const result = validateMermaidSyntax('graph TD; A-->B');
      expect(result.isValid).toBe(true);
      expect(result.error).toBeUndefined();
    });

    it('validates sequence diagram syntax', () => {
      const result = validateMermaidSyntax('sequenceDiagram\n  participant A\n  A->>B: Hello');
      expect(result.isValid).toBe(true);
    });

    it('rejects empty chart', () => {
      const result = validateMermaidSyntax('');
      expect(result.isValid).toBe(false);
      expect(result.error).toContain('empty');
    });

    it('rejects invalid chart type', () => {
      const result = validateMermaidSyntax('invalidDiagram TD; A-->B');
      expect(result.isValid).toBe(false);
      expect(result.error).toContain('valid diagram type');
    });

    it('rejects non-string input', () => {
      const result = validateMermaidSyntax(null as any);
      expect(result.isValid).toBe(false);
      expect(result.error).toContain('non-empty string');
    });
  });

  describe('setupMermaidCleanupInterval', () => {
    it('sets up interval and returns cleanup function', () => {
      vi.useFakeTimers();
      const consoleSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});

      // Create an orphaned element
      const orphanedSvg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
      orphanedSvg.id = 'mermaid-test123';
      document.body.appendChild(orphanedSvg);

      const cleanup = setupMermaidCleanupInterval(1000);

      expect(document.body.children).toHaveLength(1);

      // Fast-forward time
      vi.advanceTimersByTime(1000);

      expect(document.body.children).toHaveLength(0);

      cleanup();
      vi.useRealTimers();
      consoleSpy.mockRestore();
    });
  });
}); 