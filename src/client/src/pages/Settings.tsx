import { useCallback, useEffect, useMemo, useState } from 'react';
import { ConfirmationDialog } from '../components/common/ConfirmationDialog';
import { HomeButton } from '../components/common/HomeButton';
import { SettingsButton } from '../components/common/SettingsButton';
import { TourStartButton } from '../tour/TourStartButton';
import { useToast } from '../components/common/Toast';
import { api } from '../services/api';
import {
  CreateRuntimeProfileRequest,
  EmbeddingsRebuildConflictResponse,
  EmbeddingsRebuildResponse,
  LlamaRuntimeInventoryItemDto,
  ModelDownloadOperationDto,
  SettingsModelDto,
  SettingsRuntimeProfileDto,
  SettingsSectionSummaryDto,
  UpdateRuntimeProfileRequest,
} from '../types/settings';
import { ConnectionsTab } from './settings/components/ConnectionsTab';
import { InfrastructureTab } from './settings/components/InfrastructureTab';
import { ModelsRuntimeWorkspace } from './settings/components/ModelsRuntimeWorkspace';
import { OverviewTab } from './settings/components/OverviewTab';
import { PersonalizationTab } from './settings/components/PersonalizationTab';
import { ServicesTab, type ServiceKey } from './settings/components/ServicesTab';
import { SettingsTabNavigation } from './settings/components/SettingsTabNavigation';
import { TelemetryTab } from './settings/components/TelemetryTab';
import { AddModelWizard } from './settings/components/catalog/AddModelWizard';
import { UsersTab } from './settings/components/UsersTab';
import {
  ActiveAddOperationState,
  ModelsRuntimeDeepLink,
  PendingConfirmation,
  ProfileFormState,
  SettingsTab,
} from './settings/types';
import {
  buildProfileCreateRequest,
  createProfileFormFromContractShape,
  buildProfileUpdateRequest,
  createEmptyProfileForm,
  getErrorMessage,
} from './settings/utils';
import { useLocalModelOnboardingOperation } from '../features/localModelOnboarding/useOperationPolling';
import { useAuth } from '../contexts/AuthContext';
import { HeaderActionsBar } from '../components/common/HeaderActionsBar';
import { GuideAntsGuideButton } from '../features/guideantsGuide/GuideAntsGuideButton';
import { HeaderUserMenu } from '../components/common/HeaderUserMenu';
import { HeaderIconLinkButton } from '../components/common/HeaderIconLinkButton';
import { FiBookOpen } from 'react-icons/fi';

