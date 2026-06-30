import type { EnvironmentVariableDto } from '../../../../types/guides';
import { EnvironmentSecretRefField } from '../EnvironmentSecretRefField';
import { isLoopbackMcpApiUrl, loopbackReachabilityWarning } from './mcpRuntimeMode';
import type { McpConnectionSettings, McpHeaderRow } from './mcpToolSourceTypes';

export interface McpHttpConnectionFieldsProps {
  settings: McpConnectionSettings;
  headerRows: McpHeaderRow[];
  environmentVariables: EnvironmentVariableDto[];
  onEnvironmentVariablesChange: (variables: EnvironmentVariableDto[]) => void;
  onUrlChange: (url: string) => void;
  onHeaderRowsChange: (rows: McpHeaderRow[]) => void;
}

export function McpHttpConnectionFields({
  settings,
  headerRows,
  environmentVariables,
  onEnvironmentVariablesChange,
  onUrlChange,
  onHeaderRowsChange,
}: McpHttpConnectionFieldsProps) {
  return (
    <>
      <div>
        <label className="block text-xs font-medium text-gray-700 mb-1">
          MCP server URL <span className="text-red-500">*</span>
        </label>
        <input
          type="url"
          value={settings.url}
          onChange={(e) => onUrlChange(e.target.value)}
          placeholder="https://mcp.example.com/mcp"
          className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md font-mono"
          data-testid="mcp-server-url"
        />
        {isLoopbackMcpApiUrl(settings.url) && (
          <p className="text-xs text-amber-800 bg-amber-50 border border-amber-200 rounded-md p-2 mt-2" role="alert">
            {loopbackReachabilityWarning()}
          </p>
        )}
      </div>

      <div>
        <div className="flex items-center justify-between mb-1">
          <label className="block text-xs font-medium text-gray-700">HTTP headers (optional)</label>
          <button
            type="button"
            onClick={() =>
              onHeaderRowsChange([
                ...headerRows,
                { key: '', secretRefName: '', literalValue: '', useLiteral: false },
              ])
            }
            className="text-xs text-blue-600 hover:underline"
          >
            Add header
          </button>
        </div>
        <div className="space-y-3">
          {headerRows.length === 0 && (
            <p className="text-xs text-gray-500">
              Use headers such as <code className="font-mono">Authorization</code> with a guide secret for API keys.
            </p>
          )}
          {headerRows.map((row, index) => (
            <div key={index} className="rounded-md border border-gray-200 bg-white p-3 space-y-3">
              <div className="flex gap-2">
                <input
                  type="text"
                  value={row.key}
                  placeholder="Header name"
                  onChange={(e) => {
                    const rows = [...headerRows];
                    rows[index] = { ...row, key: e.target.value };
                    onHeaderRowsChange(rows);
                  }}
                  className="flex-1 px-2 py-1 text-xs border border-gray-300 rounded-md font-mono"
                />
                <button
                  type="button"
                  onClick={() => onHeaderRowsChange(headerRows.filter((_, i) => i !== index))}
                  className="text-xs text-red-600 hover:underline"
                >
                  Remove
                </button>
              </div>

              {row.useLiteral ? (
                <div className="space-y-2">
                  <input
                    type="text"
                    value={row.literalValue}
                    placeholder="Literal header value"
                    onChange={(e) => {
                      const rows = [...headerRows];
                      rows[index] = { ...row, literalValue: e.target.value };
                      onHeaderRowsChange(rows);
                    }}
                    className="w-full px-2 py-1 text-xs border border-gray-300 rounded-md font-mono"
                  />
                  <button
                    type="button"
                    onClick={() => {
                      const rows = [...headerRows];
                      rows[index] = { ...row, useLiteral: false, literalValue: '' };
                      onHeaderRowsChange(rows);
                    }}
                    className="text-xs text-blue-600 hover:underline"
                  >
                    Use guide secret instead
                  </button>
                </div>
              ) : (
                <div className="space-y-2">
                  <EnvironmentSecretRefField
                    label="Guide secret"
                    selectedVariableName={row.secretRefName}
                    variables={environmentVariables}
                    onVariablesChange={onEnvironmentVariablesChange}
                    onSelectedVariableNameChange={(name) => {
                      const rows = [...headerRows];
                      rows[index] = { ...row, secretRefName: name };
                      onHeaderRowsChange(rows);
                    }}
                    hint="Stored as {{secret:NAME}} in the tool source descriptor."
                  />
                  <button
                    type="button"
                    onClick={() => {
                      const rows = [...headerRows];
                      rows[index] = { ...row, useLiteral: true, secretRefName: '' };
                      onHeaderRowsChange(rows);
                    }}
                    className="text-xs text-gray-600 hover:underline"
                  >
                    Use literal value instead
                  </button>
                </div>
              )}
            </div>
          ))}
        </div>
      </div>
    </>
  );
}
