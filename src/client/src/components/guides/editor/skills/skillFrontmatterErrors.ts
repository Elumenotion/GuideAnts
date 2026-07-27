export interface SkillFrontmatterErrorLocation {
  line: number;
  column: number;
}

export interface SkillFrontmatterErrorSnippetLine {
  lineNumber: number;
  text: string;
  highlightColumn?: number;
}

export interface SkillFrontmatterErrorDetails {
  title: string;
  problem: string;
  fix: string;
  exampleFix?: string;
  location?: SkillFrontmatterErrorLocation;
  snippetLines: SkillFrontmatterErrorSnippetLine[];
  canRepair: boolean;
  repairedMarkdown?: string;
}

export class SkillFrontmatterParseError extends Error {
  readonly details: SkillFrontmatterErrorDetails;

  constructor(details: SkillFrontmatterErrorDetails) {
    super(details.problem);
    this.name = 'SkillFrontmatterParseError';
    this.details = details;
  }
}

export const SKILL_IMPORT_GUIDANCE = [
  'Frontmatter must be valid YAML between --- delimiters.',
  'name and description are required fields.',
  'If description contains colons (:), brackets ([]), or quotes, wrap the whole value in double quotes.',
  'Recommended description length is 60 characters; hard database limit is 1024 characters.',
] as const;

const DESCRIPTION_QUOTE_EXAMPLE = 'description: "Human-in-the-loop sample selection: consults audit methodology ..."';

interface YamlParseMark {
  line?: number;
  column?: number;
  snippet?: string;
}

function splitLines(text: string): string[] {
  return text.split(/\r?\n/);
}

function isQuotedYamlScalar(value: string): boolean {
  return (value.startsWith('"') && value.endsWith('"'))
    || (value.startsWith("'") && value.endsWith("'"));
}

function descriptionValueNeedsQuoting(value: string): boolean {
  if (!value) {
    return false;
  }

  if (isQuotedYamlScalar(value)) {
    return false;
  }

  return /:\s/.test(value) || /[[\]#|>&*!?]/.test(value);
}

function findUnquotedDescriptionIssue(yamlText: string): {
  lineNumber: number;
  column: number;
  lineText: string;
} | null {
  const lines = splitLines(yamlText);
  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    const match = line.match(/^description:\s*(.*)$/);
    if (!match) {
      continue;
    }

    const value = match[1].trim();
    if (!descriptionValueNeedsQuoting(value)) {
      continue;
    }

    const firstColon = line.indexOf(':');
    const secondColon = line.indexOf(':', firstColon + 1);
    const column = secondColon >= 0 ? secondColon + 1 : firstColon + 2;

    return {
      lineNumber: index + 1,
      column,
      lineText: line,
    };
  }

  return null;
}

