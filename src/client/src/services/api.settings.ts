export type {
  ImageGenerationBundleDefinitionDto,
  ImageGenerationBundleDefinitionListDto,
  ImageGenerationBundleDefinitionRoleDto,
  ImageGenerationBundleDefinitionSamplingDto,
} from '../types/settings';

/** Canonical bundle-definition.json shape used by API-owned ImageGeneration settings. */
export type ImageGenerationBundleDefinitionRequest = import('../types/settings').ImageGenerationBundleDefinitionDto;

export type ImageGenerationBundleDefinitionResponse = import('../types/settings').ImageGenerationBundleDefinitionDto;

export type ImageGenerationBundleDefinitionImportRequest = {
  definition: ImageGenerationBundleDefinitionRequest;
};
