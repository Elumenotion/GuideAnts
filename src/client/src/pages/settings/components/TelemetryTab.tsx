import { useCallback, useEffect, useMemo, useState } from 'react';
import { FaChevronDown, FaChevronRight, FaSave, FaSyncAlt } from 'react-icons/fa';
import LoadingSpinner from '../../../components/LoadingSpinner';
import { useToast } from '../../../components/common/Toast';
import { api } from '../../../services/api';
import { SettingsSectionDto } from '../../../types/settings';
import { TextActionButton } from './shared/ActionButtons';

type LogLevel = 'Trace' | 'Debug' | 'Information' | 'Warning' | 'Error' | 'Critical' | 'None';
type Preset = 'quiet' | 'normal' | 'investigating' | 'verbose';

interface TelemetryCategory {
  key: string;
  label: string;
  category: string;
  defaultLevel: LogLevel;
}

interface TelemetrySubsystem {
  id: string;
  label: string;
  summary: string;
  categoryKeys: string[];
  presets: Record<Preset, Partial<Record<string, LogLevel>>>;
}

const LOG_LEVELS: LogLevel[] = ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical', 'None'];

const CATEGORIES: TelemetryCategory[] = [
  { key: 'Default', label: 'Default', category: 'Default', defaultLevel: 'Warning' },
  { key: 'MicrosoftAspNetCore', label: 'ASP.NET Core', category: 'Microsoft.AspNetCore', defaultLevel: 'Warning' },
  { key: 'MicrosoftEntityFrameworkCore', label: 'EF Core', category: 'Microsoft.EntityFrameworkCore', defaultLevel: 'Warning' },
  {
    key: 'MicrosoftEntityFrameworkCoreDatabaseCommand',
    label: 'EF SQL commands',
    category: 'Microsoft.EntityFrameworkCore.Database.Command',
    defaultLevel: 'Warning',
  },
  {
    key: 'MicrosoftEntityFrameworkCoreQuery',
    label: 'EF query warnings',
    category: 'Microsoft.EntityFrameworkCore.Query',
    defaultLevel: 'Warning',
  },
  { key: 'Program', label: 'API startup and exception handler', category: 'Program', defaultLevel: 'Information' },
  { key: 'GuideAntsApi', label: 'GuideAnts API', category: 'GuideAntsApi', defaultLevel: 'Information' },
  {
    key: 'GuideAntsApiBackgroundJobs',
    label: 'Background jobs',
    category: 'GuideAntsApi.BackgroundJobs',
    defaultLevel: 'Information',
  },
  {
    key: 'GuideAntsApiBackgroundJobHandlers',
    label: 'Background job handlers',
    category: 'GuideAntsApi.BackgroundJobs.Jobs',
    defaultLevel: 'Information',
  },
  {
    key: 'DocumentIntelligenceService',
    label: 'Document intelligence extraction',
    category: 'GuideAntsApi.BackgroundJobs.Services.DocumentIntelligenceService',
    defaultLevel: 'Information',
  },
  {
    key: 'EmbeddingServices',
    label: 'Embedding providers',
    category: 'GuideAntsApi.BackgroundJobs.Services.Embeddings',
    defaultLevel: 'Information',
  },
  {
    key: 'IndexingServices',
    label: 'Indexing services',
    category: 'GuideAntsApi.BackgroundJobs.Services.Indexing',
    defaultLevel: 'Information',
  },
  {
    key: 'SearchServices',
    label: 'Hybrid search services',
    category: 'GuideAntsApi.BackgroundJobs.Services.Search',
    defaultLevel: 'Information',
  },
  {
    key: 'GuideAntsApiServicesRouting',
    label: 'Routing services',
    category: 'GuideAntsApi.Services.Routing',
    defaultLevel: 'Information',
  },
  {
    key: 'RoutingChatCompletionClientFactory',
    label: 'Chat route resolver',
    category: 'GuideAntsApi.Services.Conversations.RoutingChatCompletionClientFactory',
    defaultLevel: 'Information',
  },
  {
    key: 'GuideAntsApiServicesLlamaCpp',
    label: 'Llama runtime services',
    category: 'GuideAntsApi.Services.LlamaCpp',
    defaultLevel: 'Information',
  },
  {
    key: 'InfrastructureProbeService',
    label: 'Infrastructure probes',
    category: 'GuideAntsApi.Services.Infrastructure.InfrastructureProbeService',
    defaultLevel: 'Warning',
  },
  {
    key: 'SpeechTranscriptionService',
    label: 'Speech transcription',
    category: 'GuideAntsApi.Services.Components.SpeechTranscriptionService',
    defaultLevel: 'Information',
  },
  {
    key: 'SpeechSynthesisService',
    label: 'Speech synthesis',
    category: 'GuideAntsApi.Services.Components.SpeechSynthesisService',
    defaultLevel: 'Information',
  },
  {
    key: 'VideoAudioExtractionService',
    label: 'Video/audio extraction',
    category: 'GuideAntsApi.Services.Components.VideoAudioExtractionService',
    defaultLevel: 'Information',
  },
  {
    key: 'NotebookImageService',
    label: 'Image generation',
    category: 'GuideAntsApi.Services.NotebookImageService',
    defaultLevel: 'Information',
  },
  {
    key: 'SearXngWebSearchService',
    label: 'SearXNG search',
    category: 'GuideAntsApi.Services.SearXngWebSearchService',
    defaultLevel: 'Information',
  },
  {
    key: 'SearXngBrowserRenderingClient',
    label: 'Browser rendering',
    category: 'GuideAntsApi.Services.SearXngBrowserRenderingClient',
    defaultLevel: 'Warning',
  },
  {
    key: 'WebScrapingService',
    label: 'Web scraping',
    category: 'GuideAntsApi.Services.WebScrapingService',
    defaultLevel: 'Information',
  },
  {
    key: 'StoragePathResolver',
    label: 'Storage path resolver',
    category: 'GuideAntsApi.Services.StoragePathResolver',
    defaultLevel: 'Warning',
  },
  {
    key: 'NotebookFileService',
    label: 'Notebook files',
    category: 'GuideAntsApi.Services.Components.NotebookFileService',
    defaultLevel: 'Warning',
  },
  {
    key: 'NotebookFileSyncService',
    label: 'Notebook file sync',
    category: 'GuideAntsApi.Services.Components.NotebookFileSyncService',
    defaultLevel: 'Warning',
  },
  {
    key: 'ContentFileService',
    label: 'Content files',
    category: 'GuideAntsApi.Services.Components.ContentFileService',
    defaultLevel: 'Warning',
  },
  { key: 'GuideAntsUsage', label: 'Usage records', category: 'GuideAnts.Usage', defaultLevel: 'Warning' },
  { key: 'AntRunner', label: 'AntRunner', category: 'AntRunner', defaultLevel: 'Warning' },
  { key: 'AntRunnerChat', label: 'AntRunner chat', category: 'AntRunner.Chat', defaultLevel: 'Warning' },
  {
    key: 'AntRunnerChatLlamaCpp',
    label: 'AntRunner llama client',
    category: 'AntRunner.Chat.LlamaCpp',
    defaultLevel: 'Warning',
  },
];

