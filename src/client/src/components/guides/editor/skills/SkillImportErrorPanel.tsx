import type { SkillFrontmatterErrorDetails } from './skillFrontmatterErrors';

interface SkillImportErrorPanelProps {
  error: SkillFrontmatterErrorDetails;
  onRepair?: () => void;
  isRepairing?: boolean;
}

export function SkillImportErrorPanel({ error, onRepair, isRepairing = false }: SkillImportErrorPanelProps) {
  return (
    <div className="mt-4 rounded-md border border-red-200 bg-red-50 p-4 text-sm text-red-900" role="alert">
      <h3 className="font-semibold text-red-950">{error.title}</h3>
      <p className="mt-2 leading-relaxed">{error.problem}</p>

      {error.location && (
        <p className="mt-2 text-red-800">
          Location: line {error.location.line}, column {error.location.column}
        </p>
      )}

      <p className="mt-2 leading-relaxed">
        <span className="font-medium">Fix:</span> {error.fix}
      </p>

      {error.exampleFix && (
        <div className="mt-3">
          <p className="mb-1 font-medium text-red-950">Example</p>
          <pre className="overflow-x-auto rounded border border-red-200 bg-white px-3 py-2 font-mono text-xs text-gray-900">
            {error.exampleFix}
          </pre>
        </div>
      )}

      {error.snippetLines.length > 0 && (
        <div className="mt-3">
          <p className="mb-1 font-medium text-red-950">Frontmatter snippet</p>
          <pre className="overflow-x-auto rounded border border-red-200 bg-white px-3 py-2 font-mono text-xs text-gray-900">
            {error.snippetLines.map((line) => (
              <div key={line.lineNumber}>
                <span className="select-none text-gray-500">{String(line.lineNumber).padStart(2, ' ')} | </span>
                <span>{line.text}</span>
                {line.highlightColumn != null && (
                  <div className="text-red-700">
                    {' '.repeat(String(line.lineNumber).length + 3)}
                    {'-'.repeat(Math.max(1, line.highlightColumn - 1))}
                    ^
                  </div>
                )}
              </div>
            ))}
          </pre>
        </div>
      )}

      {error.canRepair && onRepair && (
        <div className="mt-4">
          <button
            type="button"
            disabled={isRepairing}
            onClick={onRepair}
            className="rounded-md bg-amber-600 px-3 py-2 text-sm font-medium text-white hover:bg-amber-700 disabled:opacity-50"
          >
            {isRepairing ? 'Repairing...' : 'Quote description and import'}
          </button>
        </div>
      )}
    </div>
  );
}
