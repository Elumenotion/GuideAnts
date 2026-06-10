import { createElement } from 'react';
import { describe, expect, it } from 'vitest';
import { FiMessageSquare } from 'react-icons/fi';
import type { NotebookToolbarProviderOptionDto, NotebookToolbarServiceDto } from '../../../../types/notebookToolbar';
import {
  serviceSummaryLine,
  statusDotClass,
  statusToneClass,
  toolbarProviderLabel,
  toolbarProviderOptionLabel,
  toolbarRefreshButtonClass,
  toolbarServiceButtonClass,
  toolbarServiceIconGlyphClass,
  toolbarServiceIconHeaderClass,
  toolbarServiceStatusDotBorderClass,
  withToolbarServiceIcon,
  WORKSPACE_CONTROLS_COPY,
} from '../toolbarFormatters';

describe('toolbarFormatters', () => {
  it('exports workspace controls copy', () => {
    expect(WORKSPACE_CONTROLS_COPY).toContain('Workspace controls');
  });

  it('returns glyph and button classes for each service color key', () => {
    expect(toolbarServiceIconGlyphClass('chat')).toContain('text-green-600');
    expect(toolbarServiceIconGlyphClass('image')).toContain('text-[#5A4528]');
    expect(toolbarServiceButtonClass('chat', { expanded: true, minSize: 'sm' })).toContain('ring-2');
    expect(toolbarServiceButtonClass('tts', { expanded: false, minSize: 'md' })).toContain('min-w-[2.25rem]');
    expect(toolbarServiceIconHeaderClass('asr')).toContain('inline-flex');
    expect(toolbarServiceStatusDotBorderClass('chat')).toBe('border-white');
    expect(toolbarRefreshButtonClass(true)).toContain('min-w-[2.5rem]');
    expect(toolbarRefreshButtonClass(false)).toContain('min-w-[2.25rem]');
  });

  it('applies glyph classes to valid icon elements', () => {
    const icon = withToolbarServiceIcon('chat', createElement(FiMessageSquare));
    expect((icon.props as { className?: string }).className).toContain('text-green-600');
  });

  it('returns non-element values unchanged from withToolbarServiceIcon', () => {
    expect(withToolbarServiceIcon('chat', 'plain' as never)).toBe('plain');
  });

  it('maps status strings to tone and dot classes', () => {
    expect(statusToneClass('ready')).toContain('emerald');
    expect(statusToneClass('blocked')).toContain('red');
    expect(statusToneClass('off')).toContain('slate');
    expect(statusToneClass('requiresload')).toContain('slate');
    expect(statusToneClass('in progress')).toContain('blue');
    expect(statusToneClass('unknown')).toContain('amber');

    expect(statusDotClass('ready')).toContain('emerald');
    expect(statusDotClass('blocked')).toContain('red');
    expect(statusDotClass('off')).toContain('slate');
    expect(statusDotClass('requiresload')).toContain('slate');
    expect(statusDotClass('inprogress')).toContain('blue');
    expect(statusDotClass('other')).toContain('amber');
  });

  it('formats provider labels and option labels', () => {
    expect(toolbarProviderLabel(null)).toBe('Unknown');
    expect(toolbarProviderLabel('LocalServiceHosts:sd')).toBe('Local');
    expect(toolbarProviderLabel('OpenAI')).toBe('OpenAI');
    expect(toolbarProviderLabel('AzureOpenAI')).toBe('Microsoft Foundry');
    expect(toolbarProviderLabel('CustomSection')).toBe('CustomSection');

    const option: NotebookToolbarProviderOptionDto = {
      providerId: 'openai-chat',
      providerSection: 'OpenAI',
      modelId: 'gpt-4o',
    };
    expect(toolbarProviderOptionLabel(option)).toBe('OpenAI gpt-4o');
    expect(toolbarProviderOptionLabel({ ...option, modelId: undefined })).toBe('OpenAI');
  });

  it('builds service summary lines from active provider and local models', () => {
    const baseService: NotebookToolbarServiceDto = {
      serviceId: 'Chat',
      summary: 'Chat fallback',
      status: 'Ready',
      activeProviderId: 'missing',
      providerOptions: [],
      localModelOptions: [],
    };
    expect(serviceSummaryLine(baseService)).toBe('Chat fallback');

    const withProvider: NotebookToolbarServiceDto = {
      ...baseService,
      activeProviderId: 'openai',
      providerOptions: [
        { providerId: 'openai', providerSection: 'OpenAI', modelId: 'gpt-4o' },
      ],
    };
    expect(serviceSummaryLine(withProvider)).toBe('OpenAI gpt-4o — Ready');

    const withLocal: NotebookToolbarServiceDto = {
      ...withProvider,
      providerOptions: [{ providerId: 'local', providerSection: 'LocalServiceHosts:sd' }],
      activeProviderId: 'local',
      localModelOptions: [
        { routerModelId: 'qwen', displayLabel: 'Qwen Local', isActive: true },
      ],
    };
    expect(serviceSummaryLine(withLocal)).toBe('Qwen Local — Ready');

    const inactiveLocal: NotebookToolbarServiceDto = {
      ...withLocal,
      localModelOptions: [
        { routerModelId: 'qwen', displayLabel: 'Qwen Local', isActive: false },
        { routerModelId: 'llama', displayLabel: 'Llama Local', isActive: true },
      ],
    };
    expect(serviceSummaryLine(inactiveLocal)).toBe('Llama Local — Ready');
  });
});
