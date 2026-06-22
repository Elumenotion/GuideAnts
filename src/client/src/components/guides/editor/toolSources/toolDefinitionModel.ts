import type { ToolSourceKind } from './toolSourceClassification';

export type ParameterScalarType = 'string' | 'number' | 'integer' | 'boolean';
export type ParameterType = ParameterScalarType | 'array' | 'object';

export interface NestedPropertyModel {
  name: string;
  type: ParameterScalarType;
  description?: string;
  required: boolean;
  default?: string;
  example?: string;
  enumValues?: string[];
}

export interface ParameterPropertyModel {
  name: string;
  type: ParameterType;
  description?: string;
  required: boolean;
  default?: string;
  example?: string;
  enumValues?: string[];
  /** For type=array: item scalar type or object with one-level properties */
  itemType?: ParameterType;
  itemProperties?: NestedPropertyModel[];
  itemRequired?: string[];
  /** For type=object: one level of nested properties (D6) */
  objectProperties?: NestedPropertyModel[];
  objectRequired?: string[];
}

export type ResponseSchemaMode = 'none' | 'object' | 'array' | 'raw';

export interface ResponseSchemaModel {
  mode: ResponseSchemaMode;
  properties?: NestedPropertyModel[];
  required?: string[];
  itemType?: ParameterScalarType;
  rawJson?: string;
}

export interface ExecutionMappingModel {
  method: string;
  path: string;
  /** Sandbox: Python function name (manual-first per D5) */
  sandboxFunctionName?: string;
  /** Client: action key path */
  clientActionKey?: string;
}

export interface ToolDefinitionModel {
  operationId: string;
  summary?: string;
  description?: string;
  contentType: string;
  /** User-facing / model-facing parameters */
  parameters: ParameterPropertyModel[];
  /** Hidden/default-injected parameters (D7 separate section) */
  injectedParameters: ParameterPropertyModel[];
  execution: ExecutionMappingModel;
  response: ResponseSchemaModel;
}

export function createEmptyToolDefinitionModel(sourceKind: ToolSourceKind): ToolDefinitionModel {
  const base: ToolDefinitionModel = {
    operationId: 'newOperation',
    summary: '',
    description: '',
    contentType: 'application/json',
    parameters: [],
    injectedParameters: [],
    execution: {
      method: 'post',
      path: '/new-endpoint',
    },
    response: { mode: 'none' },
  };

  switch (sourceKind) {
    case 'client-actions':
      return {
        ...base,
        operationId: 'NewAction',
        execution: { method: 'post', path: 'Bridge.NewAction', clientActionKey: 'Bridge.NewAction' },
      };
    case 'sandbox-module':
      return {
        ...base,
        operationId: 'new_function',
        execution: {
          method: 'post',
          path: '/new_function',
          sandboxFunctionName: 'new_function',
        },
      };
    case 'local-function':
      return {
        ...base,
        operationId: 'Invoke',
        execution: { method: 'post', path: 'Namespace.Type.Method' },
      };
    case 'mcp-connection':
      return {
        ...base,
        operationId: 'mcp_tool',
        execution: { method: 'post', path: '/tools/example' },
      };
    case 'web-api':
    default:
      return {
        ...base,
        execution: { method: 'get', path: '/new-endpoint' },
      };
  }
}

export function isInjectedParameter(param: Pick<ParameterPropertyModel, 'default' | 'enumValues'>): boolean {
  const hasDefault = param.default !== undefined && param.default !== '';
  const hasSingleEnum = !!param.enumValues && param.enumValues.length === 1;
  return hasDefault || hasSingleEnum;
}

export function partitionParameters(
  all: ParameterPropertyModel[]
): { visible: ParameterPropertyModel[]; injected: ParameterPropertyModel[] } {
  const visible: ParameterPropertyModel[] = [];
  const injected: ParameterPropertyModel[] = [];
  for (const p of all) {
    if (isInjectedParameter(p)) {
      injected.push(p);
    } else {
      visible.push(p);
    }
  }
  return { visible, injected };
}

export function mergeParameters(
  visible: ParameterPropertyModel[],
  injected: ParameterPropertyModel[]
): ParameterPropertyModel[] {
  return [...visible, ...injected];
}
