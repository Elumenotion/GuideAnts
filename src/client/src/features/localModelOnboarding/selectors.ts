import type { LlamaRuntimeInventoryItemDto } from '../../types/settings';

export function selectAttachableAliases(inventory: LlamaRuntimeInventoryItemDto[]): LlamaRuntimeInventoryItemDto[] {
  return inventory.filter((row) => row.catalogModelIds.length === 0 && row.hasModelFile);
}