const categoryByKey = new Map(CATEGORIES.map((category) => [category.key, category]));

function levelsFor(keys: string[], quiet: LogLevel, normal: LogLevel, investigating: LogLevel, verbose: LogLevel) {
  return {
    quiet: Object.fromEntries(keys.map((key) => [key, quiet])),
    normal: Object.fromEntries(keys.map((key) => [key, normal])),
    investigating: Object.fromEntries(keys.map((key) => [key, investigating])),
    verbose: Object.fromEntries(keys.map((key) => [key, verbose])),
  } as Record<Preset, Partial<Record<string, LogLevel>>>;
}

const SUBSYSTEMS: TelemetrySubsystem[] = [
  {
    id: 'api',
    label: 'API requests',
    summary: 'Startup, request failures, and framework request diagnostics.',
    categoryKeys: ['Default', 'MicrosoftAspNetCore', 'Program', 'GuideAntsApi'],
    presets: {
      quiet: { Default: 'Error', MicrosoftAspNetCore: 'Error', Program: 'Warning', GuideAntsApi: 'Warning' },
      normal: { Default: 'Warning', MicrosoftAspNetCore: 'Warning', Program: 'Information', GuideAntsApi: 'Information' },
      investigating: { Default: 'Information', MicrosoftAspNetCore: 'Information', Program: 'Information', GuideAntsApi: 'Information' },
      verbose: { Default: 'Debug', MicrosoftAspNetCore: 'Debug', Program: 'Debug', GuideAntsApi: 'Debug' },
    },
  },
  {
    id: 'chat-routing',
    label: 'Chat routing',
    summary: 'Model/provider resolution and routing problem details.',
    categoryKeys: ['GuideAntsApiServicesRouting', 'RoutingChatCompletionClientFactory'],
    presets: levelsFor(['GuideAntsApiServicesRouting', 'RoutingChatCompletionClientFactory'], 'Error', 'Information', 'Debug', 'Trace'),
  },
  {
    id: 'chat-providers',
    label: 'Chat providers',
    summary: 'Provider client outcomes for OpenAI, Anthropic, Gemini, Hugging Face, OpenRouter, and llama.',
    categoryKeys: ['AntRunner', 'AntRunnerChat', 'AntRunnerChatLlamaCpp'],
    presets: levelsFor(['AntRunner', 'AntRunnerChat', 'AntRunnerChatLlamaCpp'], 'Error', 'Warning', 'Information', 'Debug'),
  },
  {
    id: 'llama',
    label: 'Local llama runtime',
    summary: 'Router aliases, runtime inventory, load/unload, and llama client diagnostics.',
    categoryKeys: ['GuideAntsApiServicesLlamaCpp', 'AntRunnerChatLlamaCpp'],
    presets: levelsFor(['GuideAntsApiServicesLlamaCpp', 'AntRunnerChatLlamaCpp'], 'Error', 'Information', 'Debug', 'Trace'),
  },
  {
    id: 'jobs',
    label: 'Background jobs',
    summary: 'Queue claims, completions, retries, leases, and permanent failures.',
    categoryKeys: ['GuideAntsApiBackgroundJobs', 'GuideAntsApiBackgroundJobHandlers'],
    presets: levelsFor(['GuideAntsApiBackgroundJobs', 'GuideAntsApiBackgroundJobHandlers'], 'Error', 'Information', 'Debug', 'Trace'),
  },
  {
    id: 'documents',
    label: 'Document extraction',
    summary: 'Markdown extraction and document intelligence conversion flow.',
    categoryKeys: ['DocumentIntelligenceService', 'GuideAntsApiBackgroundJobHandlers'],
    presets: levelsFor(['DocumentIntelligenceService', 'GuideAntsApiBackgroundJobHandlers'], 'Error', 'Information', 'Debug', 'Trace'),
  },
  {
    id: 'embeddings-indexing',
    label: 'Embeddings and indexing',
    summary: 'Embedding providers, chunk indexing, rebuilds, and hybrid search failures.',
    categoryKeys: ['EmbeddingServices', 'IndexingServices', 'SearchServices'],
    presets: levelsFor(['EmbeddingServices', 'IndexingServices', 'SearchServices'], 'Error', 'Information', 'Debug', 'Trace'),
  },
  {
    id: 'speech',
    label: 'Speech and media',
    summary: 'ASR, TTS, podcast generation, and video/audio extraction.',
    categoryKeys: ['SpeechTranscriptionService', 'SpeechSynthesisService', 'VideoAudioExtractionService'],
    presets: levelsFor(['SpeechTranscriptionService', 'SpeechSynthesisService', 'VideoAudioExtractionService'], 'Error', 'Information', 'Debug', 'Trace'),
  },
  {
    id: 'images',
    label: 'Image generation',
    summary: 'Image provider calls, edit failures, output size, and selected dimensions.',
    categoryKeys: ['NotebookImageService'],
    presets: levelsFor(['NotebookImageService'], 'Error', 'Information', 'Debug', 'Trace'),
  },
  {
    id: 'search',
    label: 'Search and browser rendering',
    summary: 'SearXNG queries, browser render failures, and markdown fetch errors.',
    categoryKeys: ['SearXngWebSearchService', 'SearXngBrowserRenderingClient', 'WebScrapingService'],
    presets: levelsFor(['SearXngWebSearchService', 'SearXngBrowserRenderingClient', 'WebScrapingService'], 'Error', 'Information', 'Debug', 'Trace'),
  },
  {
    id: 'infrastructure',
    label: 'Infrastructure probes',
    summary: 'Settings Infrastructure reachability and path writability diagnostics.',
    categoryKeys: ['InfrastructureProbeService'],
    presets: levelsFor(['InfrastructureProbeService'], 'Error', 'Warning', 'Debug', 'Trace'),
  },
  {
    id: 'storage',
    label: 'File storage',
    summary: 'Storage path recovery, notebook sync, content files, and missing physical files.',
    categoryKeys: ['StoragePathResolver', 'NotebookFileService', 'NotebookFileSyncService', 'ContentFileService'],
    presets: levelsFor(['StoragePathResolver', 'NotebookFileService', 'NotebookFileSyncService', 'ContentFileService'], 'Error', 'Warning', 'Information', 'Debug'),
  },
  {
    id: 'database',
    label: 'Database and EF Core',
    summary: 'EF warnings, query-shape diagnostics, and short-lived SQL command visibility.',
    categoryKeys: ['MicrosoftEntityFrameworkCore', 'MicrosoftEntityFrameworkCoreDatabaseCommand', 'MicrosoftEntityFrameworkCoreQuery'],
    presets: levelsFor(['MicrosoftEntityFrameworkCore', 'MicrosoftEntityFrameworkCoreDatabaseCommand', 'MicrosoftEntityFrameworkCoreQuery'], 'Error', 'Warning', 'Information', 'Debug'),
  },
  {
    id: 'usage',
    label: 'Usage and agent records',
    summary: 'Usage attribution warnings and agent invocation recording diagnostics.',
    categoryKeys: ['GuideAntsUsage'],
    presets: levelsFor(['GuideAntsUsage'], 'Error', 'Warning', 'Debug', 'Trace'),
  },
];

