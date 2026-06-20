import { CustomToolDto } from '../../../../types/guides';
import {
  classifyToolSourceFromSpec,
  CONNECTOR_KEY_LABELS,
  SOURCE_KIND_LABELS,
  ToolSourceKind,
  ToolSourceStatus,
  extractConnectorKeyFromServerUrl,
} from './toolSourceClassification';
import { extractServerUrl, extractTools, isConnectorKeyUnique } from './openApiToolSource';
import { isCustomDescriptor, isInvalidJson, validateOpenApiSpec } from './validation';

export interface ToolSourceCardViewModel {
  sourceKind: ToolSourceKind;
  sourceKindLabel: string;
  connectorKeyLabel: string;
  connectorKeyValue: string;
  operationCount: number;
  status: ToolSourceStatus;
  statusLabel: string;
  isCustomDescriptor: boolean;
  hasAuth: boolean;
  connectorKeyConflict: boolean;
  validationMessage: string | null;
}

const STATUS_LABELS: Record<ToolSourceStatus, string> = {
  valid: 'Valid',
  'needs-attention': 'Needs attention',
  custom: 'Custom descriptor',
  'invalid-json': 'Invalid JSON',
};

export function deriveToolSourceStatus(
  spec: string,
  validationMessage: string | null,
  connectorKeyConflict: boolean,
  isCustom: boolean
): ToolSourceStatus {
  if (isInvalidJson(spec)) {
    return 'invalid-json';
  }
  if (validationMessage) {
    return 'needs-attention';
  }
  if (connectorKeyConflict) {
    return 'needs-attention';
  }
  if (isCustom) {
    return 'custom';
  }
  return 'valid';
}

export function buildToolSourceCardViewModel(
  tool: CustomToolDto,
  allTools: CustomToolDto[],
  toolIndex: number
): ToolSourceCardViewModel {
  const spec = tool.openApiSpec;
  const invalidJson = isInvalidJson(spec);
  const validationMessage = invalidJson ? null : validateOpenApiSpec(spec);
  const serverUrl = invalidJson ? null : extractServerUrl(spec);
  const sourceKind = invalidJson ? 'unknown' : classifyToolSourceFromSpec(spec, serverUrl);
  const connectorKeyFromUrl = serverUrl ? extractConnectorKeyFromServerUrl(serverUrl) : null;
  const connectorKeyValue = tool.apiHost || connectorKeyFromUrl || '';
  const connectorKeyConflict =
    !!connectorKeyValue && !isConnectorKeyUnique(connectorKeyValue, allTools, toolIndex);
  const custom = !invalidJson && isCustomDescriptor(spec);
  const status = deriveToolSourceStatus(spec, validationMessage, connectorKeyConflict, custom);
  const operations = invalidJson ? [] : extractTools(spec);

  return {
    sourceKind,
    sourceKindLabel: SOURCE_KIND_LABELS[sourceKind],
    connectorKeyLabel: CONNECTOR_KEY_LABELS[sourceKind],
    connectorKeyValue: connectorKeyValue || 'Not detected',
    operationCount: operations.length,
    status,
    statusLabel: STATUS_LABELS[status],
    isCustomDescriptor: custom,
    hasAuth: !!tool.authConfig,
    connectorKeyConflict,
    validationMessage,
  };
}

export function statusChipClassName(status: ToolSourceStatus): string {
  switch (status) {
    case 'valid':
      return 'bg-green-100 text-green-800';
    case 'needs-attention':
      return 'bg-amber-100 text-amber-800';
    case 'custom':
      return 'bg-blue-100 text-blue-800';
    case 'invalid-json':
      return 'bg-red-100 text-red-800';
  }
}

export function sourceKindBadgeClassName(kind: ToolSourceKind): string {
  switch (kind) {
    case 'web-api':
      return 'bg-indigo-100 text-indigo-800';
    case 'client-actions':
      return 'bg-purple-100 text-purple-800';
    case 'sandbox-module':
      return 'bg-orange-100 text-orange-800';
    case 'local-function':
      return 'bg-gray-100 text-gray-800';
    case 'mcp-connection':
      return 'bg-teal-100 text-teal-800';
    default:
      return 'bg-gray-100 text-gray-600';
  }
}
