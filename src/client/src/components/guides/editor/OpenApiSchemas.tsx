import { useState, useEffect, useId, useRef } from 'react';
import { FaEdit, FaTrash, FaPlus, FaChevronRight, FaFile } from 'react-icons/fa';
import { CustomToolDto, OpenApiOperation, EnvironmentVariableDto } from '../../../types/guides';
import { api } from '../../../services/api';
import { ConfirmationDialog } from '../../common/ConfirmationDialog';
import {
  extractServerUrl,
  extractTools,
  updateServerUrlInSpec,
  type OpenApiTool,
} from './toolSources/openApiToolSource';
import {
  classifyToolSourceFromSpec,
  extractConnectorKeyFromServerUrl,
  type ToolSourceKind,
} from './toolSources/toolSourceClassification';
import { McpConnectionPanel } from './toolSources/McpConnectionPanel';
import { validateOpenApiSpec } from './toolSources/validation';
import {
  buildToolSourceCardViewModel,
  sourceKindBadgeClassName,
  statusChipClassName,
} from './toolSources/toolSourceCardViewModel';
import { AddToolSourcePicker } from './toolSources/AddToolSourcePicker';
import type { DraftSourceKind } from './toolSources/openApiDescriptorBuilder';
import {
  createDraftCustomTool,
  updateConnectorKeyInTool,
} from './toolSources/openApiDescriptorBuilder';
import { StructuredOperationEditor } from './toolSources/StructuredOperationEditor';
import { createEmptyToolDefinitionModel } from './toolSources/toolDefinitionModel';
import { buildFragmentJsonFromModel } from './toolSources/operationFragmentBuilder';

interface OpenApiSchemasProps {
  customTools: CustomToolDto[];
  environmentVariables: EnvironmentVariableDto[];
  onCustomToolsChange: (tools: CustomToolDto[]) => void;
  onEnvironmentVariablesChange: (variables: EnvironmentVariableDto[]) => void;
  onValidationChange?: (hasErrors: boolean) => void;
  onDirtyChange?: () => void;
}

