import { convertAbsoluteToRelative } from './markdownUrlConverter';

export interface AssistantContentMetadata {
  messageId: string;
  conversationId?: string;
  assistantName?: string;
  notebookTitle?: string;
  projectTitle?: string;
  generatedAt: string;
  wordCount: number;
  selectedAssistant?: string;
  projectId?: string;
  notebookId?: string;
}

export const generateMarkdownFile = (
  content: string,
  fileName: string,
  metadata: AssistantContentMetadata
): File => {
  // Convert absolute API URLs to relative paths for portability
  const portableContent = convertAbsoluteToRelative(
    content,
    metadata.projectId,
    metadata.notebookId
  );
  
  const frontMatter = generateFrontMatter(metadata);
  const header = generateHeader(metadata);
  const footer = generateFooter(metadata);
  
  const fileContent = `${frontMatter}\n\n${header}\n\n---\n\n${portableContent}\n\n${footer}`;
  
  return new File([fileContent], fileName, { type: 'text/markdown' });
};

const generateFrontMatter = (metadata: AssistantContentMetadata): string => {
  return `---
title: "Assistant Response"
source: "conversation"
generated_at: "${metadata.generatedAt}"
assistant: "${metadata.assistantName || 'Assistant'}"
notebook: "${metadata.notebookTitle || ''}"
project: "${metadata.projectTitle || ''}"
message_id: "${metadata.messageId}"
conversation_id: "${metadata.conversationId || ''}"
word_count: ${metadata.wordCount}
---`;
};

const generateHeader = (metadata: AssistantContentMetadata): string => {
  const date = new Date(metadata.generatedAt);
  const formattedDate = date.toLocaleDateString('en-US', {
    year: 'numeric',
    month: 'long',
    day: 'numeric'
  });
  const formattedTime = date.toLocaleTimeString('en-US', {
    hour: '2-digit',
    minute: '2-digit'
  });

  const assistantName = metadata.assistantName || metadata.selectedAssistant || 'Assistant';
  
  let headerLines = [
    '# Assistant Response',
    '',
    `*Generated from conversation with **${assistantName}** on ${formattedDate} at ${formattedTime}*`
  ];

  if (metadata.notebookTitle) {
    headerLines.push(`*Notebook: ${metadata.notebookTitle}${metadata.projectTitle ? ` | Project: ${metadata.projectTitle}` : ''}*`);
  }

  return headerLines.join('\n');
};

const generateFooter = (metadata: AssistantContentMetadata): string => {
  const footerLines = [
    '---',
    '',
    '**Metadata:**',
    `- Message ID: ${metadata.messageId}`,
    `- Conversation ID: ${metadata.conversationId || 'N/A'}`,
    `- Word Count: ${metadata.wordCount} words`,
    `- Generated: ${metadata.generatedAt}`,
    `- Assistant: ${metadata.assistantName || metadata.selectedAssistant || 'Assistant'}`
  ];

  if (metadata.notebookTitle) {
    footerLines.push(`- Notebook: ${metadata.notebookTitle}`);
  }

  if (metadata.projectTitle) {
    footerLines.push(`- Project: ${metadata.projectTitle}`);
  }

  return footerLines.join('\n');
};

export const generateFileName = (
  content: string,
  assistantName?: string,
  selectedAssistant?: string
): string => {
  // Generate base name from content (first meaningful sentence)
  let baseName = content
    .split('\n')
    .find(line => line.trim().length > 10)
    ?.trim()
    .slice(0, 50)
    .replace(/[^a-zA-Z0-9\s-]/g, '')
    .replace(/\s+/g, '-')
    .toLowerCase();

  if (!baseName) {
    baseName = 'assistant-response';
  }

  // Add assistant name if available
  const assistant = assistantName || selectedAssistant;
  if (assistant) {
    baseName = `${assistant.toLowerCase().replace(/\s+/g, '-')}-${baseName}`;
  }

  // Add timestamp for uniqueness
  const timestamp = new Date().toISOString()
    .replace(/:/g, '-')
    .replace(/\./g, '-')
    .slice(0, 16);

  return `${baseName}-${timestamp}.md`;
}; 

// ----------------- Tool-call markdown -----------------

export interface ToolCallMarkdownOptions {
  toolName: string;
  parameters: Record<string, any> | string;
  resultContent: string;
  metadata: AssistantContentMetadata;
}

/**
 * Builds a markdown string for a tool-call result, reusing the same header/footer
 * logic as assistant responses so users get a consistent file layout.
 */
export const buildToolCallMarkdown = ({ toolName, parameters, resultContent, metadata }: ToolCallMarkdownOptions): string => {
  // Format parameters with proper JSON formatting
  let formattedParams: string;
  try {
    const parsedParams = typeof parameters === 'string' ? JSON.parse(parameters) : parameters;
    formattedParams = JSON.stringify(parsedParams, null, 2);
  } catch {
    formattedParams = typeof parameters === 'string' ? parameters : JSON.stringify(parameters, null, 2);
  }

  // Clean header without front matter pollution
  const date = new Date(metadata.generatedAt);
  const formattedDate = date.toLocaleDateString('en-US', {
    year: 'numeric',
    month: 'long', 
    day: 'numeric'
  });
  const formattedTime = date.toLocaleTimeString('en-US', {
    hour: '2-digit',
    minute: '2-digit'
  });

  // Convert absolute API URLs to relative paths for portability
  const portableResultContent = convertAbsoluteToRelative(
    resultContent,
    metadata.projectId,
    metadata.notebookId
  );

  const content = [
    `# Tool Call: ${toolName}`,
    '',
    `**Generated:** ${formattedDate} at ${formattedTime}`,
    `**Notebook:** ${metadata.notebookTitle || 'Unknown'}`,
    `**Message ID:** \`${metadata.messageId}\``,
    '',
    '---',
    '',
    '## Parameters',
    '',
    formattedParams.split('\n').map(line => `    ${line}`).join('\n'),
    '',
    '---',
    '',
    '## Result',
    '',
    portableResultContent,
    '',
    '---',
    '',
    '### Metadata',
    '',
    `- **Word Count:** ${portableResultContent.trim().split(/\s+/).length} words`,
    `- **Generated:** ${metadata.generatedAt}`,
    `- **Tool:** ${toolName}`,
    ''
  ].join('\n');

  return content;
};

export const generateToolCallMarkdownFile = (opts: ToolCallMarkdownOptions, fileName: string): File => {
  const content = buildToolCallMarkdown(opts);
  return new File([content], fileName, { type: 'text/markdown' });
}; 