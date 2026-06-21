import type { ToolSourceKind } from './toolSourceClassification';
import {
  createEmptyToolDefinitionModel,
  isInjectedParameter,
  mergeParameters,
  partitionParameters,
  type NestedPropertyModel,
  type ParameterPropertyModel,
  type ParameterScalarType,
  type ParameterType,
  type ResponseSchemaModel,
  type ToolDefinitionModel,
} from './toolDefinitionModel';

export interface OperationFragment {
  path: string;
  method: string;
  operation: Record<string, unknown>;
}

export interface ParseFragmentResult {
  model: ToolDefinitionModel;
  isCustomMode: boolean;
  customReason?: string;
}

const SCALAR_TYPES: ParameterScalarType[] = ['string', 'number', 'integer', 'boolean'];

function isScalarType(t: string): t is ParameterScalarType {
  return (SCALAR_TYPES as string[]).includes(t);
}

function parseScalarValue(raw: unknown): string | undefined {
  if (raw === undefined || raw === null) return undefined;
  if (typeof raw === 'string') return raw;
  return JSON.stringify(raw);
}

function parseEnumValues(schema: Record<string, unknown>): string[] | undefined {
  if (!Array.isArray(schema.enum)) return undefined;
  return schema.enum.map((v) => (typeof v === 'string' ? v : JSON.stringify(v)));
}

function parseNestedProperty(name: string, schema: Record<string, unknown>, requiredSet: Set<string>): NestedPropertyModel {
  const type = typeof schema.type === 'string' && isScalarType(schema.type) ? schema.type : 'string';
  return {
    name,
    type,
    description: typeof schema.description === 'string' ? schema.description : undefined,
    required: requiredSet.has(name),
    default: parseScalarValue(schema.default),
    example: parseScalarValue(schema.example),
    enumValues: parseEnumValues(schema),
  };
}

function schemaHasUnsupportedFeatures(schema: unknown, depth = 0): string | null {
  if (!schema || typeof schema !== 'object' || Array.isArray(schema)) return null;
  const s = schema as Record<string, unknown>;

  if ('$ref' in s) return 'Schema uses $ref';
  if ('allOf' in s || 'oneOf' in s || 'anyOf' in s) return 'Schema uses composition keywords';
  if (depth > 1) return 'Schema nesting exceeds level-1 structured depth';

  if (s.type === 'object' && s.properties && typeof s.properties === 'object') {
    for (const prop of Object.values(s.properties as Record<string, unknown>)) {
      if (prop && typeof prop === 'object') {
        const p = prop as Record<string, unknown>;
        if (p.type === 'object' && p.properties) {
          const nested = schemaHasUnsupportedFeatures(p, depth + 1);
          if (nested) return nested;
        }
        if (p.type === 'array' && p.items) {
          const items = p.items as Record<string, unknown>;
          if (items.type === 'object' && items.properties) {
            const nested = schemaHasUnsupportedFeatures(items, depth + 1);
            if (nested) return nested;
          }
        }
      }
    }
  }

  if (s.type === 'array' && s.items) {
    const items = s.items as Record<string, unknown>;
    if (items.type === 'object' && items.properties) {
      for (const prop of Object.values(items.properties as Record<string, unknown>)) {
        if (prop && typeof prop === 'object') {
          const p = prop as Record<string, unknown>;
          if (p.type === 'object') return 'Array item object nesting exceeds level-1';
        }
      }
    }
  }

  return null;
}