export default function Settings() {
  const { showToast } = useToast();
  const { role } = useAuth();
  const isAdmin = role === 'Admin';
  const [activeTab, setActiveTab] = useState<SettingsTab>(() => (isAdmin ? 'overview' : 'personalization'));
  const [pendingConfirmation, setPendingConfirmation] = useState<PendingConfirmation>(null);
  const [modelsRuntimeDeepLink, setModelsRuntimeDeepLink] = useState<ModelsRuntimeDeepLink | null>(null);
  const [connectionsFocusedSection, setConnectionsFocusedSection] = useState<string | null>(null);
  const [infrastructureFocusedKey, setInfrastructureFocusedKey] = useState<string | null>(null);
  const [servicesFocusedKey, setServicesFocusedKey] = useState<ServiceKey | null>(null);

  const [sectionSummaries, setSectionSummaries] = useState<SettingsSectionSummaryDto[]>([]);
  const [sectionSummariesError, setSectionSummariesError] = useState<string | null>(null);

  const [models, setModels] = useState<SettingsModelDto[]>([]);
  const [modelsLoading, setModelsLoading] = useState(true);
  const [modelsError, setModelsError] = useState<string | null>(null);
  const [deletingModelId, setDeletingModelId] = useState<string | null>(null);
  const [wizardOpen, setWizardOpen] = useState(false);
  const [wizardProviderPreselect, setWizardProviderPreselect] = useState<string | null>(null);
  const [activeAddOperation, setActiveAddOperationState] = useState<ActiveAddOperationState | null>(() => {
    if (typeof window === 'undefined') {
      return null;
    }
    try {
      const raw = window.sessionStorage.getItem('guideants.settings.activeAddOperation');
      return raw ? (JSON.parse(raw) as ActiveAddOperationState) : null;
    } catch {
      return null;
    }
  });
  const [activeAddOperationStatus, setActiveAddOperationStatus] = useState<string>('queued');
  const [catalogVersion, setCatalogVersion] = useState(0);

  const setActiveAddOperation = useCallback((next: ActiveAddOperationState | null) => {
    setActiveAddOperationState(next);
    if (typeof window === 'undefined') {
      return;
    }
    try {
      if (next) {
        window.sessionStorage.setItem('guideants.settings.activeAddOperation', JSON.stringify(next));
      } else {
        window.sessionStorage.removeItem('guideants.settings.activeAddOperation');
      }
    } catch {
      // sessionStorage is best-effort; ignore quota/disabled errors
    }
  }, []);

  const [profiles, setProfiles] = useState<SettingsRuntimeProfileDto[]>([]);
  const [profilesLoading, setProfilesLoading] = useState(true);
  const [profilesError, setProfilesError] = useState<string | null>(null);
  const [profileDialogOpen, setProfileDialogOpen] = useState(false);
  const [profileForm, setProfileForm] = useState<ProfileFormState>(() => createEmptyProfileForm());
  const [editingProfileId, setEditingProfileId] = useState<string | null>(null);
  const [profileSaving, setProfileSaving] = useState(false);
  const [deletingProfileId, setDeletingProfileId] = useState<string | null>(null);

  const [llamaInventory, setLlamaInventory] = useState<LlamaRuntimeInventoryItemDto[]>([]);
  const [llamaInventoryLoading, setLlamaInventoryLoading] = useState(true);
  const [llamaInventoryRefreshing, setLlamaInventoryRefreshing] = useState(false);
  const [llamaInventoryError, setLlamaInventoryError] = useState<string | null>(null);

  useEffect(() => {
    if (!isAdmin && activeTab !== 'personalization') {
      setActiveTab('personalization');
    }
  }, [activeTab, isAdmin]);

  useEffect(() => {
    if (!isAdmin && activeAddOperation) {
      setActiveAddOperation(null);
    }
  }, [activeAddOperation, isAdmin, setActiveAddOperation]);

  const loadSectionSummaries = useCallback(async () => {
    try {
      const summaries = await api.settings.getSections();
      setSectionSummaries(summaries);
    } catch (error) {
      setSectionSummariesError(getErrorMessage(error, 'Failed to load settings section summaries.'));
    }
  }, []);

  const loadModels = useCallback(async () => {
    setModelsLoading(true);
    setModelsError(null);
    try {
      const nextModels = await api.settings.getModels();
      setModels(nextModels);
      setCatalogVersion((previous) => previous + 1);
    } catch (error) {
      setModelsError(getErrorMessage(error, 'Failed to load models.'));
    } finally {
      setModelsLoading(false);
    }
  }, []);

  const loadProfiles = useCallback(async () => {
    setProfilesLoading(true);
    setProfilesError(null);
    try {
      const nextProfiles = await api.settings.getRuntimeProfiles();
      setProfiles(nextProfiles);
    } catch (error) {
      setProfilesError(getErrorMessage(error, 'Failed to load runtime profiles.'));
    } finally {
      setProfilesLoading(false);
    }
  }, []);

  const loadLlamaInventory = useCallback(async (mode: 'initial' | 'refresh' = 'initial') => {
    if (mode === 'refresh') {
      setLlamaInventoryRefreshing(true);
    } else {
      setLlamaInventoryLoading(true);
    }
    setLlamaInventoryError(null);
    try {
      const items = await api.settings.getLlamaInventory();
      setLlamaInventory(items);
    } catch (error) {
      setLlamaInventory([]);
      setLlamaInventoryError(getErrorMessage(error, 'Failed to load llama runtime inventory.'));
    } finally {
      if (mode === 'refresh') {
        setLlamaInventoryRefreshing(false);
      } else {
        setLlamaInventoryLoading(false);
      }
    }
  }, []);

  /** Stable identity required: `AddModelWizard` polls in an effect that depends on this callback. */
  const handleCatalogDataRefresh = useCallback(async () => {
    await loadModels();
    await loadLlamaInventory('refresh');
  }, [loadModels, loadLlamaInventory]);

  const refreshLlamaInventory = useCallback(async () => {
    await loadLlamaInventory('refresh');
  }, [loadLlamaInventory]);

  useEffect(() => {
    if (!isAdmin) {
      setModelsLoading(false);
      setProfilesLoading(false);
      setLlamaInventoryLoading(false);
      return;
    }

    void loadSectionSummaries();
    void loadModels();
    void loadProfiles();
    void loadLlamaInventory();
  }, [isAdmin, loadSectionSummaries, loadModels, loadProfiles, loadLlamaInventory]);

  useLocalModelOnboardingOperation({
    operationId: activeAddOperation?.operationId ?? null,
    enabled: isAdmin && Boolean(activeAddOperation) && !wizardOpen,
    onUpdate: (op: ModelDownloadOperationDto) => {
      setActiveAddOperationStatus(op.status);
    },
    onTerminal: (op: ModelDownloadOperationDto) => {
      if (!activeAddOperation) {
        return;
      }

      if (op.status === 'completed') {
        setActiveAddOperation(null);
        setActiveAddOperationStatus('completed');
        void (async () => {
          await loadModels();
          await loadLlamaInventory('refresh');
          await loadSectionSummaries();
          showToast({ type: 'success', title: `Model ${activeAddOperation.catalogModelId} added` });
        })();
        return;
      }

      setActiveAddOperation(null);
      showToast({
        type: 'error',
        title: 'Add model failed',
        message: op.error?.message ?? op.errorMessage ?? 'Add model operation failed.',
      });
    },
    onPollFailureThreshold: () => {
      setActiveAddOperation(null);
      showToast({
        type: 'error',
        title: 'Add model polling failed',
        message: 'Could not read add-model operation status.',
      });
    },
    intervalMs: 2000,
  });

  const orderedModels = useMemo(() => {
    return [...models].sort((left, right) => {
      const leftOrder = left.displayOrder ?? Number.MAX_SAFE_INTEGER;
      const rightOrder = right.displayOrder ?? Number.MAX_SAFE_INTEGER;
      if (leftOrder !== rightOrder) {
        return leftOrder - rightOrder;
      }
      return left.modelId.localeCompare(right.modelId);
    });
  }, [models]);

  const handleRebuildEmbeddings = useCallback(async () => {
    try {
      const response = (await api.settings.rebuildEmbeddings()) as EmbeddingsRebuildResponse;
      showToast({
        type: 'success',
        title: 'Vector rebuild queued',
        message: `Job ${response.jobId} is now ${response.status}.`,
      });
    } catch (error) {
      const status = (error as { status?: number })?.status;
      if (status === 409) {
        const body = (error as { body?: EmbeddingsRebuildConflictResponse })?.body;
        showToast({
          type: 'warning',
          title: 'Rebuild already running',
          message: body?.jobId ? `Existing job ${body.jobId} is ${body.status}.` : 'A rebuild job is already pending.',
        });
        return;
      }
      showToast({
        type: 'error',
        title: 'Failed to queue rebuild',
        message: getErrorMessage(error, 'Delete and regenerate request failed.'),
      });
    } finally {
    }
  }, [showToast]);

  const handleOpenAddModelWizard = useCallback((providerPreselect?: string) => {
    setWizardProviderPreselect(providerPreselect ?? null);
    setWizardOpen(true);
  }, []);

  const handleDeleteModel = useCallback(
    async (modelId: string) => {
      setDeletingModelId(modelId);
      try {
        await api.settings.deleteModel(modelId);
        await loadModels();
        await loadLlamaInventory('refresh');
        await loadSectionSummaries();

        showToast({ type: 'success', title: `Model ${modelId} deleted` });
      } catch (error) {
        showToast({
          type: 'error',
          title: `Failed to delete ${modelId}`,
          message: getErrorMessage(error, 'Delete request failed.'),
        });
      } finally {
        setDeletingModelId(null);
      }
    },
    [loadLlamaInventory, loadModels, loadSectionSummaries, showToast]
  );

  const handleProfileFormChange = useCallback(<K extends keyof ProfileFormState>(key: K, value: ProfileFormState[K]) => {
    setProfileForm((previous) => ({ ...previous, [key]: value }));
  }, []);

  const handleOpenCreateProfile = useCallback(() => {
    setEditingProfileId(null);
    setProfileForm(createEmptyProfileForm());
    setProfileDialogOpen(true);
  }, []);

  const handleImportProfile = useCallback((form: ProfileFormState) => {
    setEditingProfileId(null);
    setProfileForm(form);
    setProfileDialogOpen(true);
  }, []);

  const createRuntimeProfileFromTemplate = useCallback(
    async (template: 'qwen3_5' | 'qwen3_6' | 'gemma4') => {
      const templates: Record<'qwen3_5' | 'qwen3_6' | 'gemma4', CreateRuntimeProfileRequest> = {
        qwen3_5: {
          profileId: 'qwen3_5',
          displayName: 'Qwen 3.5',
          description: 'Template profile for Qwen 3.5 local runtime.',
          combineSystemAndDeveloperMessages: true,
          thoughtBlockPattern: '',
          samplingParametersJson: '{"temperature":{"kind":"number","defaultValue":0.7}}',
          thinkingControlJson: '{"defaultChoice":"medium","choiceActions":{"minimal":[],"low":[],"medium":[],"high":[]}}',
        },
        qwen3_6: {
          profileId: 'qwen3_6',
          displayName: 'Qwen 3.6',
          description: 'Template profile for Qwen 3.6 local runtime.',
          combineSystemAndDeveloperMessages: true,
          thoughtBlockPattern: '',
          samplingParametersJson: '{"temperature":{"kind":"number","defaultValue":0.7}}',
          thinkingControlJson: '{"defaultChoice":"medium","choiceActions":{"minimal":[],"low":[],"medium":[],"high":[]}}',
        },
        gemma4: {
          profileId: 'gemma4',
          displayName: 'Gemma 4',
          description: 'Template profile for Gemma 4 local runtime.',
          combineSystemAndDeveloperMessages: true,
          thoughtBlockPattern: '',
          samplingParametersJson: '{"temperature":{"kind":"number","defaultValue":0.7}}',
          thinkingControlJson: '{"defaultChoice":"medium","choiceActions":{"minimal":[],"low":[],"medium":[],"high":[]}}',
        },
      };
      try {
        await api.settings.createRuntimeProfile(templates[template]);
        await loadProfiles();
        showToast({ type: 'success', title: `Runtime profile ${templates[template].profileId} created` });
      } catch (error) {
        const message = getErrorMessage(error, 'Template creation failed.');
        if (message.toLowerCase().includes('already exists')) {
          await loadProfiles();
          showToast({
            type: 'success',
            title: `Runtime profile ${templates[template].profileId} ready`,
            message: 'Existing profile reused.',
          });
          return;
        }
        showToast({
          type: 'error',
          title: 'Failed to create runtime profile',
          message,
        });
      }
    },
    [loadProfiles, showToast]
  );

  const createCustomRuntimeProfile = useCallback(
    async (request: CreateRuntimeProfileRequest) => {
      const created = await api.settings.createRuntimeProfile(request);
      await loadProfiles();
      showToast({ type: 'success', title: `Runtime profile ${created.profileId} created` });
      return created;
    },
    [loadProfiles, showToast]
  );

  const resetProfileForm = useCallback(() => {
    setProfileDialogOpen(false);
    setEditingProfileId(null);
    setProfileForm(createEmptyProfileForm());
  }, []);

  const handleEditProfile = useCallback((profile: SettingsRuntimeProfileDto) => {
    setEditingProfileId(profile.profileId);
    setProfileForm(createProfileFormFromContractShape(profile));
    setProfileDialogOpen(true);
  }, []);

  const handleSaveProfile = useCallback(async () => {
    let payload: CreateRuntimeProfileRequest | UpdateRuntimeProfileRequest;
    try {
      payload = editingProfileId ? buildProfileUpdateRequest(profileForm) : buildProfileCreateRequest(profileForm);
    } catch (error) {
      showToast({
        type: 'error',
        title: 'Profile validation failed',
        message: getErrorMessage(error, 'Check required fields and JSON values.'),
      });
      return;
    }

    setProfileSaving(true);
    try {
      if (editingProfileId) {
        await api.settings.updateRuntimeProfile(editingProfileId, payload);
      } else {
        await api.settings.createRuntimeProfile(payload as CreateRuntimeProfileRequest);
      }

      await loadProfiles();

      showToast({
        type: 'success',
        title: editingProfileId ? `Profile ${payload.profileId} updated` : `Profile ${payload.profileId} created`,
      });
      resetProfileForm();
    } catch (error) {
      showToast({
        type: 'error',
        title: editingProfileId ? 'Failed to update profile' : 'Failed to create profile',
        message: getErrorMessage(error, 'Request failed.'),
      });
    } finally {
      setProfileSaving(false);
    }
  }, [editingProfileId, loadProfiles, profileForm, resetProfileForm, showToast]);

  const handleDeleteProfile = useCallback(
    async (profileId: string) => {
      setDeletingProfileId(profileId);
      try {
        await api.settings.deleteRuntimeProfile(profileId);
        await loadProfiles();

        if (editingProfileId === profileId) {
          resetProfileForm();
        }

        showToast({ type: 'success', title: `Profile ${profileId} deleted` });
      } catch (error) {
        showToast({
          type: 'error',
          title: `Failed to delete ${profileId}`,
          message: getErrorMessage(error, 'Delete request failed.'),
        });
      } finally {
        setDeletingProfileId(null);
      }
    },
    [editingProfileId, loadProfiles, resetProfileForm, showToast]
  );

  const handleLoadLlamaModel = useCallback(
    async (routerModelId: string) => {
      await api.settings.loadLlamaModel(routerModelId);
      await loadLlamaInventory('refresh');
      showToast({
        type: 'success',
        title: 'Llama load requested',
        message: routerModelId,
      });
    },
    [loadLlamaInventory, showToast]
  );

  const handleRequestUnloadLlamaRouter = useCallback((routerModelId: string, notebookReferenceCount: number) => {
    setPendingConfirmation({ kind: 'unload-llama-router', routerModelId, notebookReferenceCount });
  }, []);

  const handleRequestDeleteLlamaRouter = useCallback(
    (routerModelId: string, _catalogModelIds: string[], notebookReferenceCount: number) => {
      setPendingConfirmation({
        kind: 'delete-llama-router',
        routerModelId,
        catalogModelIds: _catalogModelIds,
        notebookReferenceCount,
      });
    },
    []
  );

  const handleConfirmPendingAction = useCallback(() => {
    if (!pendingConfirmation) {
      return;
    }

    if (pendingConfirmation.kind === 'rebuild-embeddings') {
      void handleRebuildEmbeddings();
    } else if (pendingConfirmation.kind === 'delete-model') {
      void handleDeleteModel(pendingConfirmation.modelId);
    } else if (pendingConfirmation.kind === 'delete-profile') {
      void handleDeleteProfile(pendingConfirmation.profileId);
    } else if (pendingConfirmation.kind === 'unload-llama-router') {
      const routerId = pendingConfirmation.routerModelId;
      void (async () => {
        try {
          await api.settings.unloadLlamaModel(routerId);
          await loadLlamaInventory('refresh');
          showToast({
            type: 'success',
            title: 'Model unloaded',
            message: routerId,
          });
        } catch (error) {
          showToast({
            type: 'error',
            title: 'Unload failed',
            message: getErrorMessage(error, 'Request failed.'),
          });
        }
      })();
    } else if (pendingConfirmation.kind === 'delete-llama-router') {
      const routerId = pendingConfirmation.routerModelId;
      void (async () => {
        try {
          await api.settings.deleteLlamaRouterEntry(routerId);
          await loadModels();
          await loadLlamaInventory('refresh');
          await loadSectionSummaries();
          showToast({
            type: 'success',
            title: 'Router entry deleted',
            message: routerId,
          });
        } catch (error) {
          showToast({
            type: 'error',
            title: 'Delete failed',
            message: getErrorMessage(error, 'Request failed.'),
          });
        }
      })();
    }

    setPendingConfirmation(null);
  }, [handleDeleteModel, handleDeleteProfile, handleRebuildEmbeddings, loadLlamaInventory, loadModels, loadSectionSummaries, pendingConfirmation, showToast]);

  const pendingConfirmationView = useMemo(() => {
    if (!pendingConfirmation) {
      return null;
    }

    if (pendingConfirmation.kind === 'rebuild-embeddings') {
      return {
        title: 'Delete And Regenerate Vectors',
        message: 'Delete all stored vectors and regenerate from markdown shadows? This cannot be undone and may take time.',
        confirmText: 'Regenerate',
        confirmButtonClass: 'bg-red-600 hover:bg-red-700 text-white',
      };
    }

    if (pendingConfirmation.kind === 'delete-model') {
      return {
        title: 'Delete model',
        message: `Delete model '${pendingConfirmation.modelId}'? This cannot be undone.`,
        confirmText: 'Delete',
        confirmButtonClass: 'bg-red-600 hover:bg-red-700 text-white',
      };
    }

    if (pendingConfirmation.kind === 'delete-profile') {
      return {
        title: 'Delete Runtime Profile',
        message: `Delete runtime profile '${pendingConfirmation.profileId}'? This action cannot be undone.`,
        confirmText: 'Delete',
        confirmButtonClass: 'bg-red-600 hover:bg-red-700 text-white',
      };
    }

    if (pendingConfirmation.kind === 'delete-llama-router') {
      return {
        title: 'Delete alias + files',
        message: `This will stop any load, remove GGUF/mmproj files for '${pendingConfirmation.routerModelId}', and remove ${pendingConfirmation.catalogModelIds.length} linked catalog row(s). This cannot be undone.`,
        confirmText: 'Delete alias + files',
        confirmButtonClass: 'bg-red-600 hover:bg-red-700 text-white',
      };
    }

    const n = pendingConfirmation.notebookReferenceCount;
    return {
      title: 'Unload from llama runtime',
      message:
        n > 0
          ? `Unload router '${pendingConfirmation.routerModelId}'? This catalog model is referenced by ${n} notebook(s).`
          : `Unload router '${pendingConfirmation.routerModelId}' from the local llama server?`,
      confirmText: 'Unload',
      confirmButtonClass: 'bg-amber-600 hover:bg-amber-700 text-white',
    };
  }, [pendingConfirmation]);

  const handleNavigate = useCallback((tab: SettingsTab) => {
    setActiveTab(tab);
  }, []);

  const handleOpenConnections = useCallback((focusedSection?: string) => {
    setConnectionsFocusedSection(focusedSection ?? null);
    setActiveTab('connections');
  }, []);

  const handleOpenServices = useCallback((serviceId: ServiceKey) => {
    setServicesFocusedKey(serviceId);
    setActiveTab('services');
  }, []);

  const handleOpenModelInCatalog = useCallback((modelId: string) => {
    setModelsRuntimeDeepLink({ subTab: 'catalog', focusedModelId: modelId });
    setActiveTab('models-runtime');
  }, []);

  const handleOpenModelsRuntime = useCallback((subTab: 'catalog' | 'profiles' | 'local-llama') => {
    setModelsRuntimeDeepLink({ subTab });
    setActiveTab('models-runtime');
  }, []);

  const handleServicesFocusHandled = useCallback(() => {
    setServicesFocusedKey(null);
  }, []);

  const activeTabContent = useMemo(() => {
    if (!isAdmin && activeTab !== 'personalization') {
      return (
        <>
          <div className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800" role="alert">
            You are not authorized to access this settings section. Showing Personalization instead.
          </div>
          <PersonalizationTab />
        </>
      );
    }

    if (activeTab === 'overview') {
      return (
        <OverviewTab
          onOpenConnections={handleOpenConnections}
          onOpenServices={handleOpenServices}
          onOpenModelsRuntime={handleOpenModelsRuntime}
          catalogVersion={catalogVersion}
        />
      );
    }

    if (activeTab === 'services') {
      return (
        <ServicesTab focusedService={servicesFocusedKey} onFocusedServiceHandled={handleServicesFocusHandled} />
      );
    }

    if (activeTab === 'personalization') {
      return <PersonalizationTab />;
    }

    if (activeTab === 'users') {
      return <UsersTab />;
    }

    if (activeTab === 'telemetry') {
      return <TelemetryTab />;
    }

    if (activeTab === 'connections') {
      return (
        <ConnectionsTab
          focusedSection={connectionsFocusedSection}
          providerSections={sectionSummaries}
          sectionSummariesError={sectionSummariesError}
          onRefreshSectionSummaries={() => void loadSectionSummaries()}
          onOpenServiceConsumer={handleOpenServices}
          onOpenChatTarget={handleOpenModelInCatalog}
        />
      );
    }

    if (activeTab === 'infrastructure') {
      return (
        <InfrastructureTab
          focusedRuntimeKey={infrastructureFocusedKey}
          onFocusedRuntimeKeyHandled={() => setInfrastructureFocusedKey(null)}
        />
      );
    }

    if (activeTab === 'models-runtime') {
      return (
        <ModelsRuntimeWorkspace
          initialSubTab={modelsRuntimeDeepLink?.subTab}
          focusedAlias={modelsRuntimeDeepLink?.focusedAlias}
          focusedModelId={modelsRuntimeDeepLink?.focusedModelId}
          llamaInventory={llamaInventory}
          llamaInventoryLoading={llamaInventoryLoading}
          llamaInventoryRefreshing={llamaInventoryRefreshing}
          llamaInventoryError={llamaInventoryError}
          onRefreshLlamaInventory={refreshLlamaInventory}
          onLoadLlamaModel={handleLoadLlamaModel}
          onRequestUnloadLlamaRouter={handleRequestUnloadLlamaRouter}
          onRequestDeleteLlamaRouter={handleRequestDeleteLlamaRouter}
          profilesLoading={profilesLoading}
          profiles={profiles}
          modelsLoading={modelsLoading}
          modelsError={modelsError}
          orderedModels={orderedModels}
          deletingModelId={deletingModelId}
          onRetryLoadModels={() => void loadModels()}
          onRequestDeleteModel={(modelId) => setPendingConfirmation({ kind: 'delete-model', modelId })}
          onCatalogEdited={handleCatalogDataRefresh}
          onOpenAddModelWizard={handleOpenAddModelWizard}
          activeAddOperation={activeAddOperation}
          profileDialogOpen={profileDialogOpen}
          editingProfileId={editingProfileId}
          profileForm={profileForm}
          profileSaving={profileSaving}
          profilesError={profilesError}
          deletingProfileId={deletingProfileId}
          onProfileFormChange={handleProfileFormChange}
          onOpenCreateProfile={handleOpenCreateProfile}
          onImportProfile={handleImportProfile}
          onResetProfileForm={resetProfileForm}
          onSaveProfile={() => void handleSaveProfile()}
          onRetryLoadProfiles={() => void loadProfiles()}
          onEditProfile={handleEditProfile}
          onRequestDeleteProfile={(profileId) => setPendingConfirmation({ kind: 'delete-profile', profileId })}
          onInsertRuntimeProfileTemplate={createRuntimeProfileFromTemplate}
        />
      );
    }

    return null;
  }, [
    activeTab,
    activeAddOperation,
    catalogVersion,
    connectionsFocusedSection,
    deletingModelId,
    deletingProfileId,
    editingProfileId,
    handleEditProfile,
    handleLoadLlamaModel,
    handleNavigate,
    handleOpenAddModelWizard,
    handleOpenConnections,
    handleOpenModelInCatalog,
    handleOpenCreateProfile,
    handleOpenServices,
    handleServicesFocusHandled,
    handleImportProfile,
    handleProfileFormChange,
    handleRequestUnloadLlamaRouter,
    handleRequestDeleteLlamaRouter,
    handleSaveProfile,
    infrastructureFocusedKey,
    isAdmin,
    servicesFocusedKey,
    llamaInventory,
    llamaInventoryError,
    llamaInventoryLoading,
    llamaInventoryRefreshing,
    handleCatalogDataRefresh,
    loadModels,
    loadProfiles,
    loadSectionSummaries,
    modelsError,
    modelsLoading,
    modelsRuntimeDeepLink,
    orderedModels,
    profileDialogOpen,
    profileForm,
    profileSaving,
    profiles,
    profilesError,
    profilesLoading,
    refreshLlamaInventory,
    createRuntimeProfileFromTemplate,
    resetProfileForm,
    sectionSummaries,
    sectionSummariesError,
  ]);

  return (
    <div className="flex h-screen flex-col bg-gray-50">
      <header className="border-b border-gray-200 bg-white px-8 py-4">
        <div className="mx-auto flex max-w-7xl flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex min-w-0 items-center gap-4">
            <img src="./guide.png" alt="GuideAnts" className="h-10 w-10" />
            <div className="min-w-0">
              <h1 className="text-xl font-semibold text-gray-900">Settings</h1>
              <p className="text-sm text-gray-600">
                Manage service-specific provider settings, provider connections, telemetry, runtime infrastructure, and models &amp; runtime.
              </p>
            </div>
          </div>
          <HeaderActionsBar className="self-end sm:self-auto">
            <GuideAntsGuideButton />
            {isAdmin ? (
              <HeaderIconLinkButton
                to="/settings/system-guides"
                title="System Guides"
                icon={<FiBookOpen className="h-4 w-4" />}
                tourId="settings.actions.system-guides"
              />
            ) : null}
            <HomeButton />
            <SettingsButton />
            <HeaderUserMenu />
            <TourStartButton screenId="settings" inline />
          </HeaderActionsBar>
        </div>
      </header>

      <SettingsTabNavigation activeTab={activeTab} role={role} onTabChange={handleNavigate} />

      {isAdmin && activeAddOperation && (
        <div
          className="border-b border-blue-200 bg-blue-50 px-8 py-2 text-sm text-blue-900"
          role="status"
          aria-live="polite"
        >
          <div className="mx-auto flex max-w-7xl flex-wrap items-center gap-3">
            <span className="inline-flex h-2 w-2 animate-pulse rounded-full bg-blue-500" aria-hidden />
            <span>
              Installing <span className="font-mono">{activeAddOperation.catalogModelId}</span> &middot; step{' '}
              <span className="font-medium">{activeAddOperationStatus}</span>
            </span>
            <button
              type="button"
              className="ml-auto rounded border border-blue-300 bg-white px-2 py-0.5 text-xs font-medium text-blue-800 hover:bg-blue-100"
              onClick={() => {
                setWizardProviderPreselect('llama-cpp');
                setWizardOpen(true);
                setModelsRuntimeDeepLink({ subTab: 'catalog' });
                setActiveTab('models-runtime');
              }}
              title="Open progress view"
            >
              Open progress
            </button>
          </div>
        </div>
      )}

      <main className="flex-1 overflow-auto px-8 py-6">
        <div className="mx-auto max-w-7xl space-y-6">{activeTabContent}</div>
      </main>

      {isAdmin ? (
        <AddModelWizard
          isOpen={wizardOpen}
          providerPreselect={wizardProviderPreselect}
          profiles={profiles}
          profilesLoading={profilesLoading}
          inventory={llamaInventory}
          inventoryError={llamaInventoryError}
          onClose={() => setWizardOpen(false)}
          onCreateRuntimeProfileTemplate={createRuntimeProfileFromTemplate}
          onCreateCustomRuntimeProfile={createCustomRuntimeProfile}
          onCatalogChanged={handleCatalogDataRefresh}
          onSetActiveAddOperation={setActiveAddOperation}
        />
      ) : null}

      {pendingConfirmationView && (
        <ConfirmationDialog
          isOpen={Boolean(pendingConfirmation)}
          onClose={() => setPendingConfirmation(null)}
          onConfirm={handleConfirmPendingAction}
          title={pendingConfirmationView.title}
          message={pendingConfirmationView.message}
          confirmText={pendingConfirmationView.confirmText}
          confirmButtonClass={pendingConfirmationView.confirmButtonClass}
        />
      )}
    </div>
  );
}