export function OpenApiSchemas({
  customTools,
  environmentVariables,
  onCustomToolsChange,
  onEnvironmentVariablesChange,
  onValidationChange,
  onDirtyChange,
}: OpenApiSchemasProps) {
  const [expandedIndex, setExpandedIndex] = useState<number | null>(null);
  const [activeTab, setActiveTab] = useState<{ [key: number]: 'tools' | 'schema' | 'auth' }>({});
  const [editingOperation, setEditingOperation] = useState<{ schemaIndex: number; operation: OpenApiOperation } | null>(null);
  const [deleteSchemaDialog, setDeleteSchemaDialog] = useState<{ isOpen: boolean; schemaIndex: number | null }>({ isOpen: false, schemaIndex: null });
  const [deleteOperationDialog, setDeleteOperationDialog] = useState<{ isOpen: boolean; schemaIndex: number | null; operation: OpenApiOperation | null }>({ isOpen: false, schemaIndex: null, operation: null });
  const [jsonErrors, setJsonErrors] = useState<{ [key: number]: string }>({});
  const [pickerOpen, setPickerOpen] = useState(false);
  const [focusField, setFocusField] = useState<{ index: number; fieldId: string } | null>(null);
  const listIdPrefix = useId();
  const addButtonRef = useRef<HTMLButtonElement>(null);
  const connectionInputRefs = useRef<Record<string, HTMLInputElement | null>>({});

  // Notify parent of validation state changes
  useEffect(() => {
    const hasErrors = Object.keys(jsonErrors).length > 0;
    onValidationChange?.(hasErrors);
  }, [jsonErrors, onValidationChange]);

  // Auto-expand if there's only one connector
  useEffect(() => {
    if (customTools.length === 1 && expandedIndex === null) {
      setExpandedIndex(0);
    }
  }, [customTools.length, expandedIndex]);

  const handleAddFromPicker = (kind: DraftSourceKind) => {
    const { tool, focusFieldId } = createDraftCustomTool(kind, customTools);
    const newIndex = customTools.length;
    onCustomToolsChange([...customTools, tool]);
    setExpandedIndex(newIndex);
    setFocusField({ index: newIndex, fieldId: focusFieldId });
    setPickerOpen(false);
    addButtonRef.current?.focus();
  };

  useEffect(() => {
    if (!focusField) return;
    const key = `${focusField.index}-${focusField.fieldId}`;
    const input = connectionInputRefs.current[key];
    if (input) {
      input.focus();
      setFocusField(null);
    }
  }, [focusField, expandedIndex, customTools.length]);

  const handleUpdate = (index: number, updates: Partial<CustomToolDto>) => {
    const updatedTools = [...customTools];
    updatedTools[index] = { ...updatedTools[index], ...updates };
    
    // If spec changed, validate and re-extract server URL and API host
    if (updates.openApiSpec) {
      const validationError = validateOpenApiSpec(updates.openApiSpec);
      
      if (validationError) {
        // Invalid - set error
        setJsonErrors(prev => ({
          ...prev,
          [index]: validationError
        }));
      } else {
        // Valid - clear any error and extract server info
        setJsonErrors(prev => {
          const newErrors = { ...prev };
          delete newErrors[index];
          return newErrors;
        });
        
        const serverUrl = extractServerUrl(updates.openApiSpec);
        if (serverUrl) {
          const connectorKey = extractConnectorKeyFromServerUrl(serverUrl);
          if (connectorKey) {
            updatedTools[index].apiHost = connectorKey;
            updatedTools[index].name = connectorKey;
          }
        }
      }
    }
    
    onCustomToolsChange(updatedTools);
  };

  const handleServerUrlUpdate = (index: number, serverUrl: string) => {
    const updatedTools = [...customTools];

    try {
      const connectorKey = extractConnectorKeyFromServerUrl(serverUrl);
      if (connectorKey) {
        updatedTools[index].apiHost = connectorKey;
        updatedTools[index].name = connectorKey;
      }

      updatedTools[index].openApiSpec = updateServerUrlInSpec(
        updatedTools[index].openApiSpec,
        serverUrl
      );
      onCustomToolsChange(updatedTools);
    } catch (error) {
      console.error('Failed to update server URL:', error);
    }
  };

  const handleConnectorKeyUpdate = (index: number, sourceKind: ToolSourceKind, connectorKey: string) => {
    const updatedTools = [...customTools];
    updatedTools[index] = updateConnectorKeyInTool(updatedTools[index], sourceKind, connectorKey);
    onCustomToolsChange(updatedTools);
  };

  const sourceKindForTool = (tool: CustomToolDto): ToolSourceKind => {
    const serverUrl = extractServerUrl(tool.openApiSpec);
    return classifyToolSourceFromSpec(tool.openApiSpec, serverUrl);
  };

  const handleRemove = (index: number) => {
    setDeleteSchemaDialog({ isOpen: true, schemaIndex: index });
  };

  const confirmRemoveSchema = () => {
    if (deleteSchemaDialog.schemaIndex !== null) {
      const index = deleteSchemaDialog.schemaIndex;
      onCustomToolsChange(customTools.filter((_, i) => i !== index));
      if (expandedIndex === index) setExpandedIndex(null);
      
      // Clear validation error for removed schema
      setJsonErrors(prev => {
        const newErrors = { ...prev };
        delete newErrors[index];
        return newErrors;
      });
    }
    setDeleteSchemaDialog({ isOpen: false, schemaIndex: null });
  };

  const handleToggleAuth = (index: number) => {
    const tool = customTools[index];
    if (tool.authConfig) {
      // Remove auth
      handleUpdate(index, { authConfig: undefined });
    } else {
      // Add default auth
      handleUpdate(index, {
        authConfig: {
          authType: 'oauth',
          userConfigPolicy: 'none',
          scopes: [],
        },
      });
    }
  };

  const handleEditOperation = (schemaIndex: number, toolOrOp: OpenApiTool | OpenApiOperation) => {
    if ('id' in toolOrOp && toolOrOp.id) {
      setEditingOperation({ schemaIndex, operation: toolOrOp });
      return;
    }

    try {
      const schema = customTools[schemaIndex];
      const parsed = JSON.parse(schema.openApiSpec);
      const method = toolOrOp.method.toLowerCase();
      const path = toolOrOp.path;
      const operationObj = parsed.paths?.[path]?.[method];
      const schemaFragment = JSON.stringify(
        { path, method, operation: operationObj },
        null,
        2
      );
      const tempOperation: OpenApiOperation = {
        id: '',
        operationId: toolOrOp.operationId,
        method: toolOrOp.method,
        path: toolOrOp.path,
        summary: toolOrOp.summary,
        schemaFragment,
        toolDefinition: '',
      };
      setEditingOperation({ schemaIndex, operation: tempOperation });
    } catch (error) {
      console.error('Failed to open operation editor:', error);
    }
  };

  const handleSaveOperation = async (operationId: string, schemaFragment: string) => {
    if (!editingOperation) return;
    
    const updatedTools = [...customTools];
    const schema = updatedTools[editingOperation.schemaIndex];
    
    // Check if this is a new operation (empty ID) or existing operation
    const isNewOperation = editingOperation.operation.id === '';
    
    if (isNewOperation) {
      // For new operations, just update the schema JSON directly
      try {
        const fragment = JSON.parse(schemaFragment);
        const parsed = JSON.parse(schema.openApiSpec);
        
        // Find and remove old operation (by original operationId)
        const oldFragment = JSON.parse(editingOperation.operation.schemaFragment || '{}');
        if (parsed.paths?.[oldFragment.path]?.[oldFragment.method]) {
          delete parsed.paths[oldFragment.path][oldFragment.method];
          if (Object.keys(parsed.paths[oldFragment.path]).length === 0) {
            delete parsed.paths[oldFragment.path];
          }
        }
        
        // Add updated operation
        if (!parsed.paths) {
          parsed.paths = {};
        }
        if (!parsed.paths[fragment.path]) {
          parsed.paths[fragment.path] = {};
        }
        parsed.paths[fragment.path][fragment.method] = fragment.operation;
        
        schema.openApiSpec = JSON.stringify(parsed, null, 2);
        onCustomToolsChange(updatedTools);
        onDirtyChange?.();
      } catch (error) {
        console.error('Failed to update new operation in schema:', error);
        throw error;
      }
    } else {
      // For existing operations, call the API
      const updatedOperation = await api.guides.operations.update(operationId, {
        schemaFragmentJson: schemaFragment,
      });

      // Update the operation in the customTools array AND sync back to schema
      if (schema.operations) {
        const opIndex = schema.operations.findIndex(op => op.id === operationId);
        if (opIndex !== -1) {
          schema.operations[opIndex] = updatedOperation;
          
          // Sync operation change back to the OpenAPI schema JSON
          syncOperationToSchema(updatedTools, editingOperation.schemaIndex, updatedOperation);
          
          // Notify parent that changes were made
          onCustomToolsChange(updatedTools);
          
          // Mark the parent form as dirty so it knows to save the schema
          onDirtyChange?.();
        }
      }
    }
  };

  // Sync an operation back to the OpenAPI schema JSON
  const syncOperationToSchema = (tools: CustomToolDto[], schemaIndex: number, operation: OpenApiOperation) => {
    try {
      const schema = tools[schemaIndex];
      const parsed = JSON.parse(schema.openApiSpec);
      const fragment = JSON.parse(operation.schemaFragment || '{}');
      
      // Ensure paths object exists
      if (!parsed.paths) {
        parsed.paths = {};
      }
      
      // Update or create the path and method
      if (!parsed.paths[fragment.path]) {
        parsed.paths[fragment.path] = {};
      }
      parsed.paths[fragment.path][fragment.method] = fragment.operation;
      
      schema.openApiSpec = JSON.stringify(parsed, null, 2);
    } catch (error) {
      console.error('Failed to sync operation to schema:', error);
    }
  };

  // Add a new operation to a schema
  const handleAddOperation = (schemaIndex: number) => {
    const updatedTools = [...customTools];
    const schema = updatedTools[schemaIndex];
    
    try {
      const parsed = JSON.parse(schema.openApiSpec);
      
      // Ensure paths object exists
      if (!parsed.paths) {
        parsed.paths = {};
      }
      
      // Generate a unique path to avoid conflicts
      const sourceKind = sourceKindForTool(schema);
      const emptyModel = createEmptyToolDefinitionModel(sourceKind);
      let fragment = JSON.parse(buildFragmentJsonFromModel(emptyModel));
      let newPath = fragment.path as string;
      let pathCounter = 1;
      while (parsed.paths[newPath]) {
        pathCounter++;
        if (sourceKind === 'client-actions') {
          newPath = `Bridge.NewAction${pathCounter > 1 ? pathCounter : ''}`;
        } else if (sourceKind === 'sandbox-module') {
          newPath = `/new_function${pathCounter > 1 ? `_${pathCounter}` : ''}`;
        } else {
          newPath = `/new-endpoint${pathCounter > 1 ? `-${pathCounter}` : ''}`;
        }
        emptyModel.execution.path = newPath;
        if (sourceKind === 'sandbox-module') {
          emptyModel.execution.sandboxFunctionName = newPath.replace(/^\//, '');
          emptyModel.operationId = emptyModel.execution.sandboxFunctionName;
        }
        fragment = JSON.parse(buildFragmentJsonFromModel(emptyModel));
      }
      const newMethod = fragment.method;
      const operation = fragment.operation;

      if (!parsed.paths[newPath]) {
        parsed.paths[newPath] = {};
      }

      parsed.paths[newPath][newMethod] = operation;

      schema.openApiSpec = JSON.stringify(parsed, null, 2);
      onCustomToolsChange(updatedTools);

      onDirtyChange?.();

      const tempOperation: OpenApiOperation = {
        id: '',
        operationId: operation.operationId,
        method: newMethod.toUpperCase(),
        path: newPath,
        summary: operation.summary,
        schemaFragment: buildFragmentJsonFromModel(emptyModel),
        toolDefinition: '',
      };
      
      // Open the editor for immediate editing
      setEditingOperation({ schemaIndex, operation: tempOperation });
    } catch (error) {
      console.error('Failed to add operation:', error);
    }
  };

  // Delete an operation from a schema
  const handleDeleteOperation = (schemaIndex: number, operation: OpenApiOperation) => {
    setDeleteOperationDialog({ isOpen: true, schemaIndex, operation });
  };

  const confirmDeleteOperation = () => {
    if (deleteOperationDialog.schemaIndex === null || !deleteOperationDialog.operation) {
      return;
    }

    const schemaIndex = deleteOperationDialog.schemaIndex;
    const operation = deleteOperationDialog.operation;
    const updatedTools = [...customTools];
    const schema = updatedTools[schemaIndex];
    
    try {
      const parsed = JSON.parse(schema.openApiSpec);
      const fragment = JSON.parse(operation.schemaFragment || '{}');
      
      // Remove the operation from the schema
      if (parsed.paths && parsed.paths[fragment.path]) {
        delete parsed.paths[fragment.path][fragment.method];
        
        // If the path has no more operations, remove the path entirely
        if (Object.keys(parsed.paths[fragment.path]).length === 0) {
          delete parsed.paths[fragment.path];
        }
      }
      
      schema.openApiSpec = JSON.stringify(parsed, null, 2);
      
      // Remove from operations array
      if (schema.operations) {
        schema.operations = schema.operations.filter(op => op.id !== operation.id);
      }
      
      onCustomToolsChange(updatedTools);
      
      // Mark the parent form as dirty
      onDirtyChange?.();
    } catch (error) {
      console.error('Failed to delete operation:', error);
    }

    setDeleteOperationDialog({ isOpen: false, schemaIndex: null, operation: null });
  };

  return (
    <>
      <AddToolSourcePicker
        isOpen={pickerOpen}
        onClose={() => {
          setPickerOpen(false);
          addButtonRef.current?.focus();
        }}
        onSelect={handleAddFromPicker}
      />

      {editingOperation && (
        <StructuredOperationEditor
          operation={editingOperation.operation}
          openApiSpec={customTools[editingOperation.schemaIndex]?.openApiSpec ?? '{}'}
          sourceKind={sourceKindForTool(customTools[editingOperation.schemaIndex])}
          onClose={() => setEditingOperation(null)}
          onSave={handleSaveOperation}
        />
      )}

      {/* Delete Schema Confirmation Dialog */}
      <ConfirmationDialog
        isOpen={deleteSchemaDialog.isOpen}
        onClose={() => setDeleteSchemaDialog({ isOpen: false, schemaIndex: null })}
        onConfirm={confirmRemoveSchema}
        title="Delete Tool Source"
        message="Are you sure you want to remove this tool source? This will delete all operations and cannot be undone."
        confirmText="Delete"
        cancelText="Cancel"
        confirmButtonClass="bg-red-600 hover:bg-red-700 text-white"
      />

      {/* Delete Operation Confirmation Dialog */}
      <ConfirmationDialog
        isOpen={deleteOperationDialog.isOpen}
        onClose={() => setDeleteOperationDialog({ isOpen: false, schemaIndex: null, operation: null })}
        onConfirm={confirmDeleteOperation}
        title="Delete Operation"
        message={`Are you sure you want to delete the operation "${deleteOperationDialog.operation?.operationId}"? This cannot be undone.`}
        confirmText="Delete"
        cancelText="Cancel"
        confirmButtonClass="bg-red-600 hover:bg-red-700 text-white"
      />
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <h3 className="text-sm font-medium text-gray-700">Tool Sources</h3>
          <p className="text-xs text-gray-500 mt-1">
            Configure tool sources for web APIs, client actions, sandbox modules, and more. Each connector key must be unique.
          </p>
        </div>
        <button
          ref={addButtonRef}
          type="button"
          onClick={() => setPickerOpen(true)}
          className="flex items-center gap-2 px-4 py-2 text-sm text-blue-600 border border-blue-300 rounded-md hover:bg-blue-50 transition-colors focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          data-tour-id="guide.tools.openapi.addSchema"
        >
          <FaPlus className="w-4 h-4" />
          Add Tool Source
        </button>
      </div>

      {customTools.length > 0 && (
        <div className="space-y-3">
          {customTools.map((tool, index) => {
            const isExpanded = expandedIndex === index;
            const cardVm = buildToolSourceCardViewModel(tool, customTools, index);
            const hasJsonError = !!jsonErrors[index];
            const panelId = `${listIdPrefix}-panel-${index}`;
            const hasCardError = hasJsonError || cardVm.status === 'invalid-json' || cardVm.status === 'needs-attention';

            return (
              <div
                key={index}
                className={`border rounded-lg ${
                  hasCardError ? 'border-red-400 bg-red-50' : 'border-gray-300 bg-white'
                }`}
              >
                {/* Header */}
                <div className="flex items-center justify-between p-3">
                  <div className="flex items-center gap-3 flex-1">
                    <button
                      type="button"
                      onClick={() => setExpandedIndex(isExpanded ? null : index)}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter' || e.key === ' ') {
                          e.preventDefault();
                          setExpandedIndex(isExpanded ? null : index);
                        }
                      }}
                      aria-expanded={isExpanded}
                      aria-controls={panelId}
                      className="text-gray-500 hover:text-gray-700 transition-transform focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 rounded"
                    >
                      <FaChevronRight className={`w-4 h-4 transition-transform ${isExpanded ? 'rotate-90' : ''}`} />
                    </button>
                    <div className="flex-1">
                      <div className="flex items-center gap-2 flex-wrap">
                        <span className="text-sm font-medium text-gray-900">{tool.name}</span>
                        <span
                          className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${sourceKindBadgeClassName(cardVm.sourceKind)}`}
                        >
                          {cardVm.sourceKindLabel}
                        </span>
                        <span
                          className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${statusChipClassName(cardVm.status)}`}
                        >
                          {cardVm.statusLabel}
                        </span>
                        {cardVm.hasAuth && (
                          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-green-100 text-green-800">
                            Auth configured
                          </span>
                        )}
                      </div>
                      <div className="text-xs text-gray-500 mt-0.5">
                        {cardVm.connectorKeyLabel}: {cardVm.connectorKeyValue}
                        {' · '}
                        {cardVm.operationCount} operation{cardVm.operationCount === 1 ? '' : 's'}
                      </div>
                    </div>
                  </div>
                  <div className="flex items-center gap-2">
                    <button
                      type="button"
                      onClick={() => handleRemove(index)}
                      className="text-red-600 hover:text-red-700 p-1 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 rounded"
                      title="Remove tool source"
                    >
                      <FaTrash className="w-4 h-4" />
                    </button>
                  </div>
                </div>

                 {/* Expanded Content */}
                 {isExpanded && (() => {
                   // Always extract tools from the schema to show newly added operations
                   const extractedTools = extractTools(tool.openApiSpec);
                   
                   // If we have server operations with IDs, merge them to add ID info
                   let tools: (OpenApiTool | OpenApiOperation)[];
                   if (tool.operations && tool.operations.length > 0) {
                     // Match extracted tools with server operations by operationId to add IDs
                     tools = extractedTools.map(extracted => {
                       const serverOp = tool.operations?.find(op => op.operationId === extracted.operationId);
                       return serverOp || extracted;
                     });
                   } else {
                     tools = extractedTools;
                   }
                   
                   const currentTab = activeTab[index] || 'tools';
                   
                   return (
                   <div id={panelId} className="border-t border-gray-200 bg-gray-50">
                     {/* Scheme-aware connection editor */}
                     <div className="p-4 border-b border-gray-200 bg-white space-y-3">
                       {cardVm.sourceKind === 'web-api' && (
                         <div>
                           <label className="block text-xs font-medium text-gray-700 mb-1">
                             Server URL <span className="text-red-500">*</span>
                           </label>
                           <input
                             ref={(el) => { connectionInputRefs.current[`${index}-server-url`] = el; }}
                             type="text"
                             value={extractServerUrl(tool.openApiSpec) || ''}
                             onChange={(e) => handleServerUrlUpdate(index, e.target.value)}
                             placeholder="https://api.example.com"
                             className={`w-full px-3 py-2 text-sm border rounded-md font-mono ${cardVm.connectorKeyConflict ? 'border-red-400' : 'border-gray-300'}`}
                             data-tour-id="guide.tools.openapi.serverUrl"
                           />
                           {cardVm.hasAuth && (
                             <p className="text-xs text-green-700 mt-1">Auth configured — see Authentication tab.</p>
                           )}
                         </div>
                       )}
                       {cardVm.sourceKind === 'mcp-connection' && (
                         <McpConnectionPanel
                           tool={tool}
                           environmentVariables={environmentVariables}
                           onEnvironmentVariablesChange={onEnvironmentVariablesChange}
                           onUpdate={(updates) => handleUpdate(index, updates)}
                           onDirty={onDirtyChange}
                           inputRef={(el) => { connectionInputRefs.current[`${index}-mcp-bridge-id`] = el; }}
                         />
                       )}
                       {cardVm.sourceKind === 'client-actions' && (
                         <div>
                           <label className="block text-xs font-medium text-gray-700 mb-1">
                             Client bridge id <span className="text-red-500">*</span>
                           </label>
                           <input
                             ref={(el) => { connectionInputRefs.current[`${index}-client-bridge-id`] = el; }}
                             type="text"
                             value={tool.apiHost || ''}
                             onChange={(e) => handleConnectorKeyUpdate(index, 'client-actions', e.target.value)}
                             placeholder="my-client-bridge"
                             className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md font-mono"
                           />
                           <p className="text-xs text-purple-700 mt-1">Handled by the client application.</p>
                         </div>
                       )}
                       {cardVm.sourceKind === 'sandbox-module' && (
                         <div>
                           <label className="block text-xs font-medium text-gray-700 mb-1">
                             Init module <span className="text-red-500">*</span>
                           </label>
                           <input
                             ref={(el) => { connectionInputRefs.current[`${index}-init-module`] = el; }}
                             type="text"
                             value={tool.apiHost || ''}
                             onChange={(e) => handleConnectorKeyUpdate(index, 'sandbox-module', e.target.value)}
                             placeholder="__init__.py"
                             className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md font-mono"
                           />
                           <p className="text-xs text-gray-500 mt-1">Python functions are entered manually per operation.</p>
                         </div>
                       )}
                       {cardVm.sourceKind === 'local-function' && (
                         <div>
                           <label className="block text-xs font-medium text-gray-700 mb-1">Local tool host</label>
                           <input
                             ref={(el) => { connectionInputRefs.current[`${index}-local-target`] = el; }}
                             type="text"
                             value={tool.apiHost || 'localhost'}
                             disabled
                             className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md font-mono bg-gray-50"
                           />
                           <p className="text-xs text-gray-500 mt-1">Configure method paths per operation.</p>
                         </div>
                       )}
                       {cardVm.connectorKeyConflict && (
                         <p className="text-xs text-red-600">
                           This {cardVm.connectorKeyLabel.toLowerCase()} is already used by another tool source.
                         </p>
                       )}
                     </div>

                     <div className="flex border-b border-gray-300 bg-gray-100" role="tablist" aria-label={`Tool source ${tool.name} sections`}>
                       <button
                         type="button"
                         role="tab"
                         aria-selected={currentTab === 'tools'}
                         onClick={() => setActiveTab({ ...activeTab, [index]: 'tools' })}
                         className={`px-4 py-2 text-sm font-medium focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ${
                           currentTab === 'tools'
                             ? 'text-blue-600 border-b-2 border-blue-600 bg-white'
                             : 'text-gray-600 hover:text-gray-900'
                         }`}
                       >
                         Tools
                         <span className="ml-2 inline-flex items-center px-1.5 py-0.5 rounded text-xs font-medium bg-blue-100 text-blue-800">
                           {tools.length}
                         </span>
                       </button>
                       <button
                         type="button"
                         role="tab"
                         aria-selected={currentTab === 'schema'}
                         onClick={() => setActiveTab({ ...activeTab, [index]: 'schema' })}
                         className={`px-4 py-2 text-sm font-medium focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ${
                           currentTab === 'schema'
                             ? 'text-blue-600 border-b-2 border-blue-600 bg-white'
                             : 'text-gray-600 hover:text-gray-900'
                         }`}
                       >
                         Advanced JSON
                       </button>
                       <button
                         type="button"
                         role="tab"
                         aria-selected={currentTab === 'auth'}
                         onClick={() => setActiveTab({ ...activeTab, [index]: 'auth' })}
                         className={`px-4 py-2 text-sm font-medium focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ${
                           currentTab === 'auth'
                             ? 'text-blue-600 border-b-2 border-blue-600 bg-white'
                             : 'text-gray-600 hover:text-gray-900'
                         }`}
                       >
                         Authentication
                         {cardVm.hasAuth && (
                           <span className="ml-2 inline-flex items-center px-1.5 py-0.5 rounded text-xs font-medium bg-green-100 text-green-800">
                             ✓
                           </span>
                         )}
                       </button>
                     </div>

                     <div className="p-4 space-y-4" role="tabpanel">
                       {/* Tools Tab */}
                       {currentTab === 'tools' && (
                        <div>
                          <div className="flex items-center justify-between mb-3">
                            <div>
                              <h3 className="text-sm font-medium text-gray-900">Tools</h3>
                              <p className="text-xs text-gray-600 mt-1">
                                Operations discovered from the OpenAPI specification. Each operation becomes a tool.
                              </p>
                            </div>
                            <button
                              type="button"
                              onClick={() => handleAddOperation(index)}
                              className="flex items-center gap-2 px-3 py-1.5 text-xs font-medium text-blue-600 bg-blue-50 hover:bg-blue-100 rounded-md transition-colors"
                              data-tour-id="guide.tools.openapi.addOperation"
                            >
                              <FaPlus className="w-3 h-3" />
                              Add Operation
                            </button>
                          </div>
 
                           {tools.length === 0 ? (
                             <div className="text-center py-12 bg-white border-2 border-dashed border-gray-300 rounded-lg">
                               <p className="text-sm text-gray-600 font-medium">No tools found</p>
                               <p className="text-xs text-gray-500 mt-1">
                                 Add paths and operations to your OpenAPI schema to create tools
                               </p>
                             </div>
                           ) : (
                             <div className="space-y-2">
                               {tools.map((tool, toolIndex) => {
                                 const hasId = 'id' in tool;
                                 return (
                                 <div
                                   key={toolIndex}
                                   className={`bg-white border rounded-lg p-4 hover:border-gray-300 transition-colors ${
                                     !hasId ? 'border-amber-300 bg-amber-50' : 'border-gray-200'
                                   }`}
                                 >
                                   <div className="flex items-start justify-between">
                                     <div className="flex-1">
                                       <div className="flex items-center gap-2 mb-1">
                                         <span className={`inline-flex px-2 py-1 text-xs font-semibold rounded ${
                                           tool.method === 'GET' ? 'bg-blue-100 text-blue-800' :
                                           tool.method === 'POST' ? 'bg-green-100 text-green-800' :
                                           tool.method === 'PUT' ? 'bg-yellow-100 text-yellow-800' :
                                           tool.method === 'PATCH' ? 'bg-orange-100 text-orange-800' :
                                           tool.method === 'DELETE' ? 'bg-red-100 text-red-800' :
                                           'bg-gray-100 text-gray-800'
                                         }`}>
                                           {tool.method}
                                         </span>
                                         <code className="text-sm font-mono text-gray-700">{tool.path}</code>
                                         {!hasId && (
                                           <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-amber-100 text-amber-800">
                                             Unsaved
                                           </span>
                                         )}
                                       </div>
                                       <h4 className="text-sm font-medium text-gray-900 mb-1">
                                         {tool.operationId}
                                       </h4>
                                       {tool.summary && (
                                         <p className="text-xs text-gray-600">{tool.summary}</p>
                                       )}
                                       {'description' in tool && tool.description && (
                                         <p className="text-xs text-gray-500 mt-1">{tool.description}</p>
                                       )}
                                       {!hasId && (
                                         <p className="text-xs text-amber-700 mt-2 font-medium">
                                           Unsaved in database — save the guide to persist operation records.
                                         </p>
                                       )}
                                    </div>
                                    {/* Edit and Delete buttons for individual operations */}
                                    <div className="flex items-center gap-1">
                                        <button
                                          type="button"
                                          onClick={() => handleEditOperation(index, tool)}
                                          className="p-2 text-gray-600 hover:text-blue-600 hover:bg-blue-50 rounded-md transition-colors"
                                          title="Edit operation"
                                        >
                                          <FaEdit className="w-4 h-4" />
                                        </button>
                                        <button
                                          type="button"
                                          onClick={() => handleDeleteOperation(index, tool as OpenApiOperation)}
                                          className="p-2 text-gray-600 hover:text-red-600 hover:bg-red-50 rounded-md transition-colors"
                                          title="Delete operation"
                                        >
                                          <FaTrash className="w-4 h-4" />
                                        </button>
                                      </div>
                                  </div>
                                 </div>
                               )})}
                             </div>
                           )}
                         </div>
                       )}
 
                       {/* Schema Tab */}
                       {currentTab === 'schema' && (
                         <>
                           {/* Server URL */}
                           <div>
                             <label className="block text-xs font-medium text-gray-700 mb-1">
                               Server URL
                               <span className="text-gray-500 font-normal ml-1">(updates schema)</span>
                             </label>
                            <input
                              type="text"
                              value={extractServerUrl(tool.openApiSpec) || ''}
                              onChange={(e) => handleServerUrlUpdate(index, e.target.value)}
                              placeholder="https://api.example.com"
                              className={`w-full px-3 py-2 text-sm border rounded-md font-mono ${cardVm.connectorKeyConflict ? 'border-red-400 text-red-700' : 'border-gray-300'}`}
                              data-tour-id="guide.tools.openapi.serverUrl"
                             />
                             {cardVm.connectorKeyConflict && (
                               <p className="text-xs text-red-600 mt-1">
                                 This {cardVm.connectorKeyLabel.toLowerCase()} ({tool.apiHost}) is already used by another tool source. Each connector key must be unique.
                               </p>
                             )}
                             <p className="text-xs text-gray-500 mt-1">
                               The {cardVm.connectorKeyLabel.toLowerCase()} ({cardVm.connectorKeyValue}) is automatically derived from this URL
                             </p>
                           </div>
 
                           {/* OpenAPI Specification */}
                          <div>
                            <label className="block text-xs font-medium text-gray-700 mb-1">
                              OpenAPI Specification (JSON)
                            </label>
                            <textarea
                              value={tool.openApiSpec}
                              onChange={(e) => handleUpdate(index, { openApiSpec: e.target.value })}
                              rows={12}
                              className={`w-full px-3 py-2 text-xs font-mono border rounded-md ${
                                hasJsonError ? 'border-red-400 bg-red-50' : 'border-gray-300'
                              }`}
                              placeholder="Paste OpenAPI JSON specification here..."
                              data-tour-id="guide.tools.openapi.schemaEditor"
                            />
                            {hasJsonError ? (
                              <div className="mt-2 p-2 bg-red-50 border border-red-200 rounded-md" aria-live="polite">
                                <div className="flex items-start gap-2">
                                  <span className="text-red-600 font-semibold text-xs">⚠️</span>
                                  <div className="flex-1">
                                    <p className="text-xs font-semibold text-red-800">Invalid OpenAPI Specification</p>
                                    <p className="text-xs text-red-700 mt-1">{jsonErrors[index]}</p>
                                  </div>
                                </div>
                              </div>
                            ) : (
                              <p className="text-xs text-gray-500 mt-1">
                                Editing the schema directly will update the tools list above
                              </p>
                            )}
                          </div>
                         </>
                       )}

                       {/* Authentication Tab */}
                       {currentTab === 'auth' && (
                         <div>
                          <div className="flex items-center justify-between mb-3" data-tour-id="guide.tools.openapi.auth.header">
                        <div>
                          <h4 className="text-sm font-medium text-gray-900">Authentication</h4>
                          <p className="text-xs text-gray-500">Optional authentication configuration for this API</p>
              </div>
              <button
                type="button"
                          onClick={() => handleToggleAuth(index)}
                          className="px-3 py-1 text-xs font-medium text-blue-600 border border-blue-300 rounded-md hover:bg-blue-50"
                          data-tour-id="guide.tools.openapi.toggleAuth"
                        >
                          {cardVm.hasAuth ? 'Remove Auth' : 'Add Auth'}
              </button>
            </div>

                      {cardVm.hasAuth && tool.authConfig && (
                        <div className="space-y-3 bg-white p-3 rounded-md border border-gray-200">
                          {/* Auth Type */}
                          <div>
                            <label className="block text-xs font-medium text-gray-700 mb-1">
                              Authentication Type
                            </label>
                            <select
                              value={tool.authConfig.authType}
                              onChange={(e) =>
                                handleUpdate(index, {
                                  authConfig: {
                                    ...tool.authConfig!,
                                    authType: e.target.value as 'oauth' | 'service_http',
                                  },
                                })
                              }
                              className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md"
                            >
                              <option value="oauth">OAuth 2.0</option>
                              <option value="service_http">Service HTTP Header (for API keys, use this with X-API-Key, Authorization, etc.)</option>
                              <option value="service_query">Service Query Parameter (API key in URL query)</option>
                            </select>
                          </div>

                          {/* OAuth-specific fields */}
                          {tool.authConfig.authType === 'oauth' && (
                            <>
                              <div>
                                <label className="block text-xs font-medium text-gray-700 mb-1">
                                  Client ID
                                </label>
                                <input
                                  type="text"
                                  value={tool.authConfig.clientId || ''}
                                  onChange={(e) =>
                                    handleUpdate(index, {
                                      authConfig: { ...tool.authConfig!, clientId: e.target.value },
                                  })
                                }
                                className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md"
                              />
                            </div>
                            <div>
                              <label className="block text-xs font-medium text-gray-700 mb-1">
                                Tenant (optional)
                              </label>
                              <input
                                type="text"
                                value={tool.authConfig.tenant || ''}
                                onChange={(e) =>
                                  handleUpdate(index, {
                                    authConfig: { ...tool.authConfig!, tenant: e.target.value },
                                  })
                                }
                                className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md"
                              />
                            </div>
                            <div>
                              <label className="block text-xs font-medium text-gray-700 mb-1">
                                Scopes (comma-separated)
                              </label>
                              <input
                                type="text"
                                value={tool.authConfig.scopes?.join(', ') || ''}
                                onChange={(e) =>
                                  handleUpdate(index, {
                                    authConfig: {
                                      ...tool.authConfig!,
                                      scopes: e.target.value.split(',').map(s => s.trim()).filter(s => s),
                                    },
                                  })
                                }
                                placeholder="user.read, mail.send"
                                className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md"
                                />
                              </div>
                            </>
                          )}

                          {/* Service HTTP fields (includes API key authentication) */}
                          {tool.authConfig.authType === 'service_http' && (
                            <>
                              <div>
                                <label className="block text-xs font-medium text-gray-700 mb-1">
                                  Header Name <span className="text-red-500">*</span>
                                </label>
                                <input
                                  type="text"
                                  required
                                  value={tool.authConfig.headerName || ''}
                                  onChange={(e) =>
                                    handleUpdate(index, {
                                      authConfig: { ...tool.authConfig!, headerName: e.target.value },
                                    })
                                  }
                                  placeholder="Authorization, X-API-Key, etc."
                                  className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md"
                                />
                              </div>
                              <div>
                                <label className="block text-xs font-medium text-gray-700 mb-1">
                                  Secret Value <span className="text-red-500">*</span>
                                </label>
                                <input
                                  type="password"
                                  required
                                  value={tool.authConfig.valueTemplate || ''}
                                  onChange={(e) =>
                                    handleUpdate(index, {
                                      authConfig: { ...tool.authConfig!, valueTemplate: e.target.value },
                                    })
                                  }
                                  placeholder="Enter API key or token value"
                                  className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md"
                                />
                                <p className="mt-1 text-xs text-gray-500">
                                  This value is write-only and will be stored securely. Use "Bearer YOUR_VALUE" format if needed.
                                </p>
                              </div>
                            </>
                          )}

                          {/* Service QUERY fields (API key in query string) */}
                          {tool.authConfig.authType === 'service_query' && (
                            <>
                              <div>
                                <label className="block text-xs font-medium text-gray-700 mb-1">
                                  Query Parameter Name <span className="text-red-500">*</span>
                                </label>
                                <input
                                  type="text"
                                  required
                                  value={tool.authConfig.headerName || ''}
                                  onChange={(e) =>
                                    handleUpdate(index, {
                                      authConfig: { ...tool.authConfig!, headerName: e.target.value },
                                    })
                                  }
                                  placeholder="access_key, api_key, key, etc."
                                  className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md"
                                />
                              </div>
                              <div>
                                <label className="block text-xs font-medium text-gray-700 mb-1">
                                  Secret Value <span className="text-red-500">*</span>
                                </label>
                                <input
                                  type="password"
                                  required
                                  value={tool.authConfig.valueTemplate || ''}
                                  onChange={(e) =>
                                    handleUpdate(index, {
                                      authConfig: { ...tool.authConfig!, valueTemplate: e.target.value },
                                    })
                                  }
                                  placeholder="Enter API key value"
                                  className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md"
                                />
                                <p className="mt-1 text-xs text-gray-500">
                                  This value is write-only and will be stored securely. It will be sent as a query parameter.
                                </p>
                              </div>
                            </>
                          )}
                        </div>
                      )}
                         </div>
                       )}
                     </div>
                  </div>
                  );
                })()}
              </div>
            );
          })}
        </div>
      )}

      {customTools.length === 0 && (
        <div className="text-center py-8 border-2 border-dashed border-gray-300 rounded-lg">
          <FaFile className="mx-auto h-12 w-12 text-gray-400" />
          <p className="mt-2 text-sm text-gray-600">No tool sources configured</p>
          <p className="text-xs text-gray-500">Click &quot;Add Tool Source&quot; to connect a web API, client actions, sandbox module, or other source</p>
        </div>
      )}
    </div>
    </>
  );
}