function parsePropertySchema(
  name: string,
  schema: Record<string, unknown>,
  requiredSet: Set<string>
): ParameterPropertyModel {
  const rawType = typeof schema.type === 'string' ? schema.type : 'string';
  const type: ParameterType = rawType === 'array' || rawType === 'object' ? rawType : isScalarType(rawType) ? rawType : 'string';

  const param: ParameterPropertyModel = {
    name,
    type,
    description: typeof schema.description === 'string' ? schema.description : undefined,
    required: requiredSet.has(name),
    default: parseScalarValue(schema.default),
    example: parseScalarValue(schema.example),
    enumValues: parseEnumValues(schema),
  };

  if (type === 'array' && schema.items && typeof schema.items === 'object') {
    const items = schema.items as Record<string, unknown>;
    const itemType = typeof items.type === 'string' ? items.type : 'string';
    param.itemType = itemType === 'object' ? 'object' : isScalarType(itemType) ? itemType : 'string';

    if (param.itemType === 'object' && items.properties && typeof items.properties === 'object') {
      const itemRequired = new Set(
        Array.isArray(items.required) ? items.required.filter((r): r is string => typeof r === 'string') : []
      );
      param.itemProperties = Object.entries(items.properties as Record<string, unknown>).map(([n, s]) =>
        parseNestedProperty(n, s as Record<string, unknown>, itemRequired)
      );
      param.itemRequired = [...itemRequired];
    }
  }

  if (type === 'object' && schema.properties && typeof schema.properties === 'object') {
    const objRequired = new Set(
      Array.isArray(schema.required) ? schema.required.filter((r): r is string => typeof r === 'string') : []
    );
    param.objectProperties = Object.entries(schema.properties as Record<string, unknown>).map(([n, s]) =>
      parseNestedProperty(n, s as Record<string, unknown>, objRequired)
    );
    param.objectRequired = [...objRequired];
  }

  return param;
}

function buildNestedPropertySchema(prop: NestedPropertyModel): Record<string, unknown> {
  const schema: Record<string, unknown> = { type: prop.type };
  if (prop.description) schema.description = prop.description;
  if (prop.default !== undefined && prop.default !== '') {
    try {
      schema.default = JSON.parse(prop.default);
    } catch {
      schema.default = prop.default;
    }
  }
  if (prop.example !== undefined && prop.example !== '') {
    try {
      schema.example = JSON.parse(prop.example);
    } catch {
      schema.example = prop.example;
    }
  }
  if (prop.enumValues && prop.enumValues.length > 0) {
    schema.enum = prop.enumValues.map((v) => {
      try {
        return JSON.parse(v);
      } catch {
        return v;
      }
    });
  }
  return schema;
}

function buildPropertySchema(param: ParameterPropertyModel): Record<string, unknown> {
  const schema: Record<string, unknown> = { type: param.type };
  if (param.description) schema.description = param.description;
  if (param.default !== undefined && param.default !== '') {
    try {
      schema.default = JSON.parse(param.default);
    } catch {
      schema.default = param.default;
    }
  }
  if (param.example !== undefined && param.example !== '') {
    try {
      schema.example = JSON.parse(param.example);
    } catch {
      schema.example = param.example;
    }
  }
  if (param.enumValues && param.enumValues.length > 0) {
    schema.enum = param.enumValues.map((v) => {
      try {
        return JSON.parse(v);
      } catch {
        return v;
      }
    });
  }

  if (param.type === 'array') {
    const itemType = param.itemType ?? 'string';
    if (itemType === 'object' && param.itemProperties) {
      const props: Record<string, unknown> = {};
      const required: string[] = [];
      for (const p of param.itemProperties) {
        props[p.name] = buildNestedPropertySchema(p);
        if (p.required) required.push(p.name);
      }
      schema.items = {
        type: 'object',
        properties: props,
        ...(required.length > 0 ? { required } : {}),
        additionalProperties: false,
      };
    } else {
      schema.items = { type: itemType };
    }
  }

  if (param.type === 'object' && param.objectProperties) {
    const props: Record<string, unknown> = {};
    const required: string[] = [];
    for (const p of param.objectProperties) {
      props[p.name] = buildNestedPropertySchema(p);
      if (p.required) required.push(p.name);
    }
    schema.properties = props;
    if (required.length > 0) schema.required = required;
    schema.additionalProperties = false;
  }

  return schema;
}