const PRESET_LABELS: Record<Preset, string> = {
  quiet: 'Quiet',
  normal: 'Normal',
  investigating: 'Investigating',
  verbose: 'Verbose',
};

function asLogLevel(value: unknown, fallback: LogLevel): LogLevel {
  return typeof value === 'string' && LOG_LEVELS.includes(value as LogLevel) ? (value as LogLevel) : fallback;
}

function sectionToDraft(section: SettingsSectionDto): Record<string, LogLevel> {
  return Object.fromEntries(
    CATEGORIES.map((category) => [category.key, asLogLevel(section.payload[category.key], category.defaultLevel)])
  );
}

function isPresetActive(subsystem: TelemetrySubsystem, preset: Preset, draft: Record<string, LogLevel>): boolean {
  const presetValues = subsystem.presets[preset];
  return subsystem.categoryKeys.every((key) => presetValues[key] === draft[key]);
}

function currentPresetLabel(subsystem: TelemetrySubsystem, draft: Record<string, LogLevel>): string {
  const match = (Object.keys(PRESET_LABELS) as Preset[]).find((preset) => isPresetActive(subsystem, preset, draft));
  return match ? PRESET_LABELS[match] : 'Custom';
}

function dirtyEquals(left: Record<string, LogLevel>, right: Record<string, LogLevel>): boolean {
  return CATEGORIES.every((category) => left[category.key] === right[category.key]);
}

