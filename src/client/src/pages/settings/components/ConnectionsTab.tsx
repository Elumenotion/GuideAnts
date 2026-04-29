import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { FaBan, FaRedo, FaReply, FaSave, FaSpinner, FaSyncAlt, FaUndo } from 'react-icons/fa';
import LoadingSpinner from '../../../components/LoadingSpinner';
import { useToast } from '../../../components/common/Toast';
import { api } from '../../../services/api';
import {
  ConnectionUsageDto,
  SettingsOverviewDto,
  SettingsSchemaDto,
  SettingsSectionDto,
  SettingsSectionPropertyDefinitionDto,
  SettingsSectionSummaryDto,
} from '../../../types/settings';
import type { ServiceKey } from './ServicesTab';
import {
  CONNECTION_EXCLUDED_SECTION_NAMES,
  CONNECTION_OWNERSHIP_CATEGORIES,
} from '../constants/connectionSections';
import {
  SECRET_MASK,
  clonePayload,
  getErrorMessage,
  getInputTextValue,
  getSectionSchema,
  humanizeKey,
  mapChatProviderToSection,
  parseFieldValue,
  payloadSignature,
} from '../utils';
import { TextActionButton } from './shared/ActionButtons';

/**
 * Phase D (R-5.3 / R-5.5 / R-5.6 / R-8.1 / R-10.3): the Connections tab.
 *
 * Layout is a two-pane shell. Left pane groups provider sections by client-only
 * ownership category (see OWNERSHIP_CATEGORIES below) and shows a readiness dot
 * plus "Used by N services" count. Right pane is the editing details panel for
 * the selected section with required fields first, optional fields second, and
 * secret masking via `secretHasValue` identical to the old ProviderSectionsTab
 * (R-10.3).
 *
 * "Used by services" chips on the details panel are driven by the new backend
 * endpoint `GET /api/settings/connections/{section}/usage` so the UI does not
 * guess from indirect data (R-8.1).
 */

interface ConnectionsTabProps {
  focusedSection?: string | null;
  providerSections: SettingsSectionSummaryDto[];
  sectionSummariesError: string | null;
  onRefreshSectionSummaries: () => void;
  onOpenServiceConsumer: (serviceId: ServiceKey) => void;
  onOpenChatTarget: (modelId: string) => void;
}

type SectionReadiness = 'configured' | 'blocked' | 'unconfigured' | 'loading';

const KNOWN_SERVICE_KEYS: readonly ServiceKey[] = [
  'Embeddings',
  'ImageGeneration',
  'DocumentIntelligence',
  'SpeechTranscription',
  'SpeechSynthesis',
];

function toServiceKey(value: string): ServiceKey | null {
  return (KNOWN_SERVICE_KEYS as readonly string[]).includes(value)
    ? (value as ServiceKey)
    : null;
}

