import { useState, useEffect } from 'react';
import { FaEdit, FaTrash, FaPlus, FaChevronRight, FaFile } from 'react-icons/fa';
import { CustomToolDto, OpenApiOperation } from '../../../types/guides';
import { OperationEditor } from './OperationEditor';
import { api } from '../../../services/api';
import { ConfirmationDialog } from '../../common/ConfirmationDialog';

interface OpenApiSchemasProps {
  customTools: CustomToolDto[];
  onCustomToolsChange: (tools: CustomToolDto[]) => void;
  onValidationChange?: (hasErrors: boolean) => void;
  onDirtyChange?: () => void;
}

interface OpenApiTool {
  operationId: string;
  method: string;
  path: string;
  summary?: string;
  description?: string;
}

export function OpenApiSchemas({
  customTools,
  onCustomToolsChange,
  onValidationChange,
  onDirtyChange,
}: OpenApiSchemasProps) {
  const [expandedIndex, setExpandedIndex] = useState<number | null>(null);
  const [activeTab, setActiveTab] = useState<{ [key: number]: 'tools' | 'schema' | 'auth' }>({});
  const [editingOperation, setEditingOperation] = useState<{ schemaIndex: number; operation: OpenApiOperation } | null>(null);
  const [deleteSchemaDialog, setDeleteSchemaDialog] = useState<{ isOpen: boolean; schemaIndex: number | null }>({ isOpen: false, schemaIndex: null });
  const [deleteOperationDialog, setDeleteOperationDialog] = useState<{ isOpen: boolean; schemaIndex: number | null; operation: OpenApiOperation | null }>({ isOpen: false, schemaIndex: null, operation: null });
  const [jsonErrors, setJsonErrors] = useState<{ [key: number]: string }>({});

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

  // Extract server URL from OpenAPI specification
  const extractServerUrl = (spec: string): string | null => {
    try {
      const parsed = JSON.parse(spec);
      
      // OpenAPI 3.x format
      if (parsed.servers && Array.isArray(parsed.servers) && parsed.servers.length > 0) {
        return parsed.servers[0].url;
      }
      
      // OpenAPI 2.x (Swagger) format
      if (parsed.host) {
        const scheme = parsed.schemes?.[0] || 'https';
        return `${scheme}://${parsed.host}`;
      }
      
      return null;
    } catch (error) {
      return null;
    }
  };

  // Extract API host from server URL
  const extractHostFromUrl = (url: string): string | null => {
    try {
      const urlObj = new URL(url);
      return urlObj.host;
    } catch (error) {
      return null;
    }
  };

  // Extract tools (operations) from OpenAPI specification
  const extractTools = (spec: string): OpenApiTool[] => {
    try {
      const parsed = JSON.parse(spec);
      const tools: OpenApiTool[] = [];
      
      if (parsed.paths && typeof parsed.paths === 'object') {
        for (const [path, pathItem] of Object.entries(parsed.paths)) {
          if (typeof pathItem === 'object' && pathItem !== null) {
            const methods = ['get', 'post', 'put', 'patch', 'delete', 'options', 'head'];
            
            for (const method of methods) {
              const operation = (pathItem as any)[method];
              if (operation && typeof operation === 'object') {
                tools.push({
                  operationId: operation.operationId || `${method}_${path.replace(/\//g, '_')}`,
                  method: method.toUpperCase(),
                  path,
                  summary: operation.summary,
                  description: operation.description,
                });
              }
            }
          }
        }
      }
      
      return tools;
    } catch (error) {
      return [];
    }
  };

  // Validate API host uniqueness
  const isApiHostUnique = (host: string, currentIndex: number): boolean => {
    return !customTools.some((tool, idx) => 
      idx !== currentIndex && tool.apiHost === host
    );
  };

  const handleAddNew = () => {
    const defaultSpec = '{\n  "openapi": "3.0.0",\n  "info": {\n    "title": "My API",\n    "version": "1.0.0"\n  },\n  "servers": [\n    {\n      "url": "https://api.example.com"\n    }\n  ],\n  "paths": {}\n}';
    const defaultApiHost = 'api.example.com';
    const existingNames = new Set(customTools.map(t => t.name));
    const baseName = defaultApiHost;
    let name = baseName;
    let counter = 2;
    while (existingNames.has(name)) {
      name = `${baseName} ${counter++}`;
    }
    const newTool: CustomToolDto = {
      name,
      openApiSpec: defaultSpec,
      apiHost: defaultApiHost,
    };
    onCustomToolsChange([...customTools, newTool]);
    setExpandedIndex(customTools.length);
  };

  // Validate OpenAPI specification
  const validateOpenApiSpec = (spec: string): string | null => {
    try {
      const parsed = JSON.parse(spec);
      
      // Check if it's an OpenAPI spec (version 2.x or 3.x)
      if (!parsed.openapi && !parsed.swagger) {
        return 'Missing OpenAPI version field. Spec must have either "openapi" (3.x) or "swagger" (2.x) property.';
      }
      
      // Check for required info object
      if (!parsed.info || typeof parsed.info !== 'object') {
        return 'Missing required "info" object. OpenAPI specs must include title and version.';
      }
      
      if (!parsed.info.title) {
        return 'Missing required "info.title" field.';
      }

      
      // Check for servers (OpenAPI 3.x) or host (Swagger 2.x)
      const hasOpenApi3Server = parsed.servers && Array.isArray(parsed.servers) && parsed.servers.length > 0 && parsed.servers[0].url;
      const hasSwagger2Host = parsed.host;
      
      if (!hasOpenApi3Server && !hasSwagger2Host) {
        return 'Missing server URL. Add a "servers" array with a URL (OpenAPI 3.x) or "host" field (Swagger 2.x).';
      }
      
      // Validate that all operations have operationId
      if (parsed.paths && typeof parsed.paths === 'object') {
        const missingOperationIds: string[] = [];
        
        for (const [path, pathItem] of Object.entries(parsed.paths)) {
          if (typeof pathItem === 'object' && pathItem !== null) {
            const methods = ['get', 'post', 'put', 'patch', 'delete', 'options', 'head'];
            
            for (const method of methods) {
              const operation = (pathItem as any)[method];
              if (operation && typeof operation === 'object') {
                if (!operation.operationId || typeof operation.operationId !== 'string' || operation.operationId.trim() === '') {
                  missingOperationIds.push(`${method.toUpperCase()} ${path}`);
                }
              }
            }
          }
        }
        
        if (missingOperationIds.length > 0) {
          const operations = missingOperationIds.slice(0, 3).join(', ');
          const more = missingOperationIds.length > 3 ? ` and ${missingOperationIds.length - 3} more` : '';
          return `Missing operationId for: ${operations}${more}. Every operation must have a unique operationId.`;
        }
      }
      
      return null; // Valid
    } catch (error) {
      return error instanceof Error ? error.message : 'Invalid JSON format';
    }
  };

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
          const host = extractHostFromUrl(serverUrl);
          if (host) {
            updatedTools[index].apiHost = host;
            updatedTools[index].name = host;
          }
        }
      }
    }
    
    onCustomToolsChange(updatedTools);
  };

  const handleServerUrlUpdate = (index: number, serverUrl: string) => {
    const updatedTools = [...customTools];
    
    try {
      // Extract host from URL and use it as the schema name
      const host = extractHostFromUrl(serverUrl);
      if (host) {
        updatedTools[index].apiHost = host;
        updatedTools[index].name = host;
      }
      
      // Update the schema
      const parsed = JSON.parse(updatedTools[index].openApiSpec);
      
      // Update OpenAPI 3.x format
      if (parsed.openapi) {
        if (parsed.servers && Array.isArray(parsed.servers) && parsed.servers.length > 0) {
          parsed.servers[0].url = serverUrl;
        } else {
          parsed.servers = [{ url: serverUrl }];
        }
      } else if (parsed.swagger) {
        // OpenAPI 2.x (Swagger) format
        const urlObj = new URL(serverUrl);
        parsed.host = urlObj.host;
        parsed.schemes = [urlObj.protocol.replace(':', '')];
        if (urlObj.pathname && urlObj.pathname !== '/') {
          parsed.basePath = urlObj.pathname;
        }
      }
      
      updatedTools[index].openApiSpec = JSON.stringify(parsed, null, 2);
      onCustomToolsChange(updatedTools);
    } catch (error) {
      console.error('Failed to update server URL:', error);
    }
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

  const handleEditOperation = (schemaIndex: number, operation: OpenApiOperation) => {
    setEditingOperation({ schemaIndex, operation });
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
      let pathCounter = 1;
      let newPath = '/new-endpoint';
      while (parsed.paths[newPath]) {
        pathCounter++;
        newPath = `/new-endpoint-${pathCounter}`;
      }
      
      const newMethod = 'get';
      const newOperationId = `newOperation_${Date.now()}`;
      
      if (!parsed.paths[newPath]) {
        parsed.paths[newPath] = {};
      }
      
      const operation = {
        operationId: newOperationId,
        summary: 'New operation',
        description: 'Add description here',
        parameters: [],
        responses: {
          '200': {
            description: 'Successful response',
            content: {
              'application/json': {
                schema: {
                  type: 'object',
                  properties: {}
                }
              }
            }
          }
        }
      };
      
      parsed.paths[newPath][newMethod] = operation;
      
      schema.openApiSpec = JSON.stringify(parsed, null, 2);
      onCustomToolsChange(updatedTools);
      
      // Mark the parent form as dirty
      onDirtyChange?.();
      
      // Create a temporary operation object for editing (without ID = new operation)
      const tempOperation: OpenApiOperation = {
        id: '', // Empty ID indicates this is a new operation
        operationId: newOperationId,
        method: newMethod.toUpperCase(),
        path: newPath,
        summary: operation.summary,
        schemaFragment: JSON.stringify({
          path: newPath,
          method: newMethod,
          operation: operation
        }, null, 2),
        toolDefinition: ''
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
      {/* Operation Editor Modal */}
      {editingOperation && (
        <OperationEditor
          operation={editingOperation.operation}
          onClose={() => setEditingOperation(null)}
          onSave={handleSaveOperation}
        />
      )}

      {/* Delete Schema Confirmation Dialog */}
      <ConfirmationDialog
        isOpen={deleteSchemaDialog.isOpen}
        onClose={() => setDeleteSchemaDialog({ isOpen: false, schemaIndex: null })}
        onConfirm={confirmRemoveSchema}
        title="Delete OpenAPI Schema"
        message="Are you sure you want to remove this OpenAPI schema? This will delete all operations and cannot be undone."
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
          <h3 className="text-sm font-medium text-gray-700">OpenAPI Schemas</h3>
          <p className="text-xs text-gray-500 mt-1">
            Configure OpenAPI specifications for custom web connectors. Each API host must be unique.
          </p>
        </div>
        <button
          type="button"
          onClick={handleAddNew}
          className="flex items-center gap-2 px-4 py-2 text-sm text-blue-600 border border-blue-300 rounded-md hover:bg-blue-50 transition-colors"
          data-tour-id="guide.tools.openapi.addSchema"
        >
          <FaPlus className="w-4 h-4" />
          Add OpenAPI Schema
        </button>
      </div>

      {customTools.length > 0 && (
        <div className="space-y-3">
          {customTools.map((tool, index) => {
            const isExpanded = expandedIndex === index;
            const hasAuth = !!tool.authConfig;
            const hostConflict = tool.apiHost && !isApiHostUnique(tool.apiHost, index);
            const hasJsonError = !!jsonErrors[index];

            return (
              <div
                key={index}
                className={`border rounded-lg ${
                  hostConflict || hasJsonError ? 'border-red-400 bg-red-50' : 'border-gray-300 bg-white'
                }`}
              >
                {/* Header */}
                <div className="flex items-center justify-between p-3">
                  <div className="flex items-center gap-3 flex-1">
                    <button
                      type="button"
                      onClick={() => setExpandedIndex(isExpanded ? null : index)}
                      className="text-gray-500 hover:text-gray-700 transition-transform"
                    >
                      <FaChevronRight className={`w-4 h-4 transition-transform ${isExpanded ? 'rotate-90' : ''}`} />
                    </button>
                    <div className="flex-1">
              <div className="flex items-center gap-2">
                <span className="text-sm font-medium text-gray-900">{tool.name}</span>
                        {hasAuth && (
                          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-green-100 text-green-800">
                            Auth Configured
                          </span>
                        )}
                        {hostConflict && (
                          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-red-100 text-red-800">
                            ⚠️ Duplicate Host
                          </span>
                        )}
                        {hasJsonError && (
                          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-red-100 text-red-800">
                            ⚠️ Invalid Schema
                          </span>
                        )}
                      </div>
                      <div className="text-xs text-gray-500 mt-0.5">
                        {tool.apiHost || 'No host detected'}
                      </div>
                    </div>
              </div>
                  <div className="flex items-center gap-2">
                    <button
                      type="button"
                      onClick={() => handleRemove(index)}
                      className="text-red-600 hover:text-red-700 p-1"
                      title="Remove schema"
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
                   <div className="border-t border-gray-200 bg-gray-50">
                     {/* Tabs for different sections - Tools first as most important */}
                     <div className="flex border-b border-gray-300 bg-gray-100">
                       <button
                         type="button"
                         onClick={() => setActiveTab({ ...activeTab, [index]: 'tools' })}
                         className={`px-4 py-2 text-sm font-medium ${
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
                         onClick={() => setActiveTab({ ...activeTab, [index]: 'schema' })}
                         className={`px-4 py-2 text-sm font-medium ${
                           currentTab === 'schema'
                             ? 'text-blue-600 border-b-2 border-blue-600 bg-white'
                             : 'text-gray-600 hover:text-gray-900'
                         }`}
                       >
                         Schema
                       </button>
                       <button
                         type="button"
                         onClick={() => setActiveTab({ ...activeTab, [index]: 'auth' })}
                         className={`px-4 py-2 text-sm font-medium ${
                           currentTab === 'auth'
                             ? 'text-blue-600 border-b-2 border-blue-600 bg-white'
                             : 'text-gray-600 hover:text-gray-900'
                         }`}
                       >
                         Authentication
                         {hasAuth && (
                           <span className="ml-2 inline-flex items-center px-1.5 py-0.5 rounded text-xs font-medium bg-green-100 text-green-800">
                             ✓
                           </span>
                         )}
                       </button>
                     </div>

                     <div className="p-4 space-y-4">
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
                                           ⚠️ Save the guide to make this operation editable
                                         </p>
                                       )}
                                    </div>
                                    {/* Edit and Delete buttons for individual operations */}
                                    {hasId && (
                                      <div className="flex items-center gap-1">
                                        <button
                                          type="button"
                                          onClick={() => handleEditOperation(index, tool as OpenApiOperation)}
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
                                    )}
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
                              className={`w-full px-3 py-2 text-sm border rounded-md font-mono ${hostConflict ? 'border-red-400 text-red-700' : 'border-gray-300'}`}
                              data-tour-id="guide.tools.openapi.serverUrl"
                             />
                             {hostConflict && (
                               <p className="text-xs text-red-600 mt-1">
                                 This host ({tool.apiHost}) is already used by another connector. Each API host must be unique.
                               </p>
                             )}
                             <p className="text-xs text-gray-500 mt-1">
                               The API host ({tool.apiHost || 'not detected'}) is automatically derived from this URL
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
                              <div className="mt-2 p-2 bg-red-50 border border-red-200 rounded-md">
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
                          {hasAuth ? 'Remove Auth' : 'Add Auth'}
              </button>
            </div>

                      {hasAuth && tool.authConfig && (
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
          <p className="mt-2 text-sm text-gray-600">No OpenAPI schemas configured</p>
          <p className="text-xs text-gray-500">Click "Add OpenAPI Schema" to create a new web connector</p>
        </div>
      )}
    </div>
    </>
  );
}

