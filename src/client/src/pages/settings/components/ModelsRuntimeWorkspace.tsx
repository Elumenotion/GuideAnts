import { useEffect, useState } from 'react';
import { LlamaRuntimeInventoryItemDto, SettingsModelDto } from '../../../types/settings';
import { ActiveModelOperationState, ModelsRuntimeSubTab, OpenAddModelWizardHandler } from '../types';
import { LocalLlamaRuntimeTab } from './LocalLlamaRuntimeTab';
import { ModelsTab } from './ModelsTab';

interface ModelsRuntimeWorkspaceProps {
  initialSubTab?: ModelsRuntimeSubTab;
  focusedAlias?: string;
  focusedModelId?: string;
  llamaInventory: LlamaRuntimeInventoryItemDto[];
  llamaInventoryLoading: boolean;
  llamaInventoryRefreshing: boolean;
  llamaInventoryError: string | null;
  onRefreshLlamaInventory: () => void;
  onLoadLlamaModel: (routerModelId: string) => Promise<void>;
  onRequestUnloadLlamaRouter: (routerModelId: string, notebookReferenceCount: number) => void;
  onRequestDeleteLlamaRouter: (
    routerModelId: string,
    catalogModelIds: string[],
    notebookReferenceCount: number
  ) => void;
  modelsLoading: boolean;
  modelsError: string | null;
  orderedModels: SettingsModelDto[];
  deletingModelId: string | null;
  onRetryLoadModels: () => void;
  onRequestDeleteModel: (modelId: string) => void;
  onCatalogEdited: () => Promise<void>;
  onOpenAddModelWizard: OpenAddModelWizardHandler;
  activeModelOperation: ActiveModelOperationState | null;
  onModelOperationStarted: (operation: ActiveModelOperationState) => void;
}

const subTabs: Array<{ key: ModelsRuntimeSubTab; label: string }> = [
  { key: 'catalog', label: 'Catalog' },
  { key: 'local-llama', label: 'Loaded models' },
];

export function ModelsRuntimeWorkspace(props: ModelsRuntimeWorkspaceProps) {
  const [subTab, setSubTab] = useState<ModelsRuntimeSubTab>(props.initialSubTab ?? 'catalog');

  useEffect(() => {
    if (props.initialSubTab) {
      setSubTab(props.initialSubTab);
      return;
    }
    if (props.focusedAlias) {
      setSubTab('local-llama');
    } else if (props.focusedModelId) {
      setSubTab('catalog');
    }
  }, [props.initialSubTab, props.focusedAlias, props.focusedModelId]);

  return (
    <div className="space-y-6">
      <div className="border-b border-gray-200">
        <nav className="flex flex-wrap gap-4" aria-label="Models and runtime sections">
          {subTabs.map((tab) => (
            <button
              key={tab.key}
              type="button"
              onClick={() => setSubTab(tab.key)}
              className={`border-b-2 px-1 py-2 text-sm font-medium ${
                subTab === tab.key
                  ? 'border-blue-500 text-blue-600'
                  : 'border-transparent text-gray-500 hover:border-gray-300 hover:text-gray-700'
              }`}
            >
              {tab.label}
            </button>
          ))}
        </nav>
      </div>

      {subTab === 'catalog' && (
        <ModelsTab
          modelsLoading={props.modelsLoading}
          modelsError={props.modelsError}
          orderedModels={props.orderedModels}
          deletingModelId={props.deletingModelId}
          llamaInventory={props.llamaInventory}
          llamaInventoryLoading={props.llamaInventoryLoading}
          focusedModelId={props.focusedModelId}
          onRetryLoadModels={props.onRetryLoadModels}
          onRequestDeleteModel={props.onRequestDeleteModel}
          onCatalogEdited={props.onCatalogEdited}
          onOpenAddModel={props.onOpenAddModelWizard}
          activeModelOperation={props.activeModelOperation}
          onModelOperationStarted={props.onModelOperationStarted}
        />
      )}

      {subTab === 'local-llama' && (
        <LocalLlamaRuntimeTab
          inventory={props.llamaInventory}
          inventoryLoading={props.llamaInventoryLoading}
          inventoryRefreshing={props.llamaInventoryRefreshing}
          inventoryError={props.llamaInventoryError}
          onRefresh={props.onRefreshLlamaInventory}
          onLoad={props.onLoadLlamaModel}
          onRequestUnload={props.onRequestUnloadLlamaRouter}
          onRequestDelete={props.onRequestDeleteLlamaRouter}
          onOpenAddModelWizard={props.onOpenAddModelWizard}
          focusedAlias={props.focusedAlias}
        />
      )}
    </div>
  );
}
