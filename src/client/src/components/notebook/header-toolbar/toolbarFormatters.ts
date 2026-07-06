import { cloneElement, isValidElement, type ReactElement } from 'react';
import type { NotebookToolbarProviderOptionDto, NotebookToolbarServiceDto } from '../../../types/notebookToolbar';

export const WORKSPACE_CONTROLS_COPY =
  'Workspace controls apply to this entire workspace, not one notebook.';

export type ToolbarServiceColorKey = 'chat' | 'image' | 'tts' | 'asr';

/** Icon-only color (buttons use `NEUTRAL_CHROME`). */
const TOOLBAR_GLYPH: Record<ToolbarServiceColorKey, string> = {
  chat: 'text-green-600',
  image: 'text-[#5A4528]',
  tts: 'text-red-600',
  asr: 'text-[#A68B5B]',
};

const NEUTRAL_CHROME =
  'border-slate-300 bg-white text-slate-700 hover:border-slate-400 hover:bg-slate-50';

const EXPANDED_RING = 'ring-2 ring-slate-300/80 ring-offset-1 ring-offset-white';

export function toolbarServiceIconGlyphClass(id: ToolbarServiceColorKey): string {
  return [
    'w-4 h-4 shrink-0',
    TOOLBAR_GLYPH[id],
    '[&>svg]:block [&>svg]:shrink-0',
    // Fa + Gi: paths inherit the vivid text color
    '[&>svg]:fill-current [&>svg>path]:fill-current [&>svg>g>path]:fill-current',
  ].join(' ');
}

/** Apply vivid glyph color classes to a react-icon element. */
export function withToolbarServiceIcon(
  id: ToolbarServiceColorKey,
  icon: ReactElement
): ReactElement {
  if (!isValidElement(icon)) return icon;
  const glyph = toolbarServiceIconGlyphClass(id);
  const prev = (icon.props as { className?: string }).className;
  return cloneElement(icon, {
    className: [glyph, prev].filter(Boolean).join(' ').trim(),
    'aria-hidden': true,
  } as Partial<unknown>);
}

const TOOLBAR_SERVICE_BASE =
  'inline-flex items-center justify-center rounded-md border shadow-sm transition-all duration-150 ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-1 ' +
  'disabled:cursor-not-allowed disabled:opacity-45 active:scale-[0.98]';

/** Square service control: shared neutral frame; `id` is unused for chrome (icons use TOOLBAR_GLYPH). */
export function toolbarServiceButtonClass(
  _id: ToolbarServiceColorKey,
  options: { expanded: boolean; minSize: 'md' | 'sm' }
): string {
  const min = options.minSize === 'sm' ? 'min-w-[2.5rem] min-h-[2.5rem] p-1.5' : 'min-w-[2.25rem] min-h-[2.25rem] p-1.5';
  const open = options.expanded ? EXPANDED_RING : '';
  return [TOOLBAR_SERVICE_BASE, min, NEUTRAL_CHROME, open, 'relative shrink-0 flex'].join(' ');
}

/** Optional wrapper for sheet / headers (icon + label rows). */
export function toolbarServiceIconHeaderClass(id: ToolbarServiceColorKey): string {
  return `inline-flex items-center justify-center ${TOOLBAR_GLYPH[id]} [&>svg]:!text-inherit [&>svg]:fill-current`;
}

export function toolbarServiceStatusDotBorderClass(_id: ToolbarServiceColorKey): string {
  return 'border-white';
}

/** Refresh stays neutral so it does not read as a fifth “service.” */
export function toolbarRefreshButtonClass(mobile: boolean): string {
  const size = mobile ? 'min-w-[2.5rem] min-h-[2.5rem]' : 'min-w-[2.25rem] min-h-[2.25rem]';
  return (
    'inline-flex shrink-0 items-center justify-center rounded-md border border-slate-300/90 bg-slate-50 p-1.5 ' +
    `${size} text-slate-600 shadow-sm transition-all hover:bg-slate-100/90 ` +
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-1 ' +
    'active:scale-[0.98] disabled:cursor-not-allowed disabled:opacity-45 ' +
    'relative z-10'
  );
}

export function statusToneClass(status: string): string {
  const normalized = status.trim().toLowerCase();
  if (normalized === 'ready') return 'text-emerald-700';
  if (normalized === 'blocked') return 'text-red-700';
  if (normalized === 'off' || normalized === 'requiresload') return 'text-slate-600';
  if (normalized === 'inprogress' || normalized === 'in progress') return 'text-blue-700';
  return 'text-amber-700';
}

export function statusDotClass(status: string): string {
  const normalized = status.trim().toLowerCase();
  if (normalized === 'ready') return 'bg-emerald-500';
  if (normalized === 'blocked') return 'bg-red-500';
  if (normalized === 'off' || normalized === 'requiresload') return 'bg-slate-400';
  if (normalized === 'inprogress' || normalized === 'in progress') return 'bg-blue-500';
  return 'bg-amber-500';
}

export function serviceSummaryLine(service: NotebookToolbarServiceDto): string {
  const active = service.providerOptions.find(p => p.providerId === service.activeProviderId);
  if (!active) return service.summary;
  const label = toolbarProviderOptionLabel(active);
  if (label === 'Local' && service.localModelOptions.length > 0) {
    const activeModel = service.localModelOptions.find(m => m.isActive);
    if (activeModel) return `${activeModel.displayLabel} — ${service.status}`;
  }
  if (label === 'Local') {
    return `Local — ${service.status}`;
  }
  return `${label} — ${service.status}`;
}

export function serviceStatusHeadline(service: NotebookToolbarServiceDto): string {
  const active = service.providerOptions.find(p => p.providerId === service.activeProviderId);
  const activeModel = service.localModelOptions.find(m => m.isActive);
  if (active && toolbarProviderLabel(active.providerSection) === 'Local' && activeModel) {
    return `${activeModel.displayLabel} — ${service.status}`;
  }
  return serviceSummaryLine(service);
}

const TOOLBAR_PROVIDER_NAMES: Record<string, string> = {
  AzureOpenAI: 'Microsoft Foundry',
  AzureSpeechService: 'Microsoft Foundry',
  AzureOpenAiImages: 'Microsoft Foundry',
  AzureOpenAiEmbedding: 'Microsoft Foundry',
  AzureDocumentIntelligence: 'Microsoft Foundry',
  GoogleGeminiApi: 'Google Gemini',
  OpenAI: 'OpenAI',
  OpenRouter: 'OpenRouter',
  HuggingFace: 'Hugging Face',
};

export function toolbarProviderLabel(section: string | null | undefined): string {
  if (!section) return 'Unknown';
  if (section.startsWith('LocalServiceHosts:')) return 'Local';
  return TOOLBAR_PROVIDER_NAMES[section] ?? section;
}

export function toolbarProviderOptionLabel(option: NotebookToolbarProviderOptionDto): string {
  const name = toolbarProviderLabel(option.providerSection);
  if (option.modelId) return `${name} ${option.modelId}`;
  return name;
}