function parseRequestBodyParameters(
  operation: Record<string, unknown>
): { params: ParameterPropertyModel[]; contentType: string } | null {
  const requestBody = operation.requestBody as Record<string, unknown> | undefined;
  if (!requestBody?.content || typeof requestBody.content !== 'object') {
    return { params: [], contentType: 'application/json' };
  }

  const content = requestBody.content as Record<string, unknown>;
  const mediaType = Object.keys(content)[0] ?? 'application/json';
  const media = content[mediaType] as Record<string, unknown> | undefined;
  if (!media?.schema || typeof media.schema !== 'object') {
    return { params: [], contentType: mediaType };
  }

  const schema = media.schema as Record<string, unknown>;
  const unsupported = schemaHasUnsupportedFeatures(schema);
  if (unsupported) return null;

  if (schema.type !== 'object' || !schema.properties) {
    return { params: [], contentType: mediaType };
  }

  const requiredSet = new Set(
    Array.isArray(schema.required) ? schema.required.filter((r): r is string => typeof r === 'string') : []
  );

  const params = Object.entries(schema.properties as Record<string, unknown>).map(([name, s]) =>
    parsePropertySchema(name, s as Record<string, unknown>, requiredSet)
  );

  return { params, contentType: mediaType };
}

function parseResponseSchema(operation: Record<string, unknown>): ResponseSchemaModel {
  const responses = operation.responses as Record<string, unknown> | undefined;
  const ok = responses?.['200'] as Record<string, unknown> | undefined;
  if (!ok) return { mode: 'none' };

  const content = ok.content as Record<string, unknown> | undefined;
  if (!content) return { mode: 'none' };

  const mediaType = Object.keys(content)[0] ?? 'application/json';
  const media = content[mediaType] as Record<string, unknown> | undefined;
  const schema = media?.schema;
  if (!schema || typeof schema !== 'object') return { mode: 'none' };

  const unsupported = schemaHasUnsupportedFeatures(schema);
  if (unsupported) {
    return { mode: 'raw', rawJson: JSON.stringify(schema, null, 2) };
  }

  const s = schema as Record<string, unknown>;
  if (s.type === 'array') {
    const items = s.items as Record<string, unknown> | undefined;
    const itemType = typeof items?.type === 'string' && isScalarType(items.type) ? items.type : 'string';
    return { mode: 'array', itemType };
  }

  if (s.type === 'object' && s.properties) {
    const requiredSet = new Set(
      Array.isArray(s.required) ? s.required.filter((r): r is string => typeof r === 'string') : []
    );
    return {
      mode: 'object',
      properties: Object.entries(s.properties as Record<string, unknown>).map(([name, prop]) =>
        parseNestedProperty(name, prop as Record<string, unknown>, requiredSet)
      ),
      required: [...requiredSet],
    };
  }

  return { mode: 'raw', rawJson: JSON.stringify(schema, null, 2) };
}

function buildRequestBody(model: ToolDefinitionModel): Record<string, unknown> | undefined {
  const allParams = mergeParameters(model.parameters, model.injectedParameters);
  if (allParams.length === 0) {
    return undefined;
  }

  const properties: Record<string, unknown> = {};
  const required: string[] = [];

  for (const param of allParams) {
    properties[param.name] = buildPropertySchema(param);
    if (param.required) required.push(param.name);
  }

  return {
    required: true,
    content: {
      [model.contentType]: {
        schema: {
          type: 'object',
          properties,
          ...(required.length > 0 ? { required } : {}),
          additionalProperties: false,
        },
      },
    },
  };
}

function buildResponseBody(response: ResponseSchemaModel): Record<string, unknown> {
  const base: Record<string, unknown> = { description: 'OK' };

  if (response.mode === 'none') {
    return base;
  }

  let schema: Record<string, unknown>;
  if (response.mode === 'raw' && response.rawJson) {
    try {
      schema = JSON.parse(response.rawJson);
    } catch {
      schema = { type: 'object' };
    }
  } else if (response.mode === 'array') {
    schema = { type: 'array', items: { type: response.itemType ?? 'object' } };
  } else if (response.mode === 'object' && response.properties) {
    const props: Record<string, unknown> = {};
    const required: string[] = [];
    for (const p of response.properties) {
      props[p.name] = buildNestedPropertySchema(p);
      if (p.required) required.push(p.name);
    }
    schema = {
      type: 'object',
      properties: props,
      ...(required.length > 0 ? { required } : {}),
    };
  } else {
    schema = { type: 'object' };
  }

  return {
    ...base,
    content: {
      'application/json': { schema },
    },
  };
}

