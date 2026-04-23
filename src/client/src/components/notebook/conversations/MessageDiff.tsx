// diff package lacks its own types; import as any
// eslint-disable-next-line @typescript-eslint/ban-ts-comment
// @ts-ignore
import { diffWords, diffLines } from 'diff';

interface MessageDiffProps {
  original: string;
  edited: string;
}

/**
 * Displays inline diff between original and edited messages.
 * Preserves line breaks and formatting.
 * Added content → green background, removed → red strikethrough.
 */
export default function MessageDiff({ original, edited }: MessageDiffProps) {
  // First, try line-based diff to preserve structure
  const lineChanges: Array<{ added?: boolean; removed?: boolean; value: string }> = diffLines(original, edited);
  
  // If there are only a few line changes, use line-based diff with word-level detail
  const hasSignificantLineChanges = lineChanges.filter((change: { added?: boolean; removed?: boolean; value: string }) => change.added || change.removed).length > 2;
  
  if (!hasSignificantLineChanges) {
    // For small changes, use detailed word-level diff but preserve line breaks
    const parts: Array<{ added?: boolean; removed?: boolean; value: string }> = diffWords(original, edited);
    
    return (
      <div className="prose-sm whitespace-pre-wrap font-sans text-sm leading-relaxed">
        {parts.map((part, idx) => {
          if (part.added) {
            return (
              <span
                key={idx}
                className="bg-green-100 text-green-800 rounded px-0.5"
              >
                {part.value}
              </span>
            );
          }
          if (part.removed) {
            return (
              <span
                key={idx}
                className="bg-red-100 text-red-800 line-through rounded px-0.5"
              >
                {part.value}
              </span>
            );
          }
          return <span key={idx}>{part.value}</span>;
        })}
      </div>
    );
  }

  // For larger changes, use line-based diff
  return (
    <div className="prose-sm font-sans text-sm leading-relaxed">
      {lineChanges.map((change: { added?: boolean; removed?: boolean; value: string }, idx: number) => {
        const lines = change.value.split('\n');
        
        return lines.map((line: string, lineIdx: number) => {
          // Skip empty lines at the end (artifact of split)
          if (lineIdx === lines.length - 1 && line === '') {
            return null;
          }
          
          const key = `${idx}-${lineIdx}`;
          
          if (change.added) {
            return (
              <div
                key={key}
                className="bg-green-50 border-l-4 border-green-400 pl-3 py-1 my-1"
              >
                <span className="bg-green-100 text-green-800 rounded px-1">
                  + {line}
                </span>
              </div>
            );
          }
          
          if (change.removed) {
            return (
              <div
                key={key}
                className="bg-red-50 border-l-4 border-red-400 pl-3 py-1 my-1"
              >
                <span className="bg-red-100 text-red-800 line-through rounded px-1">
                  - {line}
                </span>
              </div>
            );
          }
          
          // Unchanged line
          return (
            <div key={key} className="pl-3 py-1">
              {line}
            </div>
          );
        });
      })}
    </div>
  );
} 