import { describe, expect, it } from 'vitest';
import {
  allowedSizesForAzureProfile,
  azureProfileDisplayLabel,
  inferAzureImageProfile,
  localSdAllowedSizes,
} from './imageGenerationUi';

describe('imageGenerationUi', () => {
  it('infers gpt-image profile from deployment name variants', () => {
    expect(inferAzureImageProfile('my-gpt-image-1.5')).toBe('gpt-image-1.5');
    expect(inferAzureImageProfile('gpt_image_model')).toBe('gpt-image-1.5');
    expect(inferAzureImageProfile('GPTImage')).toBe('gpt-image-1.5');
  });

  it('defaults to flux-family for other deployment names', () => {
    expect(inferAzureImageProfile('flux-pro')).toBe('flux-family');
    expect(inferAzureImageProfile('')).toBe('flux-family');
  });

  it('returns display labels for profiles', () => {
    expect(azureProfileDisplayLabel('gpt-image-1.5')).toBe('gpt-image family');
    expect(azureProfileDisplayLabel('flux-family')).toBe('Flux-style');
  });

  it('returns allowed sizes per azure profile', () => {
    expect(allowedSizesForAzureProfile('gpt-image-1.5')).toContain('auto');
    expect(allowedSizesForAzureProfile('flux-family')).toContain('1792x1024');
    expect(allowedSizesForAzureProfile('flux-family')).not.toContain('auto');
  });

  it('exports local SD allowed sizes', () => {
    expect(localSdAllowedSizes).toEqual(['1024x1024', '1024x1792', '1792x1024']);
  });
});