export function isNonRoundtrippableOperation(operation: Record<string, unknown>): string | null {
  if (Array.isArray(operation.parameters) && operation.parameters.length > 0) {
    return 'Operation uses OpenAPI parameters array (path/query/header)';
  }

  const body = parseRequestBodyParameters(operation);
  if (body === null) {
    const requestBody = operation.requestBody as Record<string, unknown> | undefined;
    const content = requestBody?.content as Record<string, unknown> | undefined;
    const media = content ? (Object.values(content)[0] as Record<string, unknown>) : undefined;
    const schema = media?.schema;
    const unsupported = schema ? schemaHasUnsupportedFeatures(schema) : null;
    return unsupported ?? 'Request body schema is not round-trippable';
  }

  const response = operation.responses as Record<string, unknown> | undefined;
  const ok = response?.['200'] as Record<string, unknown> | undefined;
  const content = ok?.content as Record<string, unknown> | undefined;
  if (content) {
    const media = Object.values(content)[0] as Record<string, unknown> | undefined;
    if (media?.schema) {
      const unsupported = schemaHasUnsupportedFeatures(media.schema);
      if (unsupported) return unsupported;
    }
  }

  return null;
}

export function parseFragmentToModel(
  fragmentJson: string,
  sourceKind: ToolSourceKind,
  openApiSpecJson?: string
): ParseFragmentResult {
  let parsed: OperationFragment;
  try {
    parsed = JSON.parse(fragmentJson) as OperationFragment;
  } catch {
    return {
      model: createEmptyToolDefinitionModel(sourceKind),
      isCustomMode: true,
      customReason: 'Invalid fragment JSON',
    };
  }

  if (openApiSpecJson) {
    try {
      const spec = JSON.parse(openApiSpecJson);
      if (spec['x-guideants-custom-descriptor'] === true) {
        return buildModelFromFragment(parsed, sourceKind, true, 'Descriptor marked as custom');
      }
    } catch {
      /* ignore */
    }
  }

  const nonRoundtrip = isNonRoundtrippableOperation(parsed.operation);
  if (nonRoundtrip) {
    return buildModelFromFragment(parsed, sourceKind, true, nonRoundtrip);
  }

  return buildModelFromFragment(parsed, sourceKind, false);
}

