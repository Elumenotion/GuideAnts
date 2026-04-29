import { describe, expect, it } from 'vitest';
import {
  getConnectionSectionDisplayName,
  getProviderFieldHelpText,
  getProviderFieldLabel,
  getRuntimeDependencyChangeHint,
  getRuntimeDependencyDisplayName,
  getServiceDisplayName,
  getServiceProviderDisplayName,
  humanizePresentationKey,
} from './displayLabels';

describe('displayLabels', () => {
  it('returns mapped labels for known settings ids', () => {
    expect(getConnectionSectionDisplayName('AzureOpenAI')).toBe('Microsoft Foundry');
    expect(getServiceDisplayName('SpeechTranscription')).toBe('Speech Transcription');
    expect(getServiceProviderDisplayName('SpeechTranscription.AzureSpeech.Batch')).toBe(
      'Microsoft Foundry Speech Services (Batch)'
    );
    expect(getRuntimeDependencyDisplayName('LocalServiceHosts:EmbeddingsBaseUrl')).toBe('Embeddings Base URL');
  });

  it('returns deterministic humanized labels for unknown ids', () => {
    expect(humanizePresentationKey('UnknownServiceProviderId')).toBe('Unknown Service Provider Id');
    expect(getConnectionSectionDisplayName('NewProviderSection')).toBe('New Provider Section');
    expect(getRuntimeDependencyDisplayName('NewRuntime:OddKey')).toBe('New Runtime Odd Key');
  });

  it('returns provider field labels/help from client registry', () => {
    expect(getProviderFieldLabel('SpeechTranscription.HuggingFace.Inference', 'ModelId')).toBe('ASR Model ID');
    expect(getProviderFieldLabel('Unknown.Provider', 'TimeoutSeconds')).toBe('Timeout Seconds');
    expect(getProviderFieldHelpText('Unknown.Provider', 'TimeoutSeconds')).toContain('timeout');
    expect(getProviderFieldHelpText('Unknown.Provider', 'UnmappedField')).toBe('');
  });

  it('returns runtime dependency change hints from client registry', () => {
    const knownHint = getRuntimeDependencyChangeHint('LlamaCpp:BaseUrl');
    const unknownHint = getRuntimeDependencyChangeHint('Unknown:Key');

    expect(knownHint).toContain('Runtime-owned value');
    expect(unknownHint).toContain('Runtime-owned value');
  });
});