export function TelemetryTab() {
  const { showToast } = useToast();
  const [section, setSection] = useState<SettingsSectionDto | null>(null);
  const [initialDraft, setInitialDraft] = useState<Record<string, LogLevel> | null>(null);
  const [draft, setDraft] = useState<Record<string, LogLevel> | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [advancedOpen, setAdvancedOpen] = useState(false);

  const loadTelemetry = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    try {
      const nextSection = await api.settings.getSection('Telemetry');
      const nextDraft = sectionToDraft(nextSection);
      setSection(nextSection);
      setInitialDraft(nextDraft);
      setDraft(nextDraft);
    } catch (error) {
      setLoadError(error instanceof Error ? error.message : 'Failed to load telemetry settings.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadTelemetry();
  }, [loadTelemetry]);

  const isDirty = useMemo(() => {
    if (!draft || !initialDraft) {
      return false;
    }

    return !dirtyEquals(draft, initialDraft);
  }, [draft, initialDraft]);

  const applyPreset = useCallback((subsystem: TelemetrySubsystem, preset: Preset) => {
    setDraft((previous) => {
      if (!previous) {
        return previous;
      }

      return {
        ...previous,
        ...(subsystem.presets[preset] as Record<string, LogLevel>),
      };
    });
  }, []);

  const updateCategory = useCallback((key: string, value: LogLevel) => {
    setDraft((previous) => previous ? { ...previous, [key]: value } : previous);
  }, []);

  const resetToBaseline = useCallback(() => {
    setDraft(Object.fromEntries(CATEGORIES.map((category) => [category.key, category.defaultLevel])));
  }, []);

  const saveTelemetry = useCallback(async () => {
    if (!section || !draft) {
      return;
    }

    setSaving(true);
    try {
      const saved = await api.settings.updateSection('Telemetry', {
        rowVersion: section.rowVersion,
        payload: draft,
      });
      const nextDraft = sectionToDraft(saved);
      setSection(saved);
      setInitialDraft(nextDraft);
      setDraft(nextDraft);
      showToast({ type: 'success', title: 'Telemetry settings saved', message: 'API logging uses the new settings immediately.' });
    } catch (error) {
      const status = (error as { status?: number })?.status;
      showToast({
        type: 'error',
        title: status === 409 ? 'Telemetry settings changed elsewhere' : 'Failed to save telemetry settings',
        message: status === 409
          ? 'Refresh the Telemetry tab and try again.'
          : error instanceof Error
            ? error.message
            : 'The telemetry update request failed.',
      });
    } finally {
      setSaving(false);
    }
  }, [draft, section, showToast]);

  if (loading) {
    return (
      <section className="rounded-lg border border-gray-200 bg-white p-8">
        <LoadingSpinner message="Loading telemetry settings..." />
      </section>
    );
  }

  if (loadError || !draft) {
    return (
      <section className="rounded-lg border border-red-200 bg-red-50 p-6">
        <h2 className="text-base font-semibold text-red-900">Telemetry settings unavailable</h2>
        <p className="mt-1 text-sm text-red-700">{loadError ?? 'Telemetry settings could not be loaded.'}</p>
        <div className="mt-4">
          <TextActionButton tone="neutral" icon={<FaSyncAlt />} onClick={() => void loadTelemetry()}>
            Retry
          </TextActionButton>
        </div>
      </section>
    );
  }

  return (
    <section className="space-y-5">
      <div className="rounded-lg border border-gray-200 bg-white">
        <div className="flex flex-col gap-3 border-b border-gray-200 px-6 py-4 md:flex-row md:items-center md:justify-between">
          <div>
            <h2 className="text-base font-semibold text-gray-900">Telemetry</h2>
            <p className="mt-1 text-sm text-gray-600">
              Configure API visibility without restarting the container. These controls affect the GuideAnts API process.
            </p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <TextActionButton tone="neutral" icon={<FaSyncAlt />} onClick={resetToBaseline} disabled={saving}>
              Baseline
            </TextActionButton>
            <TextActionButton tone="neutral" icon={<FaSyncAlt />} onClick={() => void loadTelemetry()} disabled={saving}>
              Refresh
            </TextActionButton>
            <TextActionButton tone="primary" icon={<FaSave />} onClick={() => void saveTelemetry()} disabled={!isDirty || saving}>
              {saving ? 'Saving...' : 'Save'}
            </TextActionButton>
          </div>
        </div>
        <div className="grid gap-4 px-6 py-4 md:grid-cols-3">
          <div>
            <div className="text-xs font-medium uppercase tracking-wide text-gray-500">Baseline</div>
            <div className="mt-1 text-sm text-gray-900">Framework Warning, GuideAnts Information, providers Warning</div>
          </div>
          <div>
            <div className="text-xs font-medium uppercase tracking-wide text-gray-500">Apply behavior</div>
            <div className="mt-1 text-sm text-gray-900">Saved changes reload API logging immediately</div>
          </div>
          <div>
            <div className="text-xs font-medium uppercase tracking-wide text-gray-500">State</div>
            <div className={`mt-1 text-sm font-medium ${isDirty ? 'text-amber-700' : 'text-emerald-700'}`}>
              {isDirty ? 'Unsaved changes' : 'Saved'}
            </div>
          </div>
        </div>
      </div>

      <div className="overflow-hidden rounded-lg border border-gray-200 bg-white">
        <div className="border-b border-gray-200 px-6 py-4">
          <h3 className="text-sm font-semibold text-gray-900">Subsystem Visibility</h3>
        </div>
        <div className="divide-y divide-gray-200">
          {SUBSYSTEMS.map((subsystem) => (
            <div key={subsystem.id} className="grid gap-4 px-6 py-4 lg:grid-cols-[minmax(0,1fr)_auto] lg:items-center">
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-2">
                  <h4 className="text-sm font-semibold text-gray-900">{subsystem.label}</h4>
                  <span className="rounded bg-gray-100 px-2 py-0.5 text-xs font-medium text-gray-600">
                    {currentPresetLabel(subsystem, draft)}
                  </span>
                </div>
                <p className="mt-1 text-sm text-gray-600">{subsystem.summary}</p>
                <div className="mt-2 flex flex-wrap gap-1.5">
                  {subsystem.categoryKeys.map((key) => {
                    const category = categoryByKey.get(key);
                    return category ? (
                      <span key={key} className="rounded bg-slate-100 px-1.5 py-0.5 text-[11px] font-mono text-slate-700">
                        {category.category}: {draft[key]}
                      </span>
                    ) : null;
                  })}
                </div>
              </div>
              <div className="grid grid-cols-2 gap-2 sm:flex sm:flex-wrap sm:justify-end">
                {(Object.keys(PRESET_LABELS) as Preset[]).map((preset) => (
                  <button
                    key={preset}
                    type="button"
                    aria-label={`${subsystem.label} ${PRESET_LABELS[preset]}`}
                    onClick={() => applyPreset(subsystem, preset)}
                    className={`h-8 rounded-md border px-3 text-xs font-medium ${
                      isPresetActive(subsystem, preset, draft)
                        ? 'border-blue-600 bg-blue-600 text-white'
                        : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-50'
                    }`}
                  >
                    {PRESET_LABELS[preset]}
                  </button>
                ))}
              </div>
            </div>
          ))}
        </div>
      </div>

      <div className="overflow-hidden rounded-lg border border-gray-200 bg-white">
        <button
          type="button"
          onClick={() => setAdvancedOpen((value) => !value)}
          className="flex w-full items-center gap-2 border-b border-gray-200 px-6 py-4 text-left text-sm font-semibold text-gray-900 hover:bg-gray-50"
          aria-expanded={advancedOpen}
        >
          {advancedOpen ? <FaChevronDown aria-hidden /> : <FaChevronRight aria-hidden />}
          Advanced Categories
        </button>
        {advancedOpen && (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-gray-200">
              <thead className="bg-gray-50">
                <tr>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Category</th>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Key</th>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Level</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200 bg-white">
                {CATEGORIES.map((category) => (
                  <tr key={category.key}>
                    <td className="px-4 py-3 align-top text-sm">
                      <div className="font-medium text-gray-900">{category.label}</div>
                      <div className="mt-0.5 font-mono text-xs text-gray-500">{category.category}</div>
                    </td>
                    <td className="px-4 py-3 align-top font-mono text-xs text-gray-600">{category.key}</td>
                    <td className="px-4 py-3 align-top">
                      <select
                        value={draft[category.key]}
                        onChange={(event) => updateCategory(category.key, event.target.value as LogLevel)}
                        className="h-8 rounded border border-gray-300 bg-white px-2 text-sm"
                        aria-label={`${category.label} log level`}
                      >
                        {LOG_LEVELS.map((level) => (
                          <option key={level} value={level}>
                            {level}
                          </option>
                        ))}
                      </select>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </section>
  );
}