function buildModelFromFragment(
  parsed: OperationFragment,
  sourceKind: ToolSourceKind,
  isCustomMode: boolean,
  customReason?: string
): ParseFragmentResult {
  const op = parsed.operation;
  const bodyResult = parseRequestBodyParameters(op);
  const allParams = bodyResult?.params ?? [];
  const { visible, injected } = partitionParameters(allParams);

  const model: ToolDefinitionModel = {
    operationId: typeof op.operationId === 'string' ? op.operationId : 'operation',
    summary: typeof op.summary === 'string' ? op.summary : '',
    description: typeof op.description === 'string' ? op.description : '',
    contentType: bodyResult?.contentType ?? 'application/json',
    parameters: visible,
    injectedParameters: injected,
    execution: {
      method: parsed.method.toLowerCase(),
      path: parsed.path,
      sandboxFunctionName:
        sourceKind === 'sandbox-module' ? parsed.path.replace(/^\//, '') : undefined,
      clientActionKey: sourceKind === 'client-actions' ? parsed.path : undefined,
    },
    response: parseResponseSchema(op),
  };

  return { model, isCustomMode, customReason };
}

export function buildFragmentFromModel(model: ToolDefinitionModel): OperationFragment {
  const operation: Record<string, unknown> = {
    operationId: model.operationId,
  };

  if (model.summary) operation.summary = model.summary;
  if (model.description) operation.description = model.description;

  const requestBody = buildRequestBody(model);
  if (requestBody) {
    operation.requestBody = requestBody;
  }

  operation.responses = {
    '200': buildResponseBody(model.response),
  };

  return {
    path: model.execution.path,
    method: model.execution.method.toLowerCase(),
    operation,
  };
}

export function buildFragmentJsonFromModel(model: ToolDefinitionModel): string {
  return JSON.stringify(buildFragmentFromModel(model), null, 2);
}

export function normalizeFragmentJson(json: string): string {
  try {
    return JSON.stringify(JSON.parse(json));
  } catch {
    return json.trim();
  }
}

/** Baseline for dirty checks: guided mode uses canonical round-trip JSON, custom keeps raw fragment. */
export function createEditorBaseline(
  fragmentJson: string,
  sourceKind: ToolSourceKind,
  openApiSpecJson?: string
): {
  baselineFragmentJson: string;
  model: ToolDefinitionModel;
  isCustomMode: boolean;
  customReason?: string;
} {
  const parsed = parseFragmentToModel(fragmentJson, sourceKind, openApiSpecJson);
  const baselineFragmentJson = parsed.isCustomMode
    ? fragmentJson
    : buildFragmentJsonFromModel(parsed.model);
  return {
    baselineFragmentJson,
    model: parsed.model,
    isCustomMode: parsed.isCustomMode,
    customReason: parsed.customReason,
  };
}

export function isFragmentDirty(baselineJson: string, currentModel: ToolDefinitionModel): boolean {
  try {
    const built = buildFragmentJsonFromModel(currentModel);
    return normalizeFragmentJson(baselineJson) !== normalizeFragmentJson(built);
  } catch {
    return true;
  }
}

export function isAdvancedFragmentDirty(baselineJson: string, advancedJson: string): boolean {
  return normalizeFragmentJson(baselineJson) !== normalizeFragmentJson(advancedJson);
}

export function detectInjectedParametersFromPreview(
  toolDefinitionJson: string
): string[] {
  try {
    const parsed = JSON.parse(toolDefinitionJson);
    const params = parsed?.function?.parameters;
    if (!params?.properties) return [];
    // Backend hides injected params — we track them in model.injectedParameters instead
    return [];
  } catch {
    return [];
  }
}

export function listInjectedParameterNames(model: ToolDefinitionModel): string[] {
  return model.injectedParameters.map((p) => p.name);
}

export function validateToolDefinitionModel(model: ToolDefinitionModel): Record<string, string> {
  const errors: Record<string, string> = {};

  if (!model.operationId.trim()) {
    errors.operationId = 'Function name is required';
  } else if (!/^[a-zA-Z_][a-zA-Z0-9_]*$/.test(model.operationId)) {
    errors.operationId = 'Function name must be a valid identifier';
  }

  if (!model.execution.path.trim()) {
    errors.path = 'Execution path is required';
  }

  const allNames = new Set<string>();
  for (const p of [...model.parameters, ...model.injectedParameters]) {
    if (!p.name.trim()) {
      errors[`param-${p.name}`] = 'Parameter name is required';
    } else if (allNames.has(p.name)) {
      errors[`param-${p.name}`] = 'Duplicate parameter name';
    } else {
      allNames.add(p.name);
    }
  }

  if (model.response.mode === 'raw' && model.response.rawJson) {
    try {
      JSON.parse(model.response.rawJson);
    } catch {
      errors.responseRaw = 'Response schema JSON is invalid';
    }
  }

  return errors;
}

export function syncExecutionPath(model: ToolDefinitionModel, sourceKind: ToolSourceKind): ToolDefinitionModel {
  if (sourceKind === 'sandbox-module' && model.execution.sandboxFunctionName) {
    const fn = model.execution.sandboxFunctionName;
    return {
      ...model,
      operationId: fn,
      execution: {
        ...model.execution,
        path: `/${fn}`,
      },
    };
  }
  if (sourceKind === 'client-actions' && model.execution.clientActionKey) {
    return {
      ...model,
      execution: { ...model.execution, path: model.execution.clientActionKey },
    };
  }
  return model;
}

export { isInjectedParameter };