export function ConnectionsTab({
  focusedSection,
  providerSections,
  sectionSummariesError,
  onRefreshSectionSummaries,
  onOpenServiceConsumer,
  onOpenChatTarget,
}: ConnectionsTabProps) {
  const { showToast } = useToast();

  const [schema, setSchema] = useState<SettingsSchemaDto | null>(null);
  const [schemaError, setSchemaError] = useState<string | null>(null);

  const [overview, setOverview] = useState<SettingsOverviewDto | null>(null);
  const [overviewError, setOverviewError] = useState<string | null>(null);

  const [selectedSection, setSelectedSection] = useState<string | null>(null);

  const [sectionData, setSectionData] = useState<SettingsSectionDto | null>(null);
  const [sectionLoading, setSectionLoading] = useState(false);
  const [sectionError, setSectionError] = useState<string | null>(null);
  const [sectionDraft, setSectionDraft] = useState<Record<string, unknown>>({});
  const [sectionPreservedDraft, setSectionPreservedDraft] = useState<Record<string, unknown> | null>(null);
  const [sectionSaving, setSectionSaving] = useState(false);

  const [usage, setUsage] = useState<ConnectionUsageDto | null>(null);
  const [usageLoading, setUsageLoading] = useState(false);
  const [usageError, setUsageError] = useState<string | null>(null);

  const autoSelectedRef = useRef(false);

  const loadSchema = useCallback(async () => {
    setSchemaError(null);
    try {
      const next = await api.settings.getSchema();
      setSchema(next);
    } catch (error) {
      setSchemaError(getErrorMessage(error, 'Failed to load settings schema.'));
    }
  }, []);

  const loadOverview = useCallback(async () => {
    setOverviewError(null);
    try {
      const next = await api.settings.getOverview();
      setOverview(next);
    } catch (error) {
      setOverviewError(getErrorMessage(error, 'Failed to load settings overview.'));
    }
  }, []);

  useEffect(() => {
    void loadSchema();
    void loadOverview();
  }, [loadSchema, loadOverview]);

  const sectionSummariesByName = useMemo(() => {
    const map = new Map<string, SettingsSectionSummaryDto>();
    for (const summary of providerSections) {
      map.set(summary.sectionName, summary);
    }
    return map;
  }, [providerSections]);

  /**
   * Cheap "Used by N services" count for the left pane badge: sum of mode
   * references whose providerSection matches, plus chat targets whose provider
   * maps to a provider section. The full chip list on the right pane is driven
   * by the dedicated /connections/{section}/usage endpoint.
   */
  const usageCountBySection = useMemo(() => {
    const counts = new Map<string, number>();
    if (!overview) {
      return counts;
    }
    for (const rollup of overview.serviceModeReadiness) {
      for (const mode of rollup.modes) {
        if (mode.providerSection) {
          counts.set(mode.providerSection, (counts.get(mode.providerSection) ?? 0) + 1);
        }
      }
    }
    for (const target of overview.chatTargets.targets) {
      const section = mapChatProviderToSection(target.provider);
      if (section) {
        counts.set(section, (counts.get(section) ?? 0) + 1);
      }
    }
    return counts;
  }, [overview]);

  const resolvedReadiness = useCallback(
    (sectionName: string): SectionReadiness => {
      if (providerSections.length === 0 && !sectionSummariesError) {
        return 'loading';
      }
      const summary = sectionSummariesByName.get(sectionName);
      if (!summary) {
        return 'unconfigured';
      }

      switch (summary.readinessStatus) {
        case 'configured':
        case 'blocked':
        case 'unconfigured':
          return summary.readinessStatus;
        default:
          return 'unconfigured';
      }
    },
    [providerSections.length, sectionSummariesError, sectionSummariesByName]
  );

  const loadSection = useCallback(
    async (sectionName: string, options: { preserveDraftAsPending?: boolean } = {}) => {
      setSectionLoading(true);
      setSectionError(null);
      try {
        const next = await api.settings.getSection(sectionName);
        setSectionData(next);
        if (options.preserveDraftAsPending) {
          setSectionPreservedDraft(sectionDraft);
        } else {
          setSectionPreservedDraft(null);
        }
        setSectionDraft(clonePayload(next.payload));
      } catch (error) {
        setSectionError(getErrorMessage(error, `Failed to load section ${sectionName}.`));
        setSectionData(null);
        setSectionDraft({});
        setSectionPreservedDraft(null);
      } finally {
        setSectionLoading(false);
      }
    },
    [sectionDraft]
  );

  const loadUsage = useCallback(async (sectionName: string) => {
    setUsageLoading(true);
    setUsageError(null);
    try {
      const next = await api.settings.connections.getUsage(sectionName);
      setUsage(next);
    } catch (error) {
      setUsageError(getErrorMessage(error, `Failed to load usage for ${sectionName}.`));
      setUsage(null);
    } finally {
      setUsageLoading(false);
    }
  }, []);

  const handleSelectSection = useCallback(
    (sectionName: string) => {
      if (selectedSection === sectionName) {
        return;
      }
      setSelectedSection(sectionName);
      setSectionData(null);
      setSectionDraft({});
      setSectionPreservedDraft(null);
      setSectionError(null);
      setUsage(null);
      setUsageError(null);
      void loadSection(sectionName);
      void loadUsage(sectionName);
    },
    [loadSection, loadUsage, selectedSection]
  );

  useEffect(() => {
    if (autoSelectedRef.current) {
      return;
    }
    if (!focusedSection) {
      autoSelectedRef.current = true;
      return;
    }
    if (providerSections.length === 0) {
      return;
    }
    const exists = providerSections.some((summary) => summary.sectionName === focusedSection);
    if (exists) {
      handleSelectSection(focusedSection);
    }
    autoSelectedRef.current = true;
  }, [focusedSection, providerSections, handleSelectSection]);

  const handleUpdateField = useCallback((key: string, value: unknown) => {
    setSectionDraft((previous) => ({ ...previous, [key]: value }));
  }, []);

  const handleResetDraft = useCallback(() => {
    if (!sectionData) {
      return;
    }
    setSectionDraft(clonePayload(sectionData.payload));
  }, [sectionData]);

  const handleDiscardPreservedDraft = useCallback(() => {
    setSectionPreservedDraft(null);
  }, []);

  const handleReapplyPreservedDraft = useCallback(() => {
    if (!sectionPreservedDraft) {
      return;
    }
    setSectionDraft(sectionPreservedDraft);
    setSectionPreservedDraft(null);
  }, [sectionPreservedDraft]);

  const handleSave = useCallback(async () => {
    if (!sectionData || !selectedSection) {
      return;
    }
    setSectionSaving(true);
    try {
      const updated = await api.settings.updateSection(selectedSection, {
        rowVersion: sectionData.rowVersion,
        payload: sectionDraft,
      });
      setSectionData(updated);
      setSectionDraft(clonePayload(updated.payload));
      setSectionPreservedDraft(null);
      onRefreshSectionSummaries();
      showToast({ type: 'success', title: `Saved ${updated.displayName}` });
      void loadOverview();
      void loadUsage(selectedSection);
    } catch (error) {
      const status = (error as { status?: number })?.status;
      if (status === 409) {
        showToast({
          type: 'warning',
          title: 'Section was updated elsewhere',
          message: 'Latest server values are loaded and your draft was preserved.',
        });
        await loadSection(selectedSection, { preserveDraftAsPending: true });
        onRefreshSectionSummaries();
      } else {
        showToast({
          type: 'error',
          title: 'Failed to save section',
          message: getErrorMessage(error, 'Request failed.'),
        });
      }
    } finally {
      setSectionSaving(false);
    }
  }, [sectionData, selectedSection, sectionDraft, loadOverview, loadSection, loadUsage, onRefreshSectionSummaries, showToast]);

  const handleRefresh = useCallback(() => {
    if (!selectedSection) {
      return;
    }
    void loadSection(selectedSection);
    void loadUsage(selectedSection);
    void loadOverview();
    onRefreshSectionSummaries();
  }, [selectedSection, loadSection, loadUsage, loadOverview, onRefreshSectionSummaries]);

  const selectedSummary = selectedSection ? sectionSummariesByName.get(selectedSection) : undefined;
  const selectedSchema = selectedSection ? getSectionSchema(schema, selectedSection) : undefined;
  const isDirty = sectionData ? payloadSignature(sectionDraft) !== payloadSignature(sectionData.payload) : false;

  const { requiredProperties, optionalProperties } = useMemo(() => {
    if (!selectedSection || !selectedSchema) {
      return {
        requiredProperties: [] as SettingsSectionPropertyDefinitionDto[],
        optionalProperties: [] as SettingsSectionPropertyDefinitionDto[],
      };
    }
    const required: SettingsSectionPropertyDefinitionDto[] = [];
    const optional: SettingsSectionPropertyDefinitionDto[] = [];
    for (const property of selectedSchema.properties) {
      if (property.isRequired) {
        required.push(property);
      } else {
        optional.push(property);
      }
    }
    return { requiredProperties: required, optionalProperties: optional };
  }, [selectedSection, selectedSchema]);

  /**
   * Locally-managed tabIndex to guarantee required-first tab order (R-5.6):
   * required fields render with indices 1..N, optional with indices N+1..N+M.
   */
  const tabIndexFor = useCallback(
    (property: SettingsSectionPropertyDefinitionDto): number => {
      const requiredIndex = requiredProperties.indexOf(property);
      if (requiredIndex >= 0) {
        return requiredIndex + 1;
      }
      const optionalIndex = optionalProperties.indexOf(property);
      return requiredProperties.length + optionalIndex + 1;
    },
    [requiredProperties, optionalProperties]
  );

  return (
    <div className="flex flex-col gap-4">
      {sectionSummariesError && (
        <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
          <div className="font-medium">Could not load provider section catalog.</div>
          <div className="mt-1">{sectionSummariesError}</div>
          <div className="mt-2">
            <TextActionButton
              tone="neutral"
              icon={<FaRedo />}
              onClick={onRefreshSectionSummaries}
              title="Retry loading provider section catalog."
            >
              Retry
            </TextActionButton>
          </div>
        </div>
      )}

      {schemaError && (
        <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
          <div className="font-medium">Could not load settings schema.</div>
          <div className="mt-1">{schemaError}</div>
        </div>
      )}

      {overviewError && (
        <div className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900">
          <div className="font-medium">Readiness dots and usage counts are unavailable.</div>
          <div className="mt-1">{overviewError}</div>
        </div>
      )}

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-[18rem_1fr]">
        <aside className="space-y-4">
          {CONNECTION_OWNERSHIP_CATEGORIES.map((category) => {
            const entries = category.sectionNames
              .filter((name) => !CONNECTION_EXCLUDED_SECTION_NAMES.has(name))
              .map((name) => sectionSummariesByName.get(name))
              .filter((summary): summary is SettingsSectionSummaryDto => Boolean(summary));

            return (
              <section
                key={category.key}
                className="overflow-hidden rounded-lg border border-gray-200 bg-white"
              >
                <div className="border-b border-gray-200 px-4 py-3">
                  <h3 className="text-sm font-semibold text-gray-900">{category.label}</h3>
                  <p className="mt-1 text-xs text-gray-600">{category.description}</p>
                </div>
                {entries.length === 0 ? (
                  <div className="px-4 py-3 text-xs text-gray-500">
                    No provider sections registered for this category.
                  </div>
                ) : (
                  <ul className="divide-y divide-gray-200">
                    {entries.map((summary) => {
                      const readiness = resolvedReadiness(summary.sectionName);
                      const usageCount = usageCountBySection.get(summary.sectionName) ?? 0;
                      const isSelected = summary.sectionName === selectedSection;
                      return (
                        <li key={summary.sectionName}>
                          <button
                            type="button"
                            onClick={() => handleSelectSection(summary.sectionName)}
                            aria-current={isSelected ? 'true' : undefined}
                            className={`flex w-full items-center gap-3 px-4 py-3 text-left text-sm hover:bg-gray-50 ${
                              isSelected ? 'bg-blue-50' : ''
                            }`}
                          >
                            <ReadinessDot status={readiness} />
                            <div className="min-w-0 flex-1">
                              <div className="truncate font-medium text-gray-900">{summary.displayName}</div>
                              <div className="truncate font-mono text-xs text-gray-500">{summary.sectionName}</div>
                            </div>
                            <span className="shrink-0 rounded-full bg-gray-100 px-2 py-0.5 text-xs text-gray-700">
                              Used by {usageCount}
                            </span>
                          </button>
                        </li>
                      );
                    })}
                  </ul>
                )}
              </section>
            );
          })}

        </aside>

        <div className="space-y-4">
          {!selectedSection ? (
            <div className="rounded-lg border border-dashed border-gray-300 bg-white p-10 text-center text-sm text-gray-600">
              <p className="font-medium text-gray-900">Select a provider connection</p>
              <p className="mt-1">
                Pick a section from the left to edit credentials and see which services consume this connection.
              </p>
            </div>
          ) : (
            <DetailsPanel
              section={sectionData}
              summary={selectedSummary}
              sectionName={selectedSection}
              loading={sectionLoading}
              error={sectionError}
              saving={sectionSaving}
              draft={sectionDraft}
              preservedDraft={sectionPreservedDraft}
              isDirty={isDirty}
              requiredProperties={requiredProperties}
              optionalProperties={optionalProperties}
              usage={usage}
              usageLoading={usageLoading}
              usageError={usageError}
              onUpdateField={handleUpdateField}
              onReset={handleResetDraft}
              onSave={() => void handleSave()}
              onRefresh={handleRefresh}
              onReapplyPreservedDraft={handleReapplyPreservedDraft}
              onDiscardPreservedDraft={handleDiscardPreservedDraft}
              onOpenServiceConsumer={onOpenServiceConsumer}
              onOpenChatTarget={onOpenChatTarget}
              tabIndexFor={tabIndexFor}
            />
          )}
        </div>
      </div>
    </div>
  );
}

