import { describe, it, expect } from 'vitest';
import { 
  generateMarkdownFile, 
  generateFileName, 
  type AssistantContentMetadata 
} from '../assistantContentFile';

// Test utility to extract content from File object
const getFileContent = async (file: File): Promise<string> => {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as string);
    reader.onerror = reject;
    reader.readAsText(file);
  });
};

describe('assistantContentFile utilities', () => {
  describe('generateFileName', () => {
    it('should generate a basic filename from content', () => {
      const content = 'This is a test response from the assistant';
      const fileName = generateFileName(content);
      
      expect(fileName).toMatch(/^this-is-a-test-response-from-the-assistant-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}\.md$/);
    });

    it('should include assistant name in filename', () => {
      const content = 'Short response';
      const assistantName = 'GPT-4';
      const fileName = generateFileName(content, assistantName);
      
      expect(fileName).toMatch(/^gpt-4-short-response-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}\.md$/);
    });

    it('should prefer assistantName over selectedAssistant', () => {
      const content = 'Test response';
      const assistantName = 'Claude';
      const selectedAssistant = 'GPT-4';
      const fileName = generateFileName(content, assistantName, selectedAssistant);
      
      expect(fileName).toMatch(/^claude-test-response-/);
      expect(fileName).not.toMatch(/^gpt-4-/);
    });

    it('should fall back to selectedAssistant when assistantName is not provided', () => {
      const content = 'Test response';
      const selectedAssistant = 'GPT-4';
      const fileName = generateFileName(content, undefined, selectedAssistant);
      
      expect(fileName).toMatch(/^gpt-4-test-response-/);
    });

    it('should use default name for empty content', () => {
      const content = '';
      const fileName = generateFileName(content);
      
      expect(fileName).toMatch(/^assistant-response-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}\.md$/);
    });

    it('should use default name for content with only short lines', () => {
      const content = 'Hi\nOk\nYes';
      const fileName = generateFileName(content);
      
      expect(fileName).toMatch(/^assistant-response-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}\.md$/);
    });

    it('should sanitize special characters in content', () => {
      const content = 'This has special chars: @#$%^&*()+=[]{}|\\;:\'",.<>?/~`';
      const fileName = generateFileName(content);
      
      expect(fileName).toMatch(/^this-has-special-chars-/);
      // Check that the filename only contains allowed characters: letters, numbers, hyphens, dots
      expect(fileName).toMatch(/^[a-zA-Z0-9\-\.T]+$/);
    });

    it('should handle very long content by truncating', () => {
      const content = 'This is a very long response that should be truncated because it exceeds the fifty character limit that we have set for the base name generation process';
      const fileName = generateFileName(content);
      
      const baseName = fileName.split('-').slice(0, -5).join('-'); // Remove timestamp
      expect(baseName.length).toBeLessThanOrEqual(50);
    });

    it('should handle multiline content by using first meaningful line', () => {
      const content = 'Short\n\nThis is a longer line that should be used for filename generation\nAnother line';
      const fileName = generateFileName(content);
      
      expect(fileName).toMatch(/^this-is-a-longer-line-that-should-be-used-for-/);
    });
  });

  describe('generateMarkdownFile', () => {
    const mockMetadata: AssistantContentMetadata = {
      messageId: 'msg-123',
      conversationId: 'conv-456',
      assistantName: 'Claude',
      notebookTitle: 'Test Notebook',
      projectTitle: 'Test Project',
      generatedAt: '2024-01-15T10:30:00.000Z',
      wordCount: 5,
      selectedAssistant: 'Claude'
    };

    it('should generate a complete markdown file with front matter', async () => {
      const content = 'This is the test content';
      const fileName = 'test-file.md';
      
      const file = generateMarkdownFile(content, fileName, mockMetadata);
      
      expect(file.name).toBe(fileName);
      expect(file.type).toBe('text/markdown');
      
      // Convert File to text to check content
      const text = await getFileContent(file);
      
      expect(text).toContain('---');
      expect(text).toContain('title: "Assistant Response"');
      expect(text).toContain('source: "conversation"');
      expect(text).toContain('generated_at: "2024-01-15T10:30:00.000Z"');
      expect(text).toContain('assistant: "Claude"');
      expect(text).toContain('notebook: "Test Notebook"');
      expect(text).toContain('project: "Test Project"');
      expect(text).toContain('message_id: "msg-123"');
      expect(text).toContain('conversation_id: "conv-456"');
      expect(text).toContain('word_count: 5');
      expect(text).toContain('# Assistant Response');
      expect(text).toContain('This is the test content');
      expect(text).toContain('**Metadata:**');
      expect(text).toContain('- Message ID: msg-123');
      expect(text).toContain('- Word Count: 5 words');
    });

    it('should handle minimal metadata gracefully', async () => {
      const minimalMetadata: AssistantContentMetadata = {
        messageId: 'msg-123',
        generatedAt: '2024-01-15T10:30:00.000Z',
        wordCount: 3
      };
      
      const content = 'Simple test content';
      const fileName = 'simple-test.md';
      
      const file = generateMarkdownFile(content, fileName, minimalMetadata);
      
      const text = await getFileContent(file);
      
      expect(text).toContain('assistant: "Assistant"'); // Default value
      expect(text).toContain('notebook: ""'); // Empty value
      expect(text).toContain('project: ""'); // Empty value
      expect(text).toContain('conversation_id: ""'); // Default for missing in front matter
      expect(text).toContain('- Conversation ID: N/A'); // Default for missing in footer
      expect(text).toContain('Simple test content');
    });

    it('should format dates correctly in header', async () => {
      const metadata: AssistantContentMetadata = {
        messageId: 'msg-123',
        generatedAt: '2024-01-15T10:30:00.000Z',
        wordCount: 3,
        assistantName: 'GPT-4'
      };
      
      const content = 'Test content';
      const fileName = 'test.md';
      
      const file = generateMarkdownFile(content, fileName, metadata);
      
      const text = await getFileContent(file);
      
      expect(text).toContain('January 15, 2024');
      expect(text).toContain('05:30 AM'); // UTC time converted to local time
      expect(text).toContain('**GPT-4**');
    });

    it('should include project information when available', async () => {
      const metadata: AssistantContentMetadata = {
        messageId: 'msg-123',
        generatedAt: '2024-01-15T10:30:00.000Z',
        wordCount: 3,
        notebookTitle: 'Data Analysis',
        projectTitle: 'Customer Research'
      };
      
      const content = 'Analysis results';
      const fileName = 'analysis.md';
      
      const file = generateMarkdownFile(content, fileName, metadata);
      
      const text = await getFileContent(file);
      
      expect(text).toContain('*Notebook: Data Analysis | Project: Customer Research*');
      expect(text).toContain('- Notebook: Data Analysis');
      expect(text).toContain('- Project: Customer Research');
    });

    it('should prefer assistantName over selectedAssistant in metadata', async () => {
      const metadata: AssistantContentMetadata = {
        messageId: 'msg-123',
        generatedAt: '2024-01-15T10:30:00.000Z',
        wordCount: 3,
        assistantName: 'Claude',
        selectedAssistant: 'GPT-4'
      };
      
      const content = 'Test content';
      const fileName = 'test.md';
      
      const file = generateMarkdownFile(content, fileName, metadata);
      
      const text = await getFileContent(file);
      
      expect(text).toContain('**Claude**');
      expect(text).toContain('- Assistant: Claude');
      expect(text).not.toContain('GPT-4');
    });

    it('should use selectedAssistant when assistantName is not available', async () => {
      const metadata: AssistantContentMetadata = {
        messageId: 'msg-123',
        generatedAt: '2024-01-15T10:30:00.000Z',
        wordCount: 3,
        selectedAssistant: 'GPT-4'
      };
      
      const content = 'Test content';
      const fileName = 'test.md';
      
      const file = generateMarkdownFile(content, fileName, metadata);
      
      const text = await getFileContent(file);
      
      expect(text).toContain('**GPT-4**');
      expect(text).toContain('- Assistant: GPT-4');
    });
  });
}); 