function quoteDescriptionValue(value: string): string {
  const escaped = value
    .replace(/\\/g, '\\\\')
    .replace(/"/g, '\\"');
  return `"${escaped}"`;
}

export function repairUnquotedDescriptionMarkdown(markdown: string): string | null {
  if (!markdown.startsWith('---')) {
    return null;
  }

  const closeIndex = markdown.indexOf('\n---', 3);
  if (closeIndex < 0) {
    return null;
  }

  const yamlText = markdown.slice(3, closeIndex);
  const lines = splitLines(yamlText);
  let changed = false;

  const repairedLines = lines.map((line) => {
    const match = line.match(/^description:\s*(.*)$/);
    if (!match) {
      return line;
    }

    const rawValue = match[1];
    const trimmed = rawValue.trim();
    if (!descriptionValueNeedsQuoting(trimmed)) {
      return line;
    }

    changed = true;
    return `description: ${quoteDescriptionValue(trimmed)}`;
  });

  if (!changed) {
    return null;
  }

  const body = markdown.slice(closeIndex + '\n---'.length);
  return `---\n${repairedLines.join('\n')}\n---${body}`;
}

function buildSnippetLines(
  yamlText: string,
  location?: SkillFrontmatterErrorLocation,
): SkillFrontmatterErrorSnippetLine[] {
  const lines = splitLines(yamlText);
  if (lines.length === 0) {
    return [];
  }

  const targetLine = location?.line ?? 1;
  const start = Math.max(0, targetLine - 2);
  const end = Math.min(lines.length, targetLine + 1);

  return lines.slice(start, end).map((text, offset) => {
    const lineNumber = start + offset + 1;
    return {
      lineNumber,
      text,
      highlightColumn: lineNumber === targetLine ? location?.column : undefined,
    };
  });
}

function buildDescriptionQuoteErrorDetails(
  yamlText: string,
  issue: { lineNumber: number; column: number; lineText: string },
  markdown: string,
): SkillFrontmatterErrorDetails {
  const repairedMarkdown = repairUnquotedDescriptionMarkdown(markdown);
  const quotedValue = issue.lineText.match(/^description:\s*(.*)$/)?.[1]?.trim() ?? '';
  const exampleFix = `description: ${quoteDescriptionValue(quotedValue)}`;

  return {
    title: 'Invalid description in SKILL.md frontmatter',
    problem: 'The description contains characters (such as a colon) that YAML treats as syntax unless the value is quoted.',
    fix: 'Wrap the entire description value in double quotes.',
    exampleFix,
    location: { line: issue.lineNumber, column: issue.column },
    snippetLines: buildSnippetLines(yamlText, { line: issue.lineNumber, column: issue.column }),
    canRepair: repairedMarkdown !== null,
    repairedMarkdown: repairedMarkdown ?? undefined,
  };
}

function parseYamlMark(error: unknown): YamlParseMark | null {
  if (!error || typeof error !== 'object') {
    return null;
  }

  const mark = (error as { mark?: YamlParseMark }).mark;
  if (!mark || typeof mark !== 'object') {
    return null;
  }

  return mark;
}

function extractLocationFromMessage(message: string): SkillFrontmatterErrorLocation | undefined {
  const match = message.match(/\((\d+):(\d+)\)/);
  if (!match) {
    return undefined;
  }

  return {
    line: Number.parseInt(match[1], 10),
    column: Number.parseInt(match[2], 10),
  };
}

export function formatSkillFrontmatterParseError(
  error: unknown,
  markdown: string,
  yamlText: string,
): SkillFrontmatterErrorDetails {
  const unquotedIssue = findUnquotedDescriptionIssue(yamlText);
  if (unquotedIssue) {
    return buildDescriptionQuoteErrorDetails(yamlText, unquotedIssue, markdown);
  }

  const mark = parseYamlMark(error);
  const message = error instanceof Error ? error.message : String(error);
  const location = mark?.line != null && mark.column != null
    ? { line: mark.line + 1, column: mark.column + 1 }
    : extractLocationFromMessage(message);

  const repairedMarkdown = repairUnquotedDescriptionMarkdown(markdown);
  const snippetLines = buildSnippetLines(yamlText, location);

  if (location && snippetLines.some((line) => line.text.trimStart().startsWith('description:'))) {
    return buildDescriptionQuoteErrorDetails(yamlText, {
      lineNumber: location.line,
      column: location.column,
      lineText: snippetLines.find((line) => line.lineNumber === location.line)?.text ?? 'description: ...',
    }, markdown);
  }

  return {
    title: 'Invalid YAML in SKILL.md frontmatter',
    problem: location
      ? `Frontmatter YAML could not be parsed at line ${location.line}, column ${location.column}.`
      : 'Frontmatter YAML could not be parsed.',
    fix: 'Check indentation, quoting, and that each field uses valid YAML syntax. Wrap values containing colons or brackets in double quotes.',
    exampleFix: DESCRIPTION_QUOTE_EXAMPLE,
    location,
    snippetLines,
    canRepair: repairedMarkdown !== null,
    repairedMarkdown: repairedMarkdown ?? undefined,
  };
}

export function toSkillFrontmatterParseError(
  error: unknown,
  markdown: string,
  yamlText: string,
): SkillFrontmatterParseError {
  return new SkillFrontmatterParseError(formatSkillFrontmatterParseError(error, markdown, yamlText));
}

export function getSkillFrontmatterErrorDetails(error: unknown): SkillFrontmatterErrorDetails | null {
  if (error instanceof SkillFrontmatterParseError) {
    return error.details;
  }

  return null;
}

export function buildSimpleFrontmatterError(
  title: string,
  problem: string,
  fix: string,
): SkillFrontmatterParseError {
  return new SkillFrontmatterParseError({
    title,
    problem,
    fix,
    snippetLines: [],
    canRepair: false,
  });
}