interface DetailsPanelProps {
  sectionName: string;
  summary?: SettingsSectionSummaryDto;
  section: SettingsSectionDto | null;
  loading: boolean;
  error: string | null;
  saving: boolean;
  draft: Record<string, unknown>;
  preservedDraft: Record<string, unknown> | null;
  isDirty: boolean;
  requiredProperties: SettingsSectionPropertyDefinitionDto[];
  optionalProperties: SettingsSectionPropertyDefinitionDto[];
  usage: ConnectionUsageDto | null;
  usageLoading: boolean;
  usageError: string | null;
  onUpdateField: (key: string, value: unknown) => void;
  onReset: () => void;
  onSave: () => void;
  onRefresh: () => void;
  onReapplyPreservedDraft: () => void;
  onDiscardPreservedDraft: () => void;
  onOpenServiceConsumer: (serviceId: ServiceKey) => void;
  onOpenChatTarget: (modelId: string) => void;
  tabIndexFor: (property: SettingsSectionPropertyDefinitionDto) => number;
}

function DetailsPanel({
  sectionName,
  summary,
  section,
  loading,
  error,
  saving,
  draft,
  preservedDraft,
  isDirty,
  requiredProperties,
  optionalProperties,
  usage,
  usageLoading,
  usageError,
  onUpdateField,
  onReset,
  onSave,
  onRefresh,
  onReapplyPreservedDraft,
  onDiscardPreservedDraft,
  onOpenServiceConsumer,
  onOpenChatTarget,
  tabIndexFor,
}: DetailsPanelProps) {
  const displayName = summary?.displayName ?? sectionName;
  return (
    <section className="overflow-hidden rounded-lg border border-gray-200 bg-white">
      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-gray-200 px-6 py-4">
        <div>
          <h2 className="text-base font-semibold text-gray-900">{displayName}</h2>
          <p className="text-xs text-gray-600">
            Section <span className="font-mono">{sectionName}</span>
          </p>
        </div>
        <TextActionButton
          tone="neutral"
          icon={loading ? <FaSpinner className="animate-spin" /> : <FaSyncAlt />}
          disabled={loading || saving}
          onClick={onRefresh}
          title="Refresh this section."
        >
          Refresh
        </TextActionButton>
      </div>

      <div className="space-y-4 px-6 py-5">
        {error && (
          <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{error}</div>
        )}

        {summary && summary.missingFields.length > 0 && (
          <div
            className={`rounded px-3 py-2 text-sm ${
              summary.readinessStatus === 'blocked'
                ? 'border border-amber-200 bg-amber-50 text-amber-900'
                : 'border border-slate-200 bg-slate-50 text-slate-800'
            }`}
          >
            <div className="font-medium">
              {summary.readinessStatus === 'blocked' ? 'Connection incomplete' : 'Connection not configured'}
            </div>
            <div className="mt-1">
              Missing: {summary.missingFields.join(', ')}
            </div>
          </div>
        )}

        {preservedDraft && (
          <div className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900">
            <div>Another update was saved first. Latest server values are loaded and your unsaved draft is preserved.</div>
            <div className="mt-2 flex flex-wrap gap-2">
              <TextActionButton
                tone="accent"
                icon={<FaReply />}
                onClick={onReapplyPreservedDraft}
                title="Reapply your preserved unsaved draft."
              >
                Reapply
              </TextActionButton>
              <TextActionButton
                tone="neutral"
                icon={<FaBan />}
                onClick={onDiscardPreservedDraft}
                title="Discard your preserved unsaved draft."
              >
                Discard
              </TextActionButton>
            </div>
          </div>
        )}

        <UsageChips
          usage={usage}
          loading={usageLoading}
          error={usageError}
          onOpenServiceConsumer={onOpenServiceConsumer}
          onOpenChatTarget={onOpenChatTarget}
        />

        {loading && !section ? (
          <LoadingSpinner message={`Loading ${sectionName}...`} />
        ) : section ? (
          <div className="grid grid-cols-1 gap-6 md:grid-cols-2">
            <div>
              <h3 className="mb-3 text-xs font-medium uppercase tracking-wide text-gray-500">
                Required fields
              </h3>
              {requiredProperties.length === 0 ? (
                <p className="text-xs text-gray-500">No required fields for this section.</p>
              ) : (
                <div className="space-y-4">
                  {requiredProperties.map((property) => (
                    <FieldEditor
                      key={property.name}
                      section={section}
                      property={property}
                      value={draft[property.name]}
                      disabled={saving || !property.isEditable}
                      onValueChange={onUpdateField}
                      tabIndex={tabIndexFor(property)}
                    />
                  ))}
                </div>
              )}
            </div>
            <div>
              <h3 className="mb-3 text-xs font-medium uppercase tracking-wide text-gray-500">
                Optional fields
              </h3>
              {optionalProperties.length === 0 ? (
                <p className="text-xs text-gray-500">No optional fields for this section.</p>
              ) : (
                <div className="space-y-4">
                  {optionalProperties.map((property) => (
                    <FieldEditor
                      key={property.name}
                      section={section}
                      property={property}
                      value={draft[property.name]}
                      disabled={saving || !property.isEditable}
                      onValueChange={onUpdateField}
                      tabIndex={tabIndexFor(property)}
                    />
                  ))}
                </div>
              )}
            </div>
          </div>
        ) : null}
      </div>

      <div className="flex justify-end gap-2 border-t border-gray-200 px-6 py-4">
        <TextActionButton
          tone="neutral"
          icon={<FaUndo />}
          disabled={!section || saving || loading || !isDirty}
          onClick={onReset}
          title="Discard edits and reload from server."
        >
          Reset
        </TextActionButton>
        <TextActionButton
          tone="primary"
          icon={saving ? <FaSpinner className="animate-spin" /> : <FaSave />}
          disabled={!section || saving || loading || !isDirty}
          onClick={onSave}
          title="Save this section."
        >
          Save
        </TextActionButton>
      </div>
    </section>
  );
}

