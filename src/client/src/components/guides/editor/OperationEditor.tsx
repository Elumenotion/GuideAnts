import { useState, useEffect } from 'react';
import { FaTimes, FaSave, FaEye } from 'react-icons/fa';
import { OpenApiOperation } from '../../../types/guides';
import { api } from '../../../services/api';

interface OperationEditorProps {
  operation: OpenApiOperation;
  onClose: () => void;
  onSave: (operationId: string, schemaFragment: string) => Promise<void>;
}

export function OperationEditor({ operation, onClose, onSave }: OperationEditorProps) {
  const [schemaFragment, setSchemaFragment] = useState(operation.schemaFragment || '');
  const [toolDefinition, setToolDefinition] = useState(operation.toolDefinition || '');
  const [saving, setSaving] = useState(false);
  const [previewing, setPreviewing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [activeView, setActiveView] = useState<'schema' | 'tool'>('schema');
  const [jsonError, setJsonError] = useState<string | null>(null);
  const isNewOperation = operation.id === '';

  // Initialize with operation data
  useEffect(() => {
    setSchemaFragment(operation.schemaFragment || '');
    setToolDefinition(operation.toolDefinition || '');
  }, [operation]);

  // Validate OpenAPI operation fragment in real-time
  useEffect(() => {
    if (!schemaFragment.trim()) {
      setJsonError('Schema fragment is required');
      return;
    }

    try {
      const parsed = JSON.parse(schemaFragment);
      
      // Validate it's a proper OpenAPI operation fragment
      // It should have the structure: { "path": "...", "method": "...", "operation": {...} }
      if (!parsed.path || typeof parsed.path !== 'string') {
        setJsonError('Schema fragment must have a "path" field (string)');
        return;
      }

      if (!parsed.method || typeof parsed.method !== 'string') {
        setJsonError('Schema fragment must have a "method" field (string)');
        return;
      }

      const validMethods = ['get', 'post', 'put', 'patch', 'delete', 'options', 'head'];
      if (!validMethods.includes(parsed.method.toLowerCase())) {
        setJsonError(`Invalid HTTP method. Must be one of: ${validMethods.join(', ')}`);
        return;
      }

      if (!parsed.operation || typeof parsed.operation !== 'object') {
        setJsonError('Schema fragment must have an "operation" field (object)');
        return;
      }

      if (!parsed.operation.operationId || typeof parsed.operation.operationId !== 'string') {
        setJsonError('Operation must have an "operationId" field (string)');
        return;
      }

      // Optional but recommended fields
      if (parsed.operation.summary && typeof parsed.operation.summary !== 'string') {
        setJsonError('If provided, "summary" must be a string');
        return;
      }

      if (parsed.operation.description && typeof parsed.operation.description !== 'string') {
        setJsonError('If provided, "description" must be a string');
        return;
      }

      setJsonError(null);
    } catch (err) {
      if (err instanceof Error) {
        setJsonError(`Invalid JSON: ${err.message}`);
      } else {
        setJsonError('Invalid JSON syntax');
      }
    }
  }, [schemaFragment]);

  const handlePreview = async () => {
    if (jsonError) {
      setError('Please fix JSON errors before previewing');
      return;
    }

    setPreviewing(true);
    setError(null);

    try {
      const data = await api.guides.operations.preview({
        schemaFragmentJson: schemaFragment,
        openApiSpecJson: '{}', // Not used in preview
      });

      setToolDefinition(data.toolDefinition);
      setActiveView('tool'); // Switch to tool definition view
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to preview');
      console.error('Preview error:', err);
    } finally {
      setPreviewing(false);
    }
  };

  const handleSave = async () => {
    if (jsonError) {
      setError('Please fix JSON errors before saving');
      return;
    }

    setSaving(true);
    setError(null);

    try {
      await onSave(operation.id, schemaFragment);
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save');
      console.error('Save error:', err);
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 overflow-y-auto" data-tour-id="guide.tools.openapi.operationEditor.modal">
      <div className="flex min-h-screen items-center justify-center p-4">
        {/* Backdrop */}
        <div 
          className="fixed inset-0 bg-black bg-opacity-50 transition-opacity"
          onClick={onClose}
        />

        {/* Modal */}
        <div className="relative bg-white rounded-lg shadow-xl max-w-6xl w-full max-h-[90vh] flex flex-col">
          {/* Header */}
          <div className="flex items-center justify-between p-6 border-b border-gray-200" data-tour-id="guide.tools.openapi.operationEditor.header">
            <div>
              <h2 className="text-xl font-semibold text-gray-900">
                {isNewOperation ? 'Create New Operation' : 'Edit Operation'}
              </h2>
              <div className="flex items-center gap-2 mt-1">
                <span className={`inline-flex px-2 py-1 text-xs font-semibold rounded ${
                  operation.method === 'GET' ? 'bg-blue-100 text-blue-800' :
                  operation.method === 'POST' ? 'bg-green-100 text-green-800' :
                  operation.method === 'PUT' ? 'bg-yellow-100 text-yellow-800' :
                  operation.method === 'PATCH' ? 'bg-orange-100 text-orange-800' :
                  operation.method === 'DELETE' ? 'bg-red-100 text-red-800' :
                  'bg-gray-100 text-gray-800'
                }`}>
                  {operation.method}
                </span>
                <code className="text-sm font-mono text-gray-700">{operation.path}</code>
                {isNewOperation && (
                  <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-amber-100 text-amber-800">
                    New - Save guide to persist
                  </span>
                )}
              </div>
              <p className="text-sm text-gray-600 mt-1">{operation.operationId}</p>
            </div>
            <button
              onClick={onClose}
              className="text-gray-400 hover:text-gray-600 p-2 rounded-lg hover:bg-gray-100"
              data-tour-id="guide.tools.openapi.operationEditor.close"
            >
              <FaTimes className="w-5 h-5" />
            </button>
          </div>

          {/* Tabs */}
          <div className="flex border-b border-gray-200 px-6">
            <button
              onClick={() => setActiveView('schema')}
              className={`px-4 py-3 text-sm font-medium border-b-2 -mb-px ${
                activeView === 'schema'
                  ? 'border-blue-600 text-blue-600'
                  : 'border-transparent text-gray-600 hover:text-gray-900 hover:border-gray-300'
              }`}
              data-tour-id="guide.tools.openapi.operationEditor.tab.schema"
            >
              OpenAPI Schema Fragment
            </button>
            <button
              onClick={() => setActiveView('tool')}
              className={`px-4 py-3 text-sm font-medium border-b-2 -mb-px ${
                activeView === 'tool'
                  ? 'border-blue-600 text-blue-600'
                  : 'border-transparent text-gray-600 hover:text-gray-900 hover:border-gray-300'
              }`}
              data-tour-id="guide.tools.openapi.operationEditor.tab.tool"
            >
              Tool Definition
            </button>
          </div>

          {/* Content */}
          <div className="flex-1 overflow-auto p-6">
            {error && (
              <div className="mb-4 p-4 bg-red-50 border border-red-200 rounded-lg">
                <p className="text-sm text-red-800">{error}</p>
              </div>
            )}

            {activeView === 'schema' ? (
              <div className="space-y-4">
                <div>
                  <div className="flex items-center justify-between mb-2">
                    <label className="block text-sm font-medium text-gray-700">
                      Schema Fragment
                    </label>
                    {!isNewOperation && (
                      <button
                        onClick={handlePreview}
                        disabled={previewing || !!jsonError}
                        className="inline-flex items-center gap-2 px-3 py-1.5 text-sm font-medium text-blue-700 bg-blue-50 border border-blue-200 rounded-md hover:bg-blue-100 disabled:opacity-50 disabled:cursor-not-allowed"
                        title={jsonError ? 'Fix JSON errors before previewing' : undefined}
                        data-tour-id="guide.tools.openapi.operationEditor.preview"
                      >
                        <FaEye className="w-4 h-4" />
                        {previewing ? 'Previewing...' : 'Preview Tool Definition'}
                      </button>
                    )}
                  </div>
                  <p className="text-xs text-gray-600 mb-2">
                    {isNewOperation 
                      ? 'Define the OpenAPI operation schema. This operation will be created when you save the guide.'
                      : 'Edit the OpenAPI operation schema. The tool definition will be automatically regenerated when you save.'}
                  </p>
                  {isNewOperation && (
                    <div className="mb-2 p-2 bg-amber-50 border border-amber-200 rounded-md">
                      <p className="text-xs text-amber-800">
                        💡 This is a new operation. Changes are saved to the schema only. Save the guide to persist this operation to the database.
                      </p>
                    </div>
                  )}
                  <textarea
                    value={schemaFragment}
                    onChange={(e) => setSchemaFragment(e.target.value)}
                    className={`w-full h-[500px] px-3 py-2 font-mono text-sm border rounded-md focus:ring-2 ${
                      jsonError
                        ? 'border-red-400 bg-red-50 focus:ring-red-500 focus:border-transparent'
                        : 'border-gray-300 focus:ring-blue-500 focus:border-transparent'
                    }`}
                    spellCheck={false}
                    data-tour-id="guide.tools.openapi.operationEditor.schema"
                  />
                  {jsonError && (
                    <div className="mt-2 p-3 bg-red-50 border border-red-200 rounded-md">
                      <p className="text-sm font-medium text-red-800">Invalid OpenAPI Operation Fragment</p>
                      <p className="text-xs text-red-700 mt-1">{jsonError}</p>
                      <p className="text-xs text-gray-600 mt-2">
                        Expected structure: <code className="bg-white px-1 rounded">{'{ "path": "/example", "method": "get", "operation": { "operationId": "...", ... } }'}</code>
                      </p>
                    </div>
                  )}
                </div>
              </div>
            ) : (
              <div className="space-y-4">
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-2">
                    Generated Tool Definition
                  </label>
                  <p className="text-xs text-gray-600 mb-2">
                    This is the tool definition that will be sent to the LLM. It's generated from the schema fragment.
                  </p>
                  <div className="relative">
                    <pre className="w-full h-[500px] overflow-auto px-3 py-2 font-mono text-sm bg-gray-50 border border-gray-300 rounded-md">
                      {toolDefinition ? JSON.stringify(JSON.parse(toolDefinition), null, 2) : 'No tool definition available. Save the schema fragment to generate one.'}
                    </pre>
                  </div>
                </div>
              </div>
            )}
          </div>

          {/* Footer */}
          <div className="flex items-center justify-end gap-3 p-6 border-t border-gray-200 bg-gray-50">
            <button
              onClick={onClose}
              className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50"
            >
              Cancel
            </button>
            <button
              onClick={handleSave}
              disabled={saving || !!jsonError}
              className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-white bg-blue-600 border border-transparent rounded-md hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed"
              title={jsonError ? 'Fix JSON errors before saving' : undefined}
              data-tour-id="guide.tools.openapi.operationEditor.save"
            >
              <FaSave className="w-4 h-4" />
              {saving ? 'Saving...' : (isNewOperation ? 'Save to Schema' : 'Save Changes')}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

