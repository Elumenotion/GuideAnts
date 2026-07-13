import { useState } from 'react';
import { resolveExternalApiUrl } from '../../../config/apiConfig';
import { PublishedGuideApiKeySection } from './PublishedGuideApiKeySection';

interface McpTabProps {
  mcpEnabled: boolean;
  setMcpEnabled: (enabled: boolean) => void;
  mcpDescription: string;
  setMcpDescription: (desc: string) => void;
  hasApiKey: boolean;
  sessionApiKey: string | null;
  guideId: string;
  publishedGuideId?: string;
  authWebhookUrl: string;
  mcpPersisted: boolean;
  onApiKeyChange: (hasKey: boolean) => void;
  onSessionApiKeyChange: (apiKey: string | null) => void;
  onEnableMcpAccess?: () => Promise<void>;
  onDownloadClaudeSkill?: () => Promise<void>;
}

export function McpTab({
  mcpEnabled,
  setMcpEnabled,
  mcpDescription,
  setMcpDescription,
  hasApiKey,
  sessionApiKey,
  guideId,
  publishedGuideId,
  authWebhookUrl,
  mcpPersisted,
  onApiKeyChange,
  onSessionApiKeyChange,
  onEnableMcpAccess,
  onDownloadClaudeSkill,
}: McpTabProps) {
  const [copied, setCopied] = useState(false);
  const [isEnabling, setIsEnabling] = useState(false);
  const [isDownloadingSkill, setIsDownloadingSkill] = useState(false);
  const [downloadSkillError, setDownloadSkillError] = useState<string | null>(null);
  const [downloadSkillWarning, setDownloadSkillWarning] = useState<string | null>(null);
  const [enableError, setEnableError] = useState<string | null>(null);

  const mcpEndpointUrl = publishedGuideId
    ? resolveExternalApiUrl(`/published/mcp?pubId=${publishedGuideId}`)
    : null;

  const copyEndpoint = async () => {
    if (mcpEndpointUrl) {
      await navigator.clipboard.writeText(mcpEndpointUrl);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    }
  };

  const handleEnableMcpAccess = async () => {
    if (!onEnableMcpAccess) return;
    setIsEnabling(true);
    setEnableError(null);
    try {
      await onEnableMcpAccess();
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : 'Failed to enable MCP access';
      setEnableError(message);
    } finally {
      setIsEnabling(false);
    }
  };

  const handleDownloadClaudeSkill = async () => {
    if (!onDownloadClaudeSkill) return;
    setIsDownloadingSkill(true);
    setDownloadSkillError(null);
    setDownloadSkillWarning(null);
    try {
      await onDownloadClaudeSkill();
      if (!sessionApiKey) {
        setDownloadSkillWarning(
          'Downloaded with a placeholder for `.env` after unzip. Regenerate the API key on this tab or Auth, then download again while the key is still visible.'
        );
      }
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : 'Failed to download Claude skill pack';
      setDownloadSkillError(message);
    } finally {
      setIsDownloadingSkill(false);
    }
  };

  const canDownloadSkill = mcpPersisted && hasApiKey && mcpEnabled;
  const showQuickSetup = publishedGuideId && onEnableMcpAccess && !mcpPersisted;

  return (
    <div className="space-y-6">
      <div>
        <h3 className="text-lg font-medium text-gray-900">MCP Server</h3>
        <p className="text-sm text-gray-500 mt-1">
          Expose this guide as a{' '}
          <a
            href="https://modelcontextprotocol.io"
            target="_blank"
            rel="noreferrer"
            className="text-blue-600 hover:underline"
          >
            Model Context Protocol
          </a>{' '}
          server so AI agents and MCP clients can interact with it programmatically.
        </p>
      </div>

      <div>
        <label htmlFor="mcpDescription" className="block text-sm font-medium text-gray-700 mb-1">
          Guide Description for MCP Clients
        </label>
        <textarea
          id="mcpDescription"
          value={mcpDescription}
          onChange={(e) => setMcpDescription(e.target.value)}
          rows={4}
          maxLength={2000}
          className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500 resize-y"
          placeholder={
            'Describe what this guide does and how MCP clients should interact with it.\n\nExample: This guide assists with software architecture design. It has access to code execution and diagram tools. Provide requirements or paste existing code for review. Supports multi-turn conversations for iterative design work.'
          }
        />
        <div className="flex justify-between mt-1">
          <p className="text-xs text-gray-500">
            Shown as the tool description for the guide assistant in{' '}
            <code className="bg-gray-100 px-1 rounded">tools/list</code>.
          </p>
          <span className="text-xs text-gray-400">{mcpDescription.length}/2000</span>
        </div>
      </div>

      <PublishedGuideApiKeySection
        context="mcp"
        hasApiKey={hasApiKey}
        sessionApiKey={sessionApiKey}
        guideId={guideId}
        publishedGuideId={publishedGuideId}
        authWebhookUrl={authWebhookUrl}
        onApiKeyChange={onApiKeyChange}
        onSessionApiKeyChange={onSessionApiKeyChange}
      />

      {!publishedGuideId && (
        <p className="text-xs text-gray-500 italic">
          Publish the guide first using the button below — this dialog will stay open so you can continue MCP setup
          here.
        </p>
      )}

      {publishedGuideId && !hasApiKey && (
        <div className="p-4 bg-amber-50 border border-amber-200 rounded-lg text-sm text-amber-800">
          MCP requires an API key. Generate one above, or use the quick setup below.
        </div>
      )}

      {showQuickSetup && (
        <div className="space-y-3">
          <button
            type="button"
            onClick={handleEnableMcpAccess}
            disabled={isEnabling}
            className="w-full px-4 py-3 text-sm font-medium text-white bg-purple-600 rounded-lg hover:bg-purple-700 disabled:opacity-50 flex items-center justify-center gap-2"
          >
            {isEnabling ? (
              <>
                <svg className="animate-spin h-4 w-4" fill="none" viewBox="0 0 24 24">
                  <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                  <path
                    className="opacity-75"
                    fill="currentColor"
                    d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"
                  />
                </svg>
                Setting up MCP access...
              </>
            ) : (
              <>
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    strokeWidth={2}
                    d="M15 7a2 2 0 012 2m4 0a6 6 0 01-7.743 5.743L11 17H9v2H7v2H4a1 1 0 01-1-1v-2.586a1 1 0 01.293-.707l5.964-5.964A6 6 0 1121 9z"
                  />
                </svg>
                {hasApiKey ? 'Enable MCP Access' : 'Generate API Key & Enable MCP'}
              </>
            )}
          </button>
          <p className="text-xs text-gray-500">
            Quick setup generates a key (if needed), enables the MCP endpoint, and saves in one step. You can also
            manage the key above and toggle MCP below, then save changes.
          </p>
          {enableError && (
            <div className="p-3 bg-red-50 border border-red-200 rounded-lg text-sm text-red-800">{enableError}</div>
          )}
        </div>
      )}

      {publishedGuideId && (
        <div className="flex items-center justify-between py-3 border-y border-gray-100">
          <div>
            <span className="block text-sm font-medium text-gray-700">Enable MCP Endpoint</span>
            <span className="block text-xs text-gray-500">
              Allow MCP clients to connect using the API key for authentication
            </span>
          </div>
          <label className={`relative inline-flex items-center ${hasApiKey ? 'cursor-pointer' : 'cursor-not-allowed opacity-50'}`}>
            <input
              type="checkbox"
              checked={mcpEnabled}
              disabled={!hasApiKey}
              onChange={(e) => setMcpEnabled(e.target.checked)}
              className="sr-only peer"
            />
            <div className="w-11 h-6 bg-gray-200 peer-focus:outline-none peer-focus:ring-4 peer-focus:ring-purple-300 rounded-full peer peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-5 after:w-5 after:transition-all peer-checked:bg-purple-600 peer-disabled:opacity-60"></div>
          </label>
        </div>
      )}

      {mcpEnabled && mcpEndpointUrl && (
        <div className="grid grid-cols-1 xl:grid-cols-2 gap-4">
          <div className="space-y-4">
            <div className="p-4 bg-purple-50 border border-purple-200 rounded-lg">
              <p className="text-xs font-medium text-purple-800 mb-2">Endpoint URL</p>
              <div className="flex items-center gap-2">
                <code className="flex-1 px-3 py-2 bg-white border border-purple-300 rounded font-mono text-xs text-gray-900 select-all break-all">
                  {mcpEndpointUrl}
                </code>
                <button
                  type="button"
                  onClick={copyEndpoint}
                  className="px-3 py-2 text-xs font-medium text-purple-700 bg-white border border-purple-300 rounded hover:bg-purple-50 flex-shrink-0"
                >
                  {copied ? 'Copied!' : 'Copy'}
                </button>
              </div>
            </div>

            <div className="border border-gray-200 rounded-lg p-4">
              <h4 className="text-sm font-medium text-gray-900 mb-3">Connection Details</h4>
              <div className="space-y-2 text-sm">
                <div className="flex items-start gap-2">
                  <span className="text-gray-500 flex-shrink-0 w-20">Transport:</span>
                  <span className="text-gray-900">HTTP (Streamable HTTP), stateless</span>
                </div>
                <div className="flex items-start gap-2">
                  <span className="text-gray-500 flex-shrink-0 w-20">Method:</span>
                  <span className="text-gray-900 font-mono text-xs">POST</span>
                </div>
                <div className="flex items-start gap-2">
                  <span className="text-gray-500 flex-shrink-0 w-20">Auth header:</span>
                  <code className="text-gray-900 bg-gray-100 px-1.5 py-0.5 rounded text-xs break-all">
                    x-guideants-apikey: gak_...
                  </code>
                </div>
              </div>
            </div>
          </div>

          <div className="space-y-4">
            <div className="border border-gray-200 rounded-lg p-4">
              <h4 className="text-sm font-medium text-gray-900 mb-3">MCP Tools</h4>
              <div className="space-y-3 text-sm text-gray-600">
                <p>
                  <code className="text-purple-700 bg-purple-50 px-1.5 py-0.5 rounded text-xs font-medium">
                    tools/list
                  </code>{' '}
                  returns one tool per addressable assistant — the guide plus each crew member — using each
                  assistant&apos;s name and description.
                </p>
                <p>
                  Each assistant tool accepts{' '}
                  <code className="bg-gray-100 px-1 rounded text-xs">instructions</code> and an optional{' '}
                  <code className="bg-gray-100 px-1 rounded text-xs">conversationId</code> to continue on a shared
                  thread (you can switch assistants between turns).
                </p>
                <p>
                  <code className="text-purple-700 bg-purple-50 px-1.5 py-0.5 rounded text-xs font-medium">
                    conversation_get
                  </code>{' '}
                  retrieves conversation history. Client-side tool execution is not supported over MCP.
                </p>
              </div>
            </div>

            {onDownloadClaudeSkill && (
              <div className="border border-gray-200 rounded-lg p-4 space-y-3">
                <div>
                  <h4 className="text-sm font-medium text-gray-900">Agent Skill</h4>
                  <p className="text-sm text-gray-600 mt-1">
                    Download a self-contained{' '}
                    <a
                      href="https://code.claude.com/docs/en/skills"
                      target="_blank"
                      rel="noreferrer"
                      className="text-blue-600 hover:underline"
                    >
                      Agent Skill
                    </a>{' '}
                    pack with a cross-platform Python client (no curl or bash). Unzip into{' '}
                    <code className="bg-gray-100 px-1 rounded text-xs">~/.claude/skills/</code> for Claude Code or{' '}
                    <code className="bg-gray-100 px-1 rounded text-xs">~/.cursor/skills/</code> for Cursor.
                  </p>
                  <p className="text-xs text-gray-500 mt-2">
                    Requires Python 3.8+. Files the guide produces are saved to the agent&apos;s working directory
                    (or a path you pass via <code className="bg-gray-100 px-1 rounded">--save-dir</code>).
                  </p>
                </div>
                <button
                  type="button"
                  onClick={handleDownloadClaudeSkill}
                  disabled={isDownloadingSkill || !canDownloadSkill}
                  className="w-full px-4 py-2.5 text-sm font-medium text-white bg-indigo-600 rounded-lg hover:bg-indigo-700 disabled:opacity-50 flex items-center justify-center gap-2"
                >
                  {isDownloadingSkill ? 'Preparing download...' : 'Download Agent Skill'}
                </button>
                {downloadSkillWarning && (
                  <div className="p-3 bg-amber-50 border border-amber-200 rounded-lg text-xs text-amber-800">
                    {downloadSkillWarning}
                  </div>
                )}
                {downloadSkillError && (
                  <div className="p-3 bg-red-50 border border-red-200 rounded-lg text-xs text-red-800">
                    {downloadSkillError}
                  </div>
                )}
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
