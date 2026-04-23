/**
 * Encode spaces in markdown link destinations so links like
 * [label](./My File.pptx) are parsed correctly.
 *
 * This avoids touching fenced code blocks and keeps optional titles intact.
 */
export function encodeMarkdownLinkSpaces(markdownContent: string): string {
  if (!markdownContent) return markdownContent;

  const parts = markdownContent.split(/(```[\s\S]*?```)/g);
  return parts
    .map((part) => {
      if (part.startsWith('```')) return part;
      return part.replace(/(!?\[[^\]]*\])\(([^)]+)\)/g, (match, linkText, destination) => {
        if (!destination || !destination.includes(' ')) return match;

        const trimmed = destination.trim();
        const hasAngleBrackets = trimmed.startsWith('<') && trimmed.endsWith('>');
        const raw = hasAngleBrackets ? trimmed.slice(1, -1) : destination;

        const titleMatch = raw.match(/\s+(['"]).*$/);
        const titleIndex = titleMatch?.index ?? -1;
        const urlPart = titleIndex >= 0 ? raw.slice(0, titleIndex) : raw;
        const titlePart = titleIndex >= 0 ? raw.slice(titleIndex) : '';

        const encodedUrl = urlPart.replace(/ /g, '%20');
        const rebuilt = `${encodedUrl}${titlePart}`;
        const wrapped = hasAngleBrackets ? `<${rebuilt}>` : rebuilt;

        return `${linkText}(${wrapped})`;
      });
    })
    .join('');
}