interface FieldEditorProps {
  section: SettingsSectionDto;
  property: SettingsSectionPropertyDefinitionDto;
  value: unknown;
  disabled: boolean;
  onValueChange: (key: string, value: unknown) => void;
  tabIndex: number;
}

function FieldEditor({ section, property, value, disabled, onValueChange, tabIndex }: FieldEditorProps) {
  const fieldInputId = `${section.sectionName}-${property.name}`;
  return (
    <div className="space-y-2">
      <label
        htmlFor={fieldInputId}
        className="block text-xs font-medium uppercase tracking-wide text-gray-600"
      >
        {humanizeKey(property.name)}
      </label>

      {property.valueType === 'bool' ? (
        <label className="inline-flex items-center gap-2 text-sm text-gray-800">
          <input
            id={fieldInputId}
            type="checkbox"
            checked={Boolean(value)}
            onChange={(event) => onValueChange(property.name, event.target.checked)}
            className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
            disabled={disabled}
            tabIndex={tabIndex}
          />
          Enabled
        </label>
      ) : (
        <input
          id={fieldInputId}
          type={property.isSecret ? 'password' : property.valueType === 'int' ? 'number' : 'text'}
          value={getInputTextValue(value)}
          onChange={(event) => onValueChange(property.name, parseFieldValue(event.target.value, property))}
          className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 disabled:bg-gray-100"
          autoComplete="off"
          spellCheck={false}
          disabled={disabled}
          tabIndex={tabIndex}
        />
      )}

      {property.isSecret && (
        <p className="text-xs text-gray-500">
          {section.secretHasValue?.[property.name]
            ? `A secret is stored. Keep ${SECRET_MASK} to preserve it, or type a new value.`
            : 'No secret stored yet. Enter a value to save one.'}
        </p>
      )}
    </div>
  );
}

