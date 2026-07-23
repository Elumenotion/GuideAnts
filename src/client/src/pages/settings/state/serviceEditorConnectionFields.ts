const INLINE_CONNECTION_FIELD_NAMES = new Set([
  'Endpoint',
  'ApiKey',
  'Region',
  'Token',
  'Resource',
  'AuthToken',
  'RouterBaseUrl',
  'ApiVersion',
]);

/** True when the service editor exposes connection fields inline (Foundry multi-endpoint). */
export function hasInlineConnectionFields(operativeFields: readonly string[]): boolean {
  return operativeFields.some((name) => INLINE_CONNECTION_FIELD_NAMES.has(name));
}

export function getServiceEditorSaveGate(provider: {
  connectionConfigured: boolean;
  hasExplicitMode: boolean;
  operativeFields: readonly string[];
}): { saveDisabledByConnection: boolean; saveTitle: string } {
  const canSaveConnectionInline = hasInlineConnectionFields(provider.operativeFields);
  if (!provider.connectionConfigured && !canSaveConnectionInline) {
    return {
      saveDisabledByConnection: true,
      saveTitle: 'Configure the provider connection first.',
    };
  }
  if (!provider.connectionConfigured && canSaveConnectionInline) {
    return {
      saveDisabledByConnection: false,
      saveTitle: 'Save will write connection details and activate provider.',
    };
  }
  if (!provider.hasExplicitMode) {
    return {
      saveDisabledByConnection: false,
      saveTitle: 'Save will create an explicit service mode and activate provider.',
    };
  }
  return {
    saveDisabledByConnection: false,
    saveTitle: 'Save and activate provider.',
  };
}
