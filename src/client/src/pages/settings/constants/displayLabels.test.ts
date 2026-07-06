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
    expect(getServiceProviderDisplayName('SpeechSynthesis.LocalTts.Http')).toBe('Local TTS HTTP');
    expect(getRuntimeDependencyDisplayName('LocalServiceHosts:EmbeddingsBaseUrl')).toBe('Embeddings Base URL');
  });

  it('returns mapped labels for OpenAI service provider ids', () => {
    expect(getServiceProviderDisplayName('SpeechTranscription.OpenAI.Audio')).toBe('OpenAI Whisper');
    expect(getServiceProviderDisplayName('SpeechSynthesis.OpenAI.Tts')).toBe('OpenAI TTS');
    expect(getServiceProviderDisplayName('ImageGeneration.OpenAI.Images')).toBe('OpenAI Images');
    expect(getServiceProviderDisplayName('Embeddings.OpenAI.Embedding')).toBe('OpenAI Embeddings');
    expect(getConnectionSectionDisplayName('OpenAI')).toBe('OpenAI');
  });

  it('returns OpenAI-specific provider field labels', () => {
    expect(getProviderFieldLabel('SpeechTranscription.OpenAI.Audio', 'ModelId')).toBe('Transcription Model ID');
    expect(getProviderFieldLabel('SpeechSynthesis.OpenAI.Tts', 'ModelId')).toBe('TTS Model ID');
    expect(getProviderFieldLabel('ImageGeneration.OpenAI.Images', 'ModelId')).toBe('Image Model ID');
    expect(getProviderFieldLabel('Embeddings.OpenAI.Embedding', 'ModelId')).toBe('Embedding Model ID');
  });

  it('returns deterministic humanized labels for unknown ids', () => {
    expect(humanizePresentationKey('UnknownServiceProviderId')).toBe('Unknown Service Provider Id');
    expect(getConnectionSectionDisplayName('NewProviderSection')).toBe('New Provider Section');
    expect(getRuntimeDependencyDisplayName('NewRuntime:OddKey')).toBe('New Runtime Odd Key');
  });

  it('returns provider field labels/help from client registry', () => {
    expect(getProviderFieldLabel('SpeechTranscription.HuggingFace.Inference', 'ModelId')).toBe('ASR Model ID');
    expect(getProviderFieldLabel('SpeechSynthesis.LocalTts.Http', 'VoiceName')).toBe('Reference voice');
    expect(getProviderFieldLabel('Unknown.Provider', 'TimeoutSeconds')).toBe('Timeout Seconds');
    expect(getProviderFieldHelpText('Unknown.Provider', 'TimeoutSeconds')).toContain('timeout');
    expect(getProviderFieldHelpText('SpeechSynthesis.LocalTts.Http', 'VoiceName')).toContain('voice pack');
    expect(getProviderFieldHelpText('Unknown.Provider', 'UnmappedField')).toBe('');
  });

  it('returns runtime dependency change hints from client registry', () => {
    const knownHint = getRuntimeDependencyChangeHint('LlamaCpp:BaseUrl');
    const unknownHint = getRuntimeDependencyChangeHint('Unknown:Key');

    expect(knownHint).toContain('Runtime-owned value');
    expect(unknownHint).toContain('Runtime-owned value');
  });
});