interface UsageChipsProps {
  usage: ConnectionUsageDto | null;
  loading: boolean;
  error: string | null;
  onOpenServiceConsumer: (serviceId: ServiceKey) => void;
  onOpenChatTarget: (modelId: string) => void;
}

function UsageChips({
  usage,
  loading,
  error,
  onOpenServiceConsumer,
  onOpenChatTarget,
}: UsageChipsProps) {
  if (loading) {
    return <div className="text-xs text-gray-500">Loading consumers...</div>;
  }
  if (error) {
    return (
      <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
        <div className="font-medium">Could not load consumers.</div>
        <div className="mt-1">{error}</div>
      </div>
    );
  }
  if (!usage) {
    return null;
  }
  if (usage.modes.length === 0 && usage.chatTargets.length === 0) {
    return (
      <div className="rounded border border-gray-200 bg-gray-50 px-3 py-2 text-xs text-gray-600">
        No service mappings or active assistant-referenced models reference this connection yet.
      </div>
    );
  }
  return (
    <div className="space-y-2">
      <div className="text-xs font-medium uppercase tracking-wide text-gray-500">Used by services</div>
      <div className="flex flex-wrap gap-2 text-xs">
        {usage.modes.map((mode) => {
          const serviceKey = toServiceKey(mode.service);
          const canOpenService = Boolean(serviceKey);
          return (
            <button
              key={`mode:${mode.service}:${mode.modeId}`}
              type="button"
              onClick={() => {
                if (serviceKey) {
                  onOpenServiceConsumer(serviceKey);
                }
              }}
              disabled={!canOpenService}
              className={`inline-flex items-center gap-1 rounded-full border px-3 py-1 ${
                canOpenService
                  ? 'border-blue-200 bg-blue-50 text-blue-800 hover:bg-blue-100'
                  : 'cursor-not-allowed border-gray-200 bg-gray-100 text-gray-500'
              }`}
              title={
                canOpenService
                  ? `Open ${mode.service} on the Services tab`
                  : `Service '${mode.service}' is not available on the Services tab`
              }
            >
              <span className="font-medium">{mode.service}</span>
              <span className="text-blue-600">/</span>
              <span className="font-mono">{mode.modeId}</span>
              {mode.isDefault && (
                <span className="ml-1 rounded bg-blue-600 px-1 text-[10px] uppercase tracking-wide text-white">
                  default
                </span>
              )}
            </button>
          );
        })}
        {usage.chatTargets.map((target) => (
          <button
            key={`chat:${target.modelId}`}
            type="button"
            onClick={() => onOpenChatTarget(target.modelId)}
            className="inline-flex items-center gap-1 rounded-full border border-emerald-200 bg-emerald-50 px-3 py-1 text-emerald-800 hover:bg-emerald-100"
            title={`Open chat target ${target.modelId} in Models & Runtime`}
          >
            <span className="font-mono">{target.modelId}</span>
            <span className="ml-1 rounded bg-emerald-600 px-1 text-[10px] tracking-wide text-white">
              {target.assistantCount} assistant{target.assistantCount === 1 ? '' : 's'}
            </span>
          </button>
        ))}
      </div>
    </div>
  );
}

function ReadinessDot({ status }: { status: SectionReadiness }) {
  const className =
    status === 'configured'
      ? 'bg-emerald-500'
      : status === 'blocked'
        ? 'bg-red-500'
        : status === 'loading'
          ? 'bg-gray-300 animate-pulse'
          : 'bg-gray-300';
  const title =
    status === 'configured'
      ? 'Configured, no blockers'
      : status === 'blocked'
        ? 'Configured but has blockers'
        : status === 'loading'
          ? 'Readiness loading...'
          : 'Unconfigured';
  return <span className={`inline-block h-2.5 w-2.5 shrink-0 rounded-full ${className}`} title={title} aria-label={title} />;
}
