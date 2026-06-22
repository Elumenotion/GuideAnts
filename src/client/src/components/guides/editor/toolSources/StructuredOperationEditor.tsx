import { useState, useEffect, useCallback, useId, useRef } from 'react';
import { FaTimes, FaSave, FaEye, FaPlus, FaTrash } from 'react-icons/fa';
import { OpenApiOperation, type ToolDefinitionPreviewResult } from '../../../../types/guides';
import { api } from '../../../../services/api';
import { ConfirmationDialog } from '../../../common/ConfirmationDialog';
import type { ToolSourceKind } from './toolSourceClassification';
import { SOURCE_KIND_LABELS } from './toolSourceClassification';
import {
  buildFragmentJsonFromModel,
  createEditorBaseline,
  isAdvancedFragmentDirty,
  isFragmentDirty,
  listInjectedParameterNames,
  parseFragmentToModel,
  syncExecutionPath,
  validateToolDefinitionModel,
} from './operationFragmentBuilder';
import {
  type NestedPropertyModel,
  type ParameterPropertyModel,
  type ParameterScalarType,
  type ParameterType,
  type ResponseSchemaModel,
  type ToolDefinitionModel,
  mergeParameters,
  partitionParameters,
} from './toolDefinitionModel';
import { HTTP_METHODS } from './openApiToolSourceConstants';

type EditorSection =
  | 'definition'
  | 'parameters'
  | 'execution'
  | 'response'
  | 'preview'
  | 'advanced';

interface StructuredOperationEditorProps {
  operation: OpenApiOperation;
  openApiSpec: string;
  sourceKind: ToolSourceKind;
  onClose: () => void;
  onSave: (operationId: string, schemaFragment: string) => Promise<void>;
}

const SECTIONS: { id: EditorSection; label: string }[] = [
  { id: 'definition', label: 'Tool Definition' },
  { id: 'parameters', label: 'Parameters' },
  { id: 'execution', label: 'Execution Mapping' },
  { id: 'response', label: 'Response Schema' },
  { id: 'preview', label: 'Preview' },
  { id: 'advanced', label: 'Advanced Fragment' },
];

const PARAM_TYPES: ParameterType[] = ['string', 'number', 'integer', 'boolean', 'array', 'object'];
const SCALAR_TYPES: ParameterScalarType[] = ['string', 'number', 'integer', 'boolean'];

function actionTypeForKind(kind: ToolSourceKind): string {
  switch (kind) {
    case 'client-actions':
    case 'mcp-connection':
      return 'ClientHandled';
    case 'sandbox-module':
      return 'SandboxHandled';
    case 'local-function':
      return 'LocalFunction';
    case 'web-api':
    default:
      return 'WebApi';
  }
}

