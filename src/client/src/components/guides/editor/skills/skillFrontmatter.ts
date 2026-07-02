// @ts-expect-error js-yaml ships without bundled types in this project
import yaml from 'js-yaml';

export interface ParsedSkillFrontmatter {
  name: string;
  description: string;
  enabled: boolean;
  displayOrder: number;
  requiresToolsets: string[];
  requiresTools: string[];
  fallbackForToolsets: string[];
  fallbackForTools: string[];
  source?: string;
}

export interface SkillFrontmatterParseResult {
  frontmatter: ParsedSkillFrontmatter;
  body: string;
  originalMarkdown: string;
}

const MAX_SKILL_MARKDOWN_CHARS = 100_000;

function extractFrontmatterYaml(markdown: string): string {
  if (!markdown.startsWith('---')) {
    throw new Error('SKILL.md is missing YAML frontmatter delimiters (---).');
  }

  const closeIndex = markdown.indexOf('\n---', 3);
  if (closeIndex < 0) {
    throw new Error('SKILL.md frontmatter is not closed with a second --- delimiter.');
  }

  return markdown.slice(3, closeIndex).trim();
}

function extractBody(markdown: string): string {
  if (!markdown.startsWith('---')) {
    return markdown;
  }

  const closeIndex = markdown.indexOf('\n---', 3);
  if (closeIndex < 0) {
    return markdown;
  }

  let bodyStart = closeIndex + '\n---'.length;
  while (bodyStart < markdown.length && (markdown[bodyStart] === '\r' || markdown[bodyStart] === '\n')) {
    bodyStart += 1;
  }

  return markdown.slice(bodyStart);
}

function asRecord(value: unknown): Record<string, unknown> | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return null;
  }

  return value as Record<string, unknown>;
}

function getString(record: Record<string, unknown> | null, key: string): string | undefined {
  if (!record) {
    return undefined;
  }

  const value = record[key];
  if (typeof value === 'string') {
    return value;
  }

  if (value == null) {
    return undefined;
  }

  return String(value);
}

function getMetadataSection(root: Record<string, unknown>, section: string): Record<string, unknown> | null {
  const metadata = asRecord(root.metadata);
  if (!metadata) {
    return null;
  }

  return asRecord(metadata[section]);
}

function getStringList(record: Record<string, unknown> | null, key: string): string[] {
  if (!record) {
    return [];
  }

  const value = record[key];
  if (typeof value === 'string' && value.trim()) {
    return [value.trim()];
  }

  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .map((item) => (item == null ? '' : String(item).trim()))
    .filter((item) => item.length > 0);
}

function coalesceLists(...lists: string[][]): string[] {
  for (const list of lists) {
    if (list.length > 0) {
      return list;
    }
  }

  return [];
}

export function parseSkillFrontmatter(markdown: string): SkillFrontmatterParseResult {
  if (!markdown) {
    throw new Error('SKILL.md content is empty.');
  }

  if (markdown.length > MAX_SKILL_MARKDOWN_CHARS) {
    throw new Error(`SKILL.md exceeds maximum length of ${MAX_SKILL_MARKDOWN_CHARS.toLocaleString()} characters.`);
  }

  const yamlText = extractFrontmatterYaml(markdown);
  const root = asRecord(yaml.load(yamlText));
  if (!root) {
    throw new Error('SKILL.md frontmatter is empty.');
  }

  const guideants = getMetadataSection(root, 'guideants');
  const hermes = getMetadataSection(root, 'hermes');

  const name = getString(root, 'name')
    ?? getString(guideants, 'name')
    ?? getString(hermes, 'name');
  const description = getString(root, 'description')
    ?? getString(guideants, 'description')
    ?? getString(hermes, 'description');

  if (!name?.trim()) {
    throw new Error("SKILL.md frontmatter is missing required field 'name'.");
  }

  if (!description?.trim()) {
    throw new Error("SKILL.md frontmatter is missing required field 'description'.");
  }

  const enabledValue = guideants?.enabled ?? hermes?.enabled;
  const enabled = typeof enabledValue === 'boolean' ? enabledValue : true;
  const displayOrderRaw = guideants?.display_order ?? hermes?.display_order;
  const displayOrder = typeof displayOrderRaw === 'number' ? displayOrderRaw : 0;

  const frontmatter: ParsedSkillFrontmatter = {
    name: name.trim(),
    description: description.trim(),
    enabled,
    displayOrder,
    requiresToolsets: coalesceLists(
      getStringList(guideants, 'requires_toolsets'),
      getStringList(hermes, 'requires_toolsets'),
    ),
    requiresTools: coalesceLists(
      getStringList(guideants, 'requires_tools'),
      getStringList(hermes, 'requires_tools'),
      getStringList(root, 'allowed-tools'),
    ),
    fallbackForToolsets: coalesceLists(
      getStringList(guideants, 'fallback_for_toolsets'),
      getStringList(hermes, 'fallback_for_toolsets'),
    ),
    fallbackForTools: coalesceLists(
      getStringList(guideants, 'fallback_for_tools'),
      getStringList(hermes, 'fallback_for_tools'),
    ),
    source: getString(guideants, 'source') ?? getString(hermes, 'source'),
  };

  return {
    frontmatter,
    body: extractBody(markdown),
    originalMarkdown: markdown,
  };
}

export function buildCanonicalSkillMarkdown(input: {
  name: string;
  description: string;
  enabled: boolean;
  displayOrder: number;
  body: string;
  source: string;
  requiresToolsets?: string[];
  requiresTools?: string[];
}): string {
  const metadata: Record<string, unknown> = {
    guideants: {
      enabled: input.enabled,
      display_order: input.displayOrder,
      source: input.source,
    },
  };

  if (input.requiresToolsets && input.requiresToolsets.length > 0) {
    (metadata.guideants as Record<string, unknown>).requires_toolsets = input.requiresToolsets;
  }

  if (input.requiresTools && input.requiresTools.length > 0) {
    (metadata.guideants as Record<string, unknown>).requires_tools = input.requiresTools;
  }

  const frontmatter = {
    name: input.name,
    description: input.description,
    metadata,
  };

  const yamlBody = yaml.dump(frontmatter, { lineWidth: 120, noRefs: true }).trimEnd();
  const body = input.body.trimStart();
  return `---\n${yamlBody}\n---\n${body ? `\n${body}` : '\n'}`;
}

export function updateSkillFrontmatterFlags(
  originalMarkdown: string,
  enabled: boolean,
  displayOrder: number,
): string {
  const parsed = parseSkillFrontmatter(originalMarkdown);
  return buildCanonicalSkillMarkdown({
    name: parsed.frontmatter.name,
    description: parsed.frontmatter.description,
    enabled,
    displayOrder,
    body: parsed.body,
    source: parsed.frontmatter.source ?? 'Imported',
    requiresToolsets: parsed.frontmatter.requiresToolsets,
    requiresTools: parsed.frontmatter.requiresTools,
  });
}