export function StructuredOperationEditor({
  operation,
  openApiSpec,
  sourceKind,
  onClose,
  onSave,
}: StructuredOperationEditorProps) {
  const isNewOperation = operation.id === '';
  const initialFragment = operation.schemaFragment || '';
  const editorBaseline = createEditorBaseline(initialFragment, sourceKind, openApiSpec);

  const [model, setModel] = useState<ToolDefinitionModel>(editorBaseline.model);
  const [isCustomMode, setIsCustomMode] = useState(editorBaseline.isCustomMode);
  const [customReason, setCustomReason] = useState(editorBaseline.customReason);
  const [activeSection, setActiveSection] = useState<EditorSection>('definition');
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [touched, setTouched] = useState<Record<string, boolean>>({});
  const [saving, setSaving] = useState(false);
  const [previewing, setPreviewing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [dismissedError, setDismissedError] = useState(false);
  const [toolDefinitionPreview, setToolDefinitionPreview] = useState(operation.toolDefinition || '');
  const [previewResult, setPreviewResult] = useState<ToolDefinitionPreviewResult | null>(null);
  const [advancedJson, setAdvancedJson] = useState(editorBaseline.baselineFragmentJson);
  const [showCloseConfirm, setShowCloseConfirm] = useState(false);
  const tabListId = useId();
  const closeTriggerRef = useRef<HTMLElement | null>(null);

  useEffect(() => {
    closeTriggerRef.current = document.activeElement as HTMLElement;
  }, []);

  const isDirty = useCallback(() => {
    if (activeSection === 'advanced' || isCustomMode) {
      return isAdvancedFragmentDirty(editorBaseline.baselineFragmentJson, advancedJson);
    }
    return isFragmentDirty(editorBaseline.baselineFragmentJson, model);
  }, [activeSection, advancedJson, editorBaseline.baselineFragmentJson, isCustomMode, model]);

  const runValidation = useCallback(() => validateToolDefinitionModel(model), [model]);

  useEffect(() => {
    if (Object.keys(touched).length > 0) {
      setFieldErrors(runValidation());
    }
  }, [model, touched, runValidation]);

  const handleModelChange = (updates: Partial<ToolDefinitionModel>) => {
    let next = { ...model, ...updates };
    if (updates.execution) {
      next = syncExecutionPath(next, sourceKind);
    }
    setModel(next);
    if (!isCustomMode && activeSection !== 'advanced') {
      setAdvancedJson(buildFragmentJsonFromModel(next));
    }
  };

  const handleAdvancedJsonChange = (json: string) => {
    setAdvancedJson(json);
    try {
      const result = parseFragmentToModel(json, sourceKind, openApiSpec);
      if (!result.isCustomMode) {
        setModel(result.model);
        setIsCustomMode(false);
        setCustomReason(undefined);
      } else {
        setIsCustomMode(true);
        setCustomReason(result.customReason);
      }
    } catch {
      setIsCustomMode(true);
      setCustomReason('Invalid fragment JSON');
    }
  };

  const requestClose = () => {
    if (isDirty()) {
      setShowCloseConfirm(true);
    } else {
      closeTriggerRef.current?.focus();
      onClose();
    }
  };

  const requestSectionChange = (section: EditorSection) => {
    if (section === activeSection) return;
    // Guided sections share one model; switching never discards edits.
    setActiveSection(section);
  };

  const handlePreview = async () => {
    const errors = runValidation();
    setFieldErrors(errors);
    if (Object.keys(errors).length > 0) {
      setError('Fix validation errors before previewing');
      setDismissedError(false);
      return;
    }

    const fragment = activeSection === 'advanced' ? advancedJson : buildFragmentJsonFromModel(model);

    setPreviewing(true);
    setError(null);
    setDismissedError(false);

    try {
      const data = await api.guides.operations.preview({
        schemaFragmentJson: fragment,
        openApiSpecJson: openApiSpec,
      });
      setPreviewResult(data);
      setToolDefinitionPreview(data.toolDefinition);
      setActiveSection('preview');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to preview');
      setDismissedError(false);
    } finally {
      setPreviewing(false);
    }
  };

  const handleSave = async () => {
    const errors = runValidation();
    setFieldErrors(errors);
    setTouched({ all: true });
    if (Object.keys(errors).length > 0) {
      setError('Fix validation errors before saving');
      setDismissedError(false);
      return;
    }

    const fragment = activeSection === 'advanced' && isCustomMode ? advancedJson : buildFragmentJsonFromModel(model);

    setSaving(true);
    setError(null);

    try {
      await onSave(operation.id, fragment);
      closeTriggerRef.current?.focus();
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save');
      setDismissedError(false);
    } finally {
      setSaving(false);
    }
  };

  const handleReturnToGuided = () => {
    try {
      const result = parseFragmentToModel(advancedJson, sourceKind, openApiSpec);
      if (result.isCustomMode) {
        setError(result.customReason ?? 'Fragment is not round-trippable to guided mode');
        setDismissedError(false);
        return;
      }
      setModel(result.model);
      setIsCustomMode(false);
      setCustomReason(undefined);
      setActiveSection('definition');
    } catch {
      setError('Cannot return to guided mode: invalid fragment');
      setDismissedError(false);
    }
  };

  const validationErrors = runValidation();
  const hasErrors = Object.keys(validationErrors).length > 0;
  const injectedNames = listInjectedParameterNames(model);

  return (
    <>
      <ConfirmationDialog
        isOpen={showCloseConfirm}
        onClose={() => setShowCloseConfirm(false)}
        onConfirm={() => {
          setShowCloseConfirm(false);
          closeTriggerRef.current?.focus();
          onClose();
        }}
        title="Discard unsaved changes?"
        message="You have unsaved changes to this operation. Close without saving?"
        confirmText="Discard"
        cancelText="Keep editing"
        confirmButtonClass="bg-red-600 hover:bg-red-700 text-white"
      />

      <div className="fixed inset-0 z-50 overflow-y-auto" data-tour-id="guide.tools.operationEditor.modal">
        <div className="flex min-h-screen items-end sm:items-center justify-center p-0 sm:p-4">
          <div className="fixed inset-0 bg-black bg-opacity-50" onClick={requestClose} aria-hidden="true" />

          <div className="relative bg-white sm:rounded-lg shadow-xl w-full sm:max-w-6xl max-h-[100vh] sm:max-h-[90vh] flex flex-col">
            {/* Header */}
            <div className="flex items-center justify-between p-4 sm:p-6 border-b border-gray-200 shrink-0">
              <div>
                <h2 className="text-xl font-semibold text-gray-900">
                  {isNewOperation ? 'Create Operation' : 'Edit Operation'}
                </h2>
                <div className="flex items-center gap-2 mt-1 flex-wrap">
                  <span className="text-xs px-2 py-0.5 rounded bg-gray-100 text-gray-700">
                    {SOURCE_KIND_LABELS[sourceKind]}
                  </span>
                  {isCustomMode && (
                    <span className="text-xs px-2 py-0.5 rounded bg-blue-100 text-blue-800">
                      Custom descriptor
                    </span>
                  )}
                  {isNewOperation && (
                    <span className="text-xs px-2 py-0.5 rounded bg-amber-100 text-amber-800">
                      New — save guide to persist
                    </span>
                  )}
                </div>
              </div>
              <button
                type="button"
                onClick={requestClose}
                className="text-gray-400 hover:text-gray-600 p-2 rounded-lg hover:bg-gray-100"
                aria-label="Close"
              >
                <FaTimes className="w-5 h-5" />
              </button>
            </div>

            {isCustomMode && customReason && (
              <div className="px-4 sm:px-6 py-2 bg-blue-50 border-b border-blue-200 text-xs text-blue-800">
                {customReason}. Guided controls are limited — use Advanced Fragment to edit, or{' '}
                <button type="button" onClick={handleReturnToGuided} className="underline font-medium">
                  return to guided mode
                </button>
                .
              </div>
            )}

            {/* Section tabs */}
            <div
              className="flex border-b border-gray-200 px-2 sm:px-6 overflow-x-auto shrink-0"
              role="tablist"
              aria-label="Operation editor sections"
              id={tabListId}
            >
              {SECTIONS.map((section) => (
                <button
                  key={section.id}
                  type="button"
                  role="tab"
                  aria-selected={activeSection === section.id}
                  aria-controls={`${tabListId}-${section.id}`}
                  onClick={() => requestSectionChange(section.id)}
                  className={`px-3 sm:px-4 py-3 text-sm font-medium border-b-2 -mb-px whitespace-nowrap focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ${
                    activeSection === section.id
                      ? 'border-blue-600 text-blue-600'
                      : 'border-transparent text-gray-600 hover:text-gray-900'
                  }`}
                >
                  {section.label}
                </button>
              ))}
            </div>

            {/* Content */}
            <div className="flex-1 overflow-auto p-4 sm:p-6">
              {error && !dismissedError && (
                <div
                  className="mb-4 p-4 bg-red-50 border border-red-200 rounded-lg flex justify-between"
                  role="alert"
                  aria-live="polite"
                >
                  <p className="text-sm text-red-800">{error}</p>
                  <button
                    type="button"
                    onClick={() => setDismissedError(true)}
                    className="text-red-600 text-xs underline ml-4 shrink-0"
                  >
                    Dismiss
                  </button>
                </div>
              )}

              <div
                role="tabpanel"
                id={`${tabListId}-${activeSection}`}
                aria-labelledby={activeSection}
                className="w-full"
              >
                  {activeSection === 'definition' && (
                    <DefinitionSection
                      model={model}
                      disabled={isCustomMode}
                      errors={fieldErrors}
                      touched={touched}
                      onBlur={(field) => setTouched((t) => ({ ...t, [field]: true }))}
                      onChange={handleModelChange}
                    />
                  )}

                  {activeSection === 'parameters' && (
                    <ParametersSection
                      model={model}
                      disabled={isCustomMode}
                      onChange={handleModelChange}
                    />
                  )}

                  {activeSection === 'execution' && (
                    <ExecutionSection
                      model={model}
                      sourceKind={sourceKind}
                      disabled={isCustomMode}
                      errors={fieldErrors}
                      touched={touched}
                      onBlur={(field) => setTouched((t) => ({ ...t, [field]: true }))}
                      onChange={handleModelChange}
                    />
                  )}

                  {activeSection === 'response' && (
                    <ResponseSection
                      model={model}
                      disabled={isCustomMode}
                      errors={fieldErrors}
                      onChange={handleModelChange}
                    />
                  )}

                  {activeSection === 'preview' && (
                    <PreviewSection
                      toolDefinition={toolDefinitionPreview}
                      previewResult={previewResult}
                      sourceKind={sourceKind}
                      injectedNames={injectedNames}
                      previewing={previewing}
                    />
                  )}

                  {activeSection === 'advanced' && (
                    <AdvancedSection json={advancedJson} onChange={handleAdvancedJsonChange} />
                  )}
              </div>
            </div>

            {/* Sticky footer */}
            <div className="flex items-center justify-between gap-3 p-4 sm:p-6 border-t border-gray-200 bg-gray-50 shrink-0 sticky bottom-0">
              <button
                type="button"
                onClick={handlePreview}
                disabled={previewing || hasErrors}
                className="inline-flex items-center gap-2 px-3 py-2 text-sm font-medium text-blue-700 bg-blue-50 border border-blue-200 rounded-md hover:bg-blue-100 disabled:opacity-50"
              >
                <FaEye className="w-4 h-4" />
                {previewing ? 'Previewing…' : 'Preview'}
              </button>
              <div className="flex gap-3">
                <button
                  type="button"
                  onClick={requestClose}
                  className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50"
                >
                  Cancel
                </button>
                <button
                  type="button"
                  onClick={handleSave}
                  disabled={saving || hasErrors}
                  className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-white bg-blue-600 rounded-md hover:bg-blue-700 disabled:opacity-50"
                >
                  <FaSave className="w-4 h-4" />
                  {saving ? 'Saving…' : isNewOperation ? 'Save to Schema' : 'Save Changes'}
                </button>
              </div>
            </div>
          </div>
        </div>
      </div>
    </>
  );
}

function DefinitionSection({
  model,
  disabled,
  errors,
  touched,
  onBlur,
  onChange,
}: {
  model: ToolDefinitionModel;
  disabled: boolean;
  errors: Record<string, string>;
  touched: Record<string, boolean>;
  onBlur: (field: string) => void;
  onChange: (u: Partial<ToolDefinitionModel>) => void;
}) {
  return (
    <div className="space-y-3 w-full">
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">
          Function name <span className="text-red-500">*</span>
        </label>
        <input
          type="text"
          value={model.operationId}
          disabled={disabled}
          onChange={(e) => onChange({ operationId: e.target.value })}
          onBlur={() => onBlur('operationId')}
          className={`w-full px-3 py-2 text-sm border rounded-md font-mono ${touched.operationId && errors.operationId ? 'border-red-400' : 'border-gray-300'} disabled:bg-gray-100`}
        />
        {touched.operationId && errors.operationId && (
          <p className="text-xs text-red-600 mt-1" role="alert">{errors.operationId}</p>
        )}
      </div>
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Summary</label>
        <input
          type="text"
          value={model.summary ?? ''}
          disabled={disabled}
          onChange={(e) => onChange({ summary: e.target.value })}
          className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md disabled:bg-gray-100"
        />
        <p className="text-xs text-gray-500 mt-0.5">Preferred for the model-facing description.</p>
      </div>
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Description</label>
        <textarea
          value={model.description ?? ''}
          disabled={disabled}
          onChange={(e) => onChange({ description: e.target.value })}
          rows={2}
          className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md disabled:bg-gray-100"
        />
      </div>
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Content type</label>
        <input
          type="text"
          value={model.contentType}
          disabled={disabled}
          onChange={(e) => onChange({ contentType: e.target.value })}
          className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md font-mono disabled:bg-gray-100"
        />
      </div>
    </div>
  );
}

function ParametersSection({
  model,
  disabled,
  onChange,
}: {
  model: ToolDefinitionModel;
  disabled: boolean;
  onChange: (u: Partial<ToolDefinitionModel>) => void;
}) {
  const updateParam = (index: number, updates: Partial<ParameterPropertyModel>, injected: boolean) => {
    const list = injected ? [...model.injectedParameters] : [...model.parameters];
    list[index] = { ...list[index], ...updates };
    if (updates.default !== undefined || updates.enumValues !== undefined) {
      const merged = mergeParameters(
        injected ? model.parameters : list,
        injected ? list : model.injectedParameters
      );
      const { visible, injected: inj } = partitionParameters(merged);
      onChange({ parameters: visible, injectedParameters: inj });
      return;
    }
    onChange(injected ? { injectedParameters: list } : { parameters: list });
  };

  const addParam = (injected: boolean) => {
    const newParam: ParameterPropertyModel = {
      name: `param${(injected ? model.injectedParameters : model.parameters).length + 1}`,
      type: 'string',
      required: false,
    };
    if (injected) {
      onChange({ injectedParameters: [...model.injectedParameters, newParam] });
    } else {
      onChange({ parameters: [...model.parameters, newParam] });
    }
  };

  const removeParam = (index: number, injected: boolean) => {
    if (injected) {
      onChange({ injectedParameters: model.injectedParameters.filter((_, i) => i !== index) });
    } else {
      onChange({ parameters: model.parameters.filter((_, i) => i !== index) });
    }
  };

  return (
    <div className="space-y-4 w-full">
      <ParameterTable
        title="Parameters"
        description="Arguments exposed to the model."
        params={model.parameters}
        disabled={disabled}
        onUpdate={(i, u) => updateParam(i, u, false)}
        onAdd={() => addParam(false)}
        onRemove={(i) => removeParam(i, false)}
      />

      <ParameterTable
        title="Hidden / default-injected parameters"
        description="Parameters with a default or single enum value are auto-injected at runtime and hidden from the model."
        params={model.injectedParameters}
        disabled={disabled}
        onUpdate={(i, u) => updateParam(i, u, true)}
        onAdd={() => addParam(true)}
        onRemove={(i) => removeParam(i, true)}
        injected
      />
    </div>
  );
}

function ParameterTable({
  title,
  description,
  params,
  disabled,
  injected,
  onUpdate,
  onAdd,
  onRemove,
}: {
  title: string;
  description: string;
  params: ParameterPropertyModel[];
  disabled: boolean;
  injected?: boolean;
  onUpdate: (index: number, updates: Partial<ParameterPropertyModel>) => void;
  onAdd: () => void;
  onRemove: (index: number) => void;
}) {
  return (
    <div>
      <div className="flex items-center justify-between mb-2">
        <div>
          <h3 className="text-sm font-medium text-gray-900">{title}</h3>
          <p className="text-xs text-gray-600">{description}</p>
        </div>
        {!disabled && (
          <button
            type="button"
            onClick={onAdd}
            className="flex items-center gap-1 px-2 py-1 text-xs text-blue-600 hover:bg-blue-50 rounded"
          >
            <FaPlus className="w-3 h-3" /> Add
          </button>
        )}
      </div>
      {params.length === 0 ? (
        <p className="text-xs text-gray-500 py-4 text-center border border-dashed rounded-lg">No parameters</p>
      ) : (
        <div className="space-y-3">
          {params.map((param, index) => (
            <ParameterRow
              key={`${injected ? 'inj' : 'vis'}-${index}`}
              param={param}
              disabled={disabled}
              onUpdate={(u) => onUpdate(index, u)}
              onRemove={() => onRemove(index)}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function ParameterRow({
  param,
  disabled,
  onUpdate,
  onRemove,
}: {
  param: ParameterPropertyModel;
  disabled: boolean;
  onUpdate: (u: Partial<ParameterPropertyModel>) => void;
  onRemove: () => void;
}) {
  return (
    <div className="border border-gray-200 rounded-lg p-3 bg-white space-y-2">
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-2">
        <input
          type="text"
          value={param.name}
          disabled={disabled}
          placeholder="Name"
          onChange={(e) => onUpdate({ name: e.target.value })}
          className="px-2 py-1 text-sm border border-gray-300 rounded font-mono disabled:bg-gray-100"
        />
        <select
          value={param.type}
          disabled={disabled}
          onChange={(e) => onUpdate({ type: e.target.value as ParameterType })}
          className="px-2 py-1 text-sm border border-gray-300 rounded disabled:bg-gray-100"
        >
          {PARAM_TYPES.map((t) => (
            <option key={t} value={t}>{t}</option>
          ))}
        </select>
        <label className="flex items-center gap-1 text-sm">
          <input
            type="checkbox"
            checked={param.required}
            disabled={disabled}
            onChange={(e) => onUpdate({ required: e.target.checked })}
          />
          Required
        </label>
        {!disabled && (
          <button type="button" onClick={onRemove} className="text-red-600 hover:text-red-700 justify-self-end">
            <FaTrash className="w-4 h-4" />
          </button>
        )}
      </div>
      <input
        type="text"
        value={param.description ?? ''}
        disabled={disabled}
        placeholder="Description"
        onChange={(e) => onUpdate({ description: e.target.value })}
        className="w-full px-2 py-1 text-sm border border-gray-300 rounded disabled:bg-gray-100"
      />
      <div className="grid grid-cols-2 gap-2">
        <input
          type="text"
          value={param.default ?? ''}
          disabled={disabled}
          placeholder="Default (JSON)"
          onChange={(e) => onUpdate({ default: e.target.value })}
          className="px-2 py-1 text-sm border border-gray-300 rounded font-mono disabled:bg-gray-100"
        />
        <input
          type="text"
          value={param.example ?? ''}
          disabled={disabled}
          placeholder="Example (JSON)"
          onChange={(e) => onUpdate({ example: e.target.value })}
          className="px-2 py-1 text-sm border border-gray-300 rounded font-mono disabled:bg-gray-100"
        />
      </div>
      <input
        type="text"
        value={param.enumValues?.join(', ') ?? ''}
        disabled={disabled}
        placeholder="Enum values (comma-separated)"
        onChange={(e) =>
          onUpdate({
            enumValues: e.target.value
              .split(',')
              .map((s) => s.trim())
              .filter(Boolean),
          })
        }
        className="w-full px-2 py-1 text-sm border border-gray-300 rounded disabled:bg-gray-100"
      />

      {param.type === 'array' && (
        <div className="pl-3 border-l-2 border-gray-200 space-y-2">
          <label className="text-xs font-medium text-gray-600">Array items</label>
          <select
            value={param.itemType ?? 'string'}
            disabled={disabled}
            onChange={(e) => onUpdate({ itemType: e.target.value as ParameterType })}
            className="px-2 py-1 text-sm border border-gray-300 rounded disabled:bg-gray-100"
          >
            {[...SCALAR_TYPES, 'object'].map((t) => (
              <option key={t} value={t}>{t}</option>
            ))}
          </select>
          {param.itemType === 'object' && (
            <NestedPropertiesEditor
              properties={param.itemProperties ?? []}
              disabled={disabled}
              onChange={(props) => onUpdate({ itemProperties: props })}
            />
          )}
        </div>
      )}

      {param.type === 'object' && (
        <div className="pl-3 border-l-2 border-gray-200">
          <label className="text-xs font-medium text-gray-600">Object properties (one level)</label>
          <NestedPropertiesEditor
            properties={param.objectProperties ?? []}
            disabled={disabled}
            onChange={(props) => onUpdate({ objectProperties: props })}
          />
        </div>
      )}
    </div>
  );
}

function NestedPropertiesEditor({
  properties,
  disabled,
  onChange,
}: {
  properties: NestedPropertyModel[];
  disabled: boolean;
  onChange: (props: NestedPropertyModel[]) => void;
}) {
  const add = () => {
    onChange([
      ...properties,
      { name: `field${properties.length + 1}`, type: 'string', required: false },
    ]);
  };

  return (
    <div className="mt-2 space-y-2">
      {properties.map((prop, i) => (
        <div key={i} className="grid grid-cols-3 gap-2">
          <input
            type="text"
            value={prop.name}
            disabled={disabled}
            onChange={(e) => {
              const next = [...properties];
              next[i] = { ...prop, name: e.target.value };
              onChange(next);
            }}
            className="px-2 py-1 text-xs border rounded font-mono disabled:bg-gray-100"
          />
          <select
            value={prop.type}
            disabled={disabled}
            onChange={(e) => {
              const next = [...properties];
              next[i] = { ...prop, type: e.target.value as ParameterScalarType };
              onChange(next);
            }}
            className="px-2 py-1 text-xs border rounded disabled:bg-gray-100"
          >
            {SCALAR_TYPES.map((t) => (
              <option key={t} value={t}>{t}</option>
            ))}
          </select>
          <label className="flex items-center gap-1 text-xs">
            <input
              type="checkbox"
              checked={prop.required}
              disabled={disabled}
              onChange={(e) => {
                const next = [...properties];
                next[i] = { ...prop, required: e.target.checked };
                onChange(next);
              }}
            />
            Req
          </label>
        </div>
      ))}
      {!disabled && (
        <button type="button" onClick={add} className="text-xs text-blue-600">
          + Add property
        </button>
      )}
    </div>
  );
}

function ExecutionSection({
  model,
  sourceKind,
  disabled,
  errors,
  touched,
  onBlur,
  onChange,
}: {
  model: ToolDefinitionModel;
  sourceKind: ToolSourceKind;
  disabled: boolean;
  errors: Record<string, string>;
  touched: Record<string, boolean>;
  onBlur: (field: string) => void;
  onChange: (u: Partial<ToolDefinitionModel>) => void;
}) {
  const exec = model.execution;

  if (sourceKind === 'web-api') {
    return (
      <div className="space-y-3 w-full">
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">HTTP method</label>
          <select
            value={exec.method}
            disabled={disabled}
            onChange={(e) =>
              onChange({ execution: { ...exec, method: e.target.value } })
            }
            className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md disabled:bg-gray-100"
          >
            {HTTP_METHODS.map((m) => (
              <option key={m} value={m}>{m.toUpperCase()}</option>
            ))}
          </select>
        </div>
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Path</label>
          <input
            type="text"
            value={exec.path}
            disabled={disabled}
            onChange={(e) => onChange({ execution: { ...exec, path: e.target.value } })}
            onBlur={() => onBlur('path')}
            className={`w-full px-3 py-2 text-sm border rounded-md font-mono ${touched.path && errors.path ? 'border-red-400' : 'border-gray-300'}`}
          />
        </div>
      </div>
    );
  }

  if (sourceKind === 'client-actions') {
    return (
      <div className="space-y-3 w-full">
        <p className="text-sm text-gray-600 bg-purple-50 border border-purple-200 rounded-md p-3">
          Handled by the client application via the configured client bridge.
        </p>
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Client action key</label>
          <input
            type="text"
            value={exec.clientActionKey ?? exec.path}
            disabled={disabled}
            onChange={(e) =>
              onChange({
                execution: { ...exec, clientActionKey: e.target.value, path: e.target.value },
              })
            }
            placeholder="Bridge.ActionName"
            className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md font-mono disabled:bg-gray-100"
          />
        </div>
      </div>
    );
  }

  if (sourceKind === 'sandbox-module') {
    return (
      <div className="space-y-3 w-full">
        <p className="text-sm text-gray-600 bg-orange-50 border border-orange-200 rounded-md p-3">
          Enter the Python function name manually (function discovery is manual-first).
        </p>
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Python function name</label>
          <input
            type="text"
            value={exec.sandboxFunctionName ?? exec.path.replace(/^\//, '')}
            disabled={disabled}
            onChange={(e) =>
              onChange({
                execution: {
                  ...exec,
                  sandboxFunctionName: e.target.value,
                  path: `/${e.target.value}`,
                },
              })
            }
            className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md font-mono disabled:bg-gray-100"
          />
        </div>
      </div>
    );
  }

  if (sourceKind === 'local-function') {
    return (
      <div className="space-y-3 w-full">
        <p className="text-sm text-gray-600 bg-gray-50 border border-gray-200 rounded-md p-3">
          Fully qualified .NET type and member path (advanced).
        </p>
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Method path</label>
          <input
            type="text"
            value={exec.path}
            disabled={disabled}
            onChange={(e) => onChange({ execution: { ...exec, path: e.target.value } })}
            placeholder="Namespace.Type.Method"
            className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md font-mono disabled:bg-gray-100"
          />
        </div>
      </div>
    );
  }

  return (
    <div className="w-full">
      <label className="block text-sm font-medium text-gray-700 mb-1">Path</label>
      <input
        type="text"
        value={exec.path}
        disabled={disabled}
        onChange={(e) => onChange({ execution: { ...exec, path: e.target.value } })}
        className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md font-mono"
      />
    </div>
  );
}

function ResponseSection({
  model,
  disabled,
  errors,
  onChange,
}: {
  model: ToolDefinitionModel;
  disabled: boolean;
  errors: Record<string, string>;
  onChange: (u: Partial<ToolDefinitionModel>) => void;
}) {
  const response = model.response;

  const setResponse = (updates: Partial<ResponseSchemaModel>) => {
    onChange({ response: { ...response, ...updates } });
  };

  return (
    <div className="space-y-3 w-full">
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Response mode</label>
        <select
          value={response.mode}
          disabled={disabled}
          onChange={(e) => setResponse({ mode: e.target.value as ResponseSchemaModel['mode'] })}
          className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md disabled:bg-gray-100"
        >
          <option value="none">None</option>
          <option value="object">Object</option>
          <option value="array">Array</option>
          <option value="raw">Raw JSON schema</option>
        </select>
      </div>

      {response.mode === 'object' && (
        <NestedPropertiesEditor
          properties={response.properties ?? []}
          disabled={disabled}
          onChange={(props) => setResponse({ properties: props })}
        />
      )}

      {response.mode === 'array' && (
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Item type</label>
          <select
            value={response.itemType ?? 'object'}
            disabled={disabled}
            onChange={(e) => setResponse({ itemType: e.target.value as ParameterScalarType })}
            className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md"
          >
            {SCALAR_TYPES.map((t) => (
              <option key={t} value={t}>{t}</option>
            ))}
          </select>
        </div>
      )}

      {response.mode === 'raw' && (
        <div>
          <textarea
            value={response.rawJson ?? ''}
            disabled={disabled}
            onChange={(e) => setResponse({ rawJson: e.target.value })}
            rows={8}
            className={`w-full px-3 py-2 text-xs font-mono border rounded-md ${errors.responseRaw ? 'border-red-400' : 'border-gray-300'}`}
          />
          {errors.responseRaw && <p className="text-xs text-red-600 mt-1">{errors.responseRaw}</p>}
        </div>
      )}
    </div>
  );
}

function PreviewSection({
  toolDefinition,
  previewResult,
  sourceKind,
  injectedNames,
  previewing,
}: {
  toolDefinition: string;
  previewResult: ToolDefinitionPreviewResult | null;
  sourceKind: ToolSourceKind;
  injectedNames: string[];
  previewing: boolean;
}) {
  let formatted = 'Click Preview to generate tool definition from the backend.';
  try {
    if (toolDefinition) {
      formatted = JSON.stringify(JSON.parse(toolDefinition), null, 2);
    }
  } catch {
    formatted = toolDefinition || formatted;
  }

  const displaySourceKind = previewResult?.sourceKind ?? sourceKind;
  const displayActionType = previewResult?.actionType ?? actionTypeForKind(sourceKind);
  const hiddenFromBackend = previewResult?.hiddenParameters ?? [];
  const hiddenNames = hiddenFromBackend.length > 0 ? hiddenFromBackend : injectedNames;
  const validationMessages = previewResult?.validationMessages ?? [];
  const globalMessages = validationMessages.filter((m) => !m.field);
  const responseSchemaJson = previewResult?.responseSchemas
    ? JSON.stringify(previewResult.responseSchemas, null, 2)
    : null;

  const sourceLabel =
    SOURCE_KIND_LABELS[displaySourceKind as ToolSourceKind] ?? displaySourceKind;

  return (
    <div className="space-y-3 w-full">
      <div className="flex flex-wrap gap-4 text-sm">
        <span className="text-gray-600">
          Source: <strong>{sourceLabel}</strong>
        </span>
        <span className="text-gray-600">
          Action type: <strong>{displayActionType}</strong>
        </span>
      </div>

      {globalMessages.length > 0 && (
        <div className="space-y-2" aria-live="polite">
          {globalMessages.map((message) => (
            <div
              key={`${message.code}-${message.message}`}
              className={`p-3 rounded-md text-sm border ${
                message.severity === 'error'
                  ? 'bg-red-50 border-red-200 text-red-800'
                  : 'bg-amber-50 border-amber-200 text-amber-800'
              }`}
            >
              {message.message}
            </div>
          ))}
        </div>
      )}

      {hiddenNames.length > 0 && (
        <div className="p-3 bg-amber-50 border border-amber-200 rounded-md text-sm text-amber-800">
          Hidden/default-injected parameters (not shown to model):{' '}
          <code>{hiddenNames.join(', ')}</code>
        </div>
      )}

      {responseSchemaJson && (
        <div>
          <h4 className="text-sm font-medium text-gray-700 mb-1">Response schema</h4>
          <pre className="w-full max-h-[200px] overflow-auto px-3 py-2 font-mono text-xs bg-gray-50 border border-gray-300 rounded-md">
            {responseSchemaJson}
          </pre>
        </div>
      )}

      {previewing ? (
        <div className="text-sm text-gray-600" aria-live="polite">Loading preview…</div>
      ) : (
        <pre className="w-full max-h-[400px] overflow-auto px-3 py-2 font-mono text-xs bg-gray-50 border border-gray-300 rounded-md">
          {formatted}
        </pre>
      )}
    </div>
  );
}

function AdvancedSection({
  json,
  onChange,
}: {
  json: string;
  onChange: (json: string) => void;
}) {
  let jsonError: string | null = null;
  try {
    JSON.parse(json);
  } catch (e) {
    jsonError = e instanceof Error ? e.message : 'Invalid JSON';
  }

  return (
    <div className="w-full">
      <p className="text-xs text-gray-600 mb-2">
        Raw OpenAPI operation fragment: {'{ path, method, operation }'}
      </p>
      <textarea
        value={json}
        onChange={(e) => onChange(e.target.value)}
        rows={16}
        spellCheck={false}
        className={`w-full px-3 py-2 font-mono text-sm border rounded-md ${jsonError ? 'border-red-400 bg-red-50' : 'border-gray-300'}`}
      />
      {jsonError && (
        <p className="text-xs text-red-600 mt-1" role="alert">{jsonError}</p>
      )}
    </div>
  );
}
