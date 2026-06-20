import { useState, useEffect, useRef, useCallback } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useToast } from '../../common/Toast';
import { api } from '../../../services/api';
import { GuideDetailsDto, AssistantDetailsDto, CreateGuideDto, UpdateGuideDto, CreateAssistantDto, UpdateAssistantDto, ContextOptionDto, CustomToolDto, FileUploadDto, FileDto, AuthProviderDto, ModelDto, EnvironmentVariableDto } from '../../../types/guides';
import LoadingSpinner from '../../LoadingSpinner';
import { API_BASE_URL, getApiOrigin } from '../../../config/apiConfig';
import { ConfirmationDialog } from '../../common/ConfirmationDialog';
import type { LexicalEditorRef } from '../../notebook/conversations/LexicalEditor';
import { EditorHeader } from './EditorHeader';
import { EditorTabs } from './EditorTabs';
import { GeneralTab } from './GeneralTab';
import { ConfigurationTab } from './ConfigurationTab';
import { ToolsTab } from './ToolsTab';
import { FilesTab } from './FilesTab';
import { CrewTab } from './CrewTab';
import { AuthConfig } from './AuthConfig';
import { EnvironmentConfig } from './EnvironmentConfig';
import { MarkdownPreviewModal } from './MarkdownPreviewModal';
import { useRegisterTour } from '../../../tour/useRegisterTour';
import {
  normalizeReasoningEffortForModel,
  normalizeSamplingValueForModel,
} from '../../chat-model/reasoning';

// Helper function to convert byte array to base64 without stack overflow
// Using spread operator on large arrays causes "Maximum call stack size exceeded"
function bytesToBase64(bytes: number[]): string {
  const uint8Array = new Uint8Array(bytes);
  let binary = '';
  const chunkSize = 0x8000; // Process 32KB at a time to avoid stack overflow
  for (let i = 0; i < uint8Array.length; i += chunkSize) {
    const chunk = uint8Array.subarray(i, i + chunkSize);
    binary += String.fromCharCode.apply(null, chunk as unknown as number[]);
  }
  return btoa(binary);
}

interface FormData {
  name: string;
  description: string;
  instructions: string;
  homePageMarkdown: string; // Only used for guides
  modelId?: string;
  temperature?: number | null;
  topP?: number | null;
  reasoningEffort?: string;
  samplingOverrides: Record<string, number>;
  avatarData?: { bytes: number[]; contentType: string } | null;
  currentAvatarUrl?: string;
  selectedToolIds: string[];
  customTools: CustomToolDto[];
  contextOptions: ContextOptionDto[];
  authProviders: AuthProviderDto[]; // Only used for guides
  environmentVariables: EnvironmentVariableDto[];
  existingFiles: FileDto[]; // Files already on the server
  newFiles: FileUploadDto[]; // New files to be uploaded
  conversationStarters: string[];
  crewMemberIds: string[]; // Only used for guides
}

const defaultFormData: FormData = {
  name: '',
  description: '',
  instructions: '',
  homePageMarkdown: '',
  temperature: null,
  topP: null,
  reasoningEffort: undefined,
  samplingOverrides: {},
  selectedToolIds: [],
  customTools: [],
  contextOptions: [],
  authProviders: [],
  environmentVariables: [],
  existingFiles: [],
  newFiles: [],
  conversationStarters: [],
  crewMemberIds: [],
};

function normalizeFormValue<K extends keyof FormData>(key: K, value: FormData[K]): FormData[K] {
  if (key === 'reasoningEffort' && value === '') {
    return undefined as FormData[K];
  }
  return value;
}

function areFormValuesEqual<T>(left: T, right: T): boolean {
  if (Object.is(left, right)) {
    return true;
  }

  if (
    left !== null &&
    right !== null &&
    typeof left === 'object' &&
    typeof right === 'object'
  ) {
    return JSON.stringify(left) === JSON.stringify(right);
  }

  return false;
}

interface BaseEntityEditorProps {
  entityType: 'assistant' | 'guide';
  entityId?: string;
  projectId: string;
}

export default function BaseEntityEditor({ entityType, entityId, projectId }: BaseEntityEditorProps) {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const { showToast } = useToast();
  const isEditing = !!entityId;
  const isGuide = entityType === 'guide';

  // Persist active tab in URL for navigation stability
  const activeTab = (searchParams.get('tab') || 'general') as 'general' | 'configuration' | 'tools' | 'files' | 'environment' | 'crew' | 'auth';

  const [formData, setFormData] = useState<FormData>(defaultFormData);
  const [isDirty, setIsDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const [loading, setLoading] = useState(isEditing);
  const [catalogModels, setCatalogModels] = useState<ModelDto[]>([]);
  const [avatarBlobUrl, setAvatarBlobUrl] = useState<string | null>(null);
  const [showUnsavedDialog, setShowUnsavedDialog] = useState(false);
  const [hasValidationErrors, setHasValidationErrors] = useState(false);
  const [showMarkdownPreview, setShowMarkdownPreview] = useState(false);
  const [previewFileId, setPreviewFileId] = useState<string | null>(null);
  const instructionsEditorRef = useRef<LexicalEditorRef>(null);
  const homePageEditorRef = useRef<LexicalEditorRef>(null);
  const instructionsListenerRegistered = useRef(false);
  const homePageListenerRegistered = useRef(false);
  const pollingIntervalRef = useRef<NodeJS.Timeout | null>(null);
  
  // Register tours per active area
  useRegisterTour('guideBuilder.general', [
    {
      target: '[data-tour-id="guide.header.title"]',
      content: 'This is the Guide Builder. Use tabs to configure everything.',
      placement: 'bottom'
    },
    {
      target: '[data-tour-id="guide.tabs.general"]',
      content: 'General tab: name, description, avatar, instructions, homepage, starters, context.',
      placement: 'bottom',
      onHighlight: () => {
        const tab = document.querySelector('[data-tour-id="guide.tabs.general"]') as HTMLElement | null;
        tab?.click();
      }
    },
    {
      target: '[data-tour-id="guide.general.subtab.basic"]',
      content: 'Basic Info: set name, description, and avatar.',
      placement: 'bottom',
      onHighlight: () => {
        const basic = document.querySelector('[data-tour-id="guide.general.subtab.basic"]') as HTMLElement | null;
        basic?.click();
      }
    },
    {
      target: '[data-tour-id="guide.general.name.input"]',
      content: 'Enter a unique, descriptive name for the guide.',
      placement: 'right'
    },
    {
      target: '[data-tour-id="guide.general.description.editor"]',
      content: 'Describe purpose and key capabilities (1–2 sentences).',
      placement: 'right'
    },
    {
      target: '[data-tour-id="guide.general.subtab.instructions"]',
      content: 'Open the Instructions editor to define behavior.',
      placement: 'bottom',
      onHighlight: () => {
        const el = document.querySelector('[data-tour-id="guide.general.subtab.instructions"]') as HTMLElement | null;
        el?.click();
      }
    },
    {
      target: '[data-tour-id="guide.general.container"]',
      content: 'Write clear, structured instructions. Include delegation guidance if using a crew.',
      placement: 'right',
      spotlightPadding: 10,
      popoverOffset: 12,
      onHighlight: () => {
        const toolbar = document.querySelector('[data-tour-id="guide.content.instructions.toolbar"]') as HTMLElement | null;
        if (toolbar) toolbar.style.zIndex = '10001';
      },
      onDeselect: () => {
        const toolbar = document.querySelector('[data-tour-id="guide.content.instructions.toolbar"]') as HTMLElement | null;
        if (toolbar) toolbar.style.zIndex = '';
      }
    },
    {
      target: '[data-tour-id="guide.content.instructions.toolbar"]',
      content: 'Use the toolbar for headings, lists, quotes, code, media, and more.',
      placement: 'bottom',
      onHighlight: () => {
        const toolbar = document.querySelector('[data-tour-id="guide.content.instructions.toolbar"]') as HTMLElement | null;
        if (toolbar) toolbar.style.zIndex = '10001';
      },
      onDeselect: () => {
        const toolbar = document.querySelector('[data-tour-id="guide.content.instructions.toolbar"]') as HTMLElement | null;
        if (toolbar) toolbar.style.zIndex = '';
      }
    },
    {
      target: '[data-tour-id="guide.content.instructions.toolbar.sourceToggle"]',
      content: 'Toggle Markdown source mode to edit raw instructions.',
      placement: 'bottom',
      onHighlight: () => {
        const toggle = document.querySelector('[data-tour-id="guide.content.instructions.toolbar.sourceToggle"]') as HTMLElement | null;
        toggle?.click();
      }
    },
    {
      target: '[data-tour-id="guide.content.instructions.source"]',
      content: 'Source mode: edit plain markdown. Toggle again to return to WYSIWYG.',
      placement: 'right'
    },
    {
      target: '[data-tour-id="guide.content.instructions.toolbar.sourceToggle"]',
      content: 'Return to WYSIWYG mode.',
      placement: 'bottom',
      onHighlight: () => {
        const toggle = document.querySelector('[data-tour-id="guide.content.instructions.toolbar.sourceToggle"]') as HTMLElement | null;
        toggle?.click();
      }
    },
    {
      target: '[data-tour-id="guide.general.subtab.homepage"]',
      content: 'Open the Home Page editor to craft the guide\'s landing page.',
      placement: 'bottom',
      onHighlight: () => {
        const el = document.querySelector('[data-tour-id="guide.general.subtab.homepage"]') as HTMLElement | null;
        el?.click();
      }
    },
    {
      target: '[data-tour-id="guide.general.container"]',
      content: 'Use the Home Page to introduce capabilities and provide tips.',
      placement: 'right',
      spotlightPadding: 10,
      popoverOffset: 12
    },
    {
      target: '[data-tour-id="guide.general.container"]',
      content: 'Add concise, actionable Conversation Starters (3–5).',
      placement: 'right',
      spotlightPadding: 10,
      popoverOffset: 12,
      onHighlight: () => {
        const startersTab = document.querySelector('[data-tour-id="guide.general.subtab.starters"]') as HTMLElement | null;
        startersTab?.click();
      }
    },
    {
      target: '[data-tour-id="guide.general.container"]',
      content: 'Context Options: add key/value pairs; blanks enable “Set Context Options.”',
      placement: 'right',
      spotlightPadding: 10,
      popoverOffset: 12,
      onHighlight: () => {
        const tab = document.querySelector('[data-tour-id="guide.general.subtab.context"]') as HTMLElement | null;
        tab?.click();
      }
    }
  ]);

  useRegisterTour('guideBuilder.configuration', [
    {
      target: '[data-tour-id="guide.tabs.configuration"]',
      content: 'Configuration: select model and tune parameters.',
      placement: 'bottom'
    },
    {
      target: '[data-tour-id="guide.config.model.select"]',
      content: 'Pick the AI model. Choose based on cost/quality and task.',
      placement: 'right'
    },
    {
      target: '[data-tour-id="guide.config.temperature.input"]',
      content: 'Temperature controls creativity (lower = focused, higher = creative).',
      placement: 'bottom'
    },
    {
      target: '[data-tour-id="guide.config.topP.input"]',
      content: 'Top P controls diversity. Usually keep near 1.0.',
      placement: 'bottom'
    },
    {
      target: '[data-tour-id="guide.config.reasoningEffort.select"]',
      content: 'Set the model-supported reasoning choice.',
      placement: 'right'
    }
  ]);

  useRegisterTour('guideBuilder.tools', [
    {
      target: '[data-tour-id="guide.tabs.tools"]',
      content: 'Tools: enable globals or add custom tool sources.',
      placement: 'bottom'
    },
    {
      target: '[data-tour-id="guide.tools.subtab.global"]',
      content: 'Global tools: enable only what this guide needs.',
      placement: 'bottom',
      onHighlight: () => {
        const el = document.querySelector('[data-tour-id=\'guide.tools.subtab.global\']') as HTMLElement | null;
        el?.click();
      }
    },
    {
      target: '[data-tour-id="guide.tools.global.section"]',
      content: 'Some tools are auto-required when context options are blank.',
      placement: 'right'
    },
    {
      target: '[data-tour-id="guide.tools.subtab.connectors"]',
      content: 'Tool Sources: configure web APIs, client actions, sandbox modules, and operations.',
      placement: 'bottom',
      onHighlight: () => {
        const el = document.querySelector('[data-tour-id=\'guide.tools.subtab.connectors\']') as HTMLElement | null;
        el?.click();
      }
    },
    {
      target: '[data-tour-id="guide.tools.openapi.addSchema"]',
      content: 'Add an OpenAPI schema to create a connector.',
      placement: 'left'
    },
    {
      target: '[data-tour-id="guide.tools.openapi.addOperation"]',
      content: 'Add operations. New ones save to schema until you save the guide.',
      placement: 'left'
    },
    {
      target: '[data-tour-id="guide.tools.openapi.operationEditor.header"]',
      content: 'Edit or create an operation. Validate JSON, preview tool, then save.',
      placement: 'bottom',
      onHighlight: () => {
        const header = document.querySelector('[data-tour-id="guide.tools.openapi.operationEditor.header"]');
        if (!header) {
          const addBtn = document.querySelector('[data-tour-id="guide.tools.openapi.addOperation"]') as HTMLElement | null;
          addBtn?.click();
        }
      },
      onDeselect: () => {
        const close = document.querySelector('[data-tour-id="guide.tools.openapi.operationEditor.close"]') as HTMLElement | null;
        close?.click();
      }
    }
  ]);

  useRegisterTour('guideBuilder.files', [
    {
      target: '[data-tour-id="guide.tabs.files"]',
      content: 'Files: attach knowledge and notebook assets.',
      placement: 'bottom'
    },
    {
      target: '[data-tour-id="guide.files.vectorStores.upload"]',
      content: 'Upload reference files (indexed for semantic search).',
      placement: 'right'
    },
    {
      target: '[data-tour-id="guide.files.codeInterpreter.upload"]',
      content: 'Upload notebook files used during execution.',
      placement: 'right'
    }
  ]);

  useRegisterTour('guideBuilder.crew', [
    {
      target: '[data-tour-id="guide.tabs.crew"]',
      content: 'Crew: add assistants and order them for delegation.',
      placement: 'bottom'
    },
    {
      target: '[data-tour-id="guide.crew.available.list"]',
      content: 'Pick assistants to add. Use search to filter.',
      placement: 'right'
    },
    {
      target: '[data-tour-id="guide.crew.add"]',
      content: 'Add selected assistant to the crew.',
      placement: 'left'
    },
    {
      target: '[data-tour-id="guide.crew.selected.list"]',
      content: 'Selected members. Drag or use arrows to reorder.',
      placement: 'left'
    },
    {
      target: '[data-tour-id="guide.crew.reorder.up"]',
      content: 'Adjust order to match workflow sequence.',
      placement: 'right'
    },
    {
      target: '[data-tour-id="guide.crew.remove"]',
      content: 'Remove a member if not needed.',
      placement: 'left'
    }
  ]);

  const setActiveTab = useCallback((tab: 'general' | 'configuration' | 'tools' | 'files' | 'environment' | 'crew' | 'auth') => {
    setSearchParams((prev) => {
      const newParams = new URLSearchParams(prev);
      newParams.set('tab', tab);
      return newParams;
    });
  }, [setSearchParams]);

  // Load entity if editing
  useEffect(() => {
    if (entityId) {
      loadEntity(entityId);
    }
  }, [entityId]);

  useEffect(() => {
    const loadModelCatalog = async () => {
      try {
        const models = await api.guides.catalogs.models();
        setCatalogModels(models);
      } catch (error) {
        console.error('Failed to load model catalog:', error);
      }
    };

    loadModelCatalog();
  }, []);

  // Poll for file markdown extraction status updates
  // Only polls when on the Files tab - checks for processing files inside the interval
  useEffect(() => {
    if (!entityId || !isEditing || activeTab !== 'files') {
      if (pollingIntervalRef.current) {
        clearInterval(pollingIntervalRef.current);
        pollingIntervalRef.current = null;
      }
      return;
    }

    const intervalId = setInterval(async () => {
      try {
        const data = isGuide
          ? await api.guides.guides.get(entityId, projectId)
          : await api.guides.assistants.get(entityId, projectId);

        // Check if any files are being processed
        const hasProcessingFiles = (data.files || []).some(
          (file: any) =>
            file.folderKind === 'VectorStore' &&
            file.markdownShadow &&
            (file.markdownShadow.status === 'Pending' || file.markdownShadow.status === 'Processing')
        );

        // Only update if there are processing files AND files actually changed
        if (hasProcessingFiles) {
          setFormData((prev) => {
            const oldFiles = JSON.stringify(prev.existingFiles);
            const newFiles = JSON.stringify(data.files || []);
            if (oldFiles !== newFiles) {
              return { ...prev, existingFiles: data.files || [] };
            }
            return prev;
          });
        }
      } catch (error) {
        console.error('Failed to poll file status:', error);
      }
    }, 5000); // 5 seconds to reduce API spam

    pollingIntervalRef.current = intervalId;

    // Cleanup on unmount or when tab/entity changes
    return () => {
      if (intervalId) {
        clearInterval(intervalId);
      }
    };
  }, [entityId, isEditing, isGuide, activeTab]);

  // Unsaved changes warning
  useEffect(() => {
    const handler = (e: BeforeUnloadEvent) => {
      if (isDirty) {
        e.preventDefault();
        e.returnValue = '';
      }
    };
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, [isDirty]);

  const loadEntity = async (id: string) => {
    try {
      if (isGuide) {
        const data: GuideDetailsDto = await api.guides.guides.get(id, projectId);
        
        setFormData({
          name: data.guide.name,
          description: data.guide.description,
          instructions: data.instructions || '',
          homePageMarkdown: data.homePageMarkdown || '',
          modelId: data.guide.modelId,
          temperature: data.temperature ?? null,
          topP: data.topP ?? null,
          reasoningEffort: data.reasoningEffort,
          currentAvatarUrl: data.guide.avatarUrl,
          selectedToolIds: data.tools.filter((t) => !t.isCustom).map((t) => t.toolId),
          customTools: data.customTools,
          contextOptions: data.contextOptions,
          authProviders: data.authProviders || [],
          environmentVariables: data.environmentVariables || [],
          existingFiles: data.files || [],
          newFiles: [],
          conversationStarters: data.conversationStarters.map((cs) => cs.prompt),
          crewMemberIds: data.crews.flatMap((crew) => crew.members.map((m) => m.assistantId)),
          samplingOverrides: {},
        });

        if (data.guide.avatarUrl) {
          loadAvatar(data.guide.avatarUrl);
        }
      } else {
        const data: AssistantDetailsDto = await api.guides.assistants.get(id, projectId);
        
        setFormData({
          name: data.assistant.name,
          description: data.assistant.description,
          instructions: data.instructions || '',
          homePageMarkdown: '',
          modelId: data.assistant.modelId,
          temperature: data.temperature ?? null,
          topP: data.topP ?? null,
          reasoningEffort: data.reasoningEffort,
          samplingOverrides: {},
          currentAvatarUrl: data.assistant.avatarUrl,
          selectedToolIds: data.tools.filter((t) => !t.isCustom).map((t) => t.toolId),
          customTools: data.customTools,
          contextOptions: data.contextOptions,
          authProviders: [],
          environmentVariables: data.environmentVariables || [],
          existingFiles: data.files || [],
          newFiles: [],
          conversationStarters: data.conversationStarters.map((cs) => cs.prompt),
          crewMemberIds: [],
        });

        if (data.assistant.avatarUrl) {
          loadAvatar(data.assistant.avatarUrl);
        }
      }
    } catch (error: any) {
      showToast({ type: 'error', title: `Failed to load ${entityType}`, message: error.message });
      navigate(`/projects/${projectId}/guides`);
    } finally {
      setLoading(false);
    }
  };

  const loadAvatar = async (avatarUrl: string) => {
    try {
      if (avatarUrl.startsWith('http://') || avatarUrl.startsWith('https://')) {
        setAvatarBlobUrl(avatarUrl);
        return;
      }

      const apiOrigin = getApiOrigin();
      const fullUrl = `${apiOrigin}${avatarUrl}`;
      const result = await api.utils.getAuthenticatedUrl(fullUrl);
      setAvatarBlobUrl(result.objectUrl);
    } catch (error) {
      console.warn(`Failed to load ${entityType} avatar:`, error);
    }
  };

  // Cleanup blob URL on unmount
  useEffect(() => {
    return () => {
      if (avatarBlobUrl && avatarBlobUrl.startsWith('blob:')) {
        URL.revokeObjectURL(avatarBlobUrl);
      }
    };
  }, [avatarBlobUrl]);

  const handleSave = async () => {

    // Validation
    if (!formData.name.trim()) {
      showToast({ type: 'error', title: 'Validation Error', message: `${isGuide ? 'Guide' : 'Assistant'} name is required` });
      return;
    }
    if (!formData.description.trim()) {
      showToast({ type: 'error', title: 'Validation Error', message: `${isGuide ? 'Guide' : 'Assistant'} description is required` });
      return;
    }
    if (hasValidationErrors) {
      showToast({ type: 'error', title: 'Validation Error', message: 'Please fix all OpenAPI schema validation errors before saving' });
      return;
    }

    // Filter out completely empty context options
    const contextOptionsToSave = formData.contextOptions.filter(opt => {
      const hasKey = opt.key.trim() !== '';
      const hasValue = (opt.value ?? '').trim() !== '';
      return hasKey || hasValue;
    });
    const hasInvalidContextOptions = contextOptionsToSave.some(opt => opt.key.trim() === '');
    if (hasInvalidContextOptions) {
      showToast({ type: 'error', title: 'Validation Error', message: 'All context options must have a key' });
      return;
    }

    // Get latest markdown from Lexical editors
    const instructions = instructionsEditorRef.current?.getValue() || formData.instructions;
    const homePageMarkdown = isGuide ? (homePageEditorRef.current?.getValue() || formData.homePageMarkdown) : undefined;
    const selectedModel = catalogModels.find((model) => model.modelId === formData.modelId);
    const reasoningEffortToSave = normalizeReasoningEffortForModel(selectedModel, formData.reasoningEffort);
    const temperatureToSave = normalizeSamplingValueForModel(selectedModel, 'temperature', formData.temperature);
    const topPToSave = normalizeSamplingValueForModel(selectedModel, 'top_p', formData.topP);

    // Validate runtime compatibility before saving
    try {
      const members = [
        {
          role: isGuide ? 'guide' : 'assistant',
          entityId: entityId,
          displayName: formData.name,
          modelId: formData.modelId || undefined
        }
      ];

      if (isGuide && formData.crewMemberIds.length > 0) {
        // We don't have full assistant details here, so we just send the IDs
        // The backend will fetch the models for these assistants
        members.push(...formData.crewMemberIds.map(id => ({
          role: 'assistant',
          entityId: id,
          displayName: 'Crew Member',
          modelId: undefined // Let backend resolve
        })));
      }

      const validation = await api.guides.guides.validateRuntime({ members });
      if (!validation.isValid) {
        showToast({ 
          type: 'error', 
          title: 'Runtime Compatibility Error', 
          message: validation.conflicts.join(' ') 
        });
        return;
      }
    } catch (error: any) {
      console.error('Failed to validate runtime compatibility:', error);
      // Continue with save if validation call fails, backend will re-validate anyway
    }

    // Determine what to send for files using new incremental API:
    // - fileIdsToKeep: IDs of existing files to keep
    // - filesToAdd: New files to upload with contentBytes
    // - Files not in fileIdsToKeep will be deleted
    const fileIdsToKeep = formData.existingFiles.map(f => f.id);
    const filesToAdd = formData.newFiles.length > 0 ? formData.newFiles : undefined;

    const samplingParametersJson =
      Object.keys(formData.samplingOverrides).length > 0
        ? JSON.stringify(formData.samplingOverrides)
        : undefined;

    setSaving(true);
    try {
      if (isEditing && entityId) {
        // Update existing entity
        if (isGuide) {
          const updateDto: UpdateGuideDto = {
            projectId,
            name: formData.name,
            description: formData.description,
            instructions,
            homePageMarkdown,
            modelId: formData.modelId || undefined,
            temperature: temperatureToSave,
            topP: topPToSave,
            reasoningEffort: reasoningEffortToSave,
            samplingParametersJson,
            avatarImageBytes:
              formData.avatarData?.bytes && Array.isArray(formData.avatarData.bytes) && formData.avatarData.bytes.length > 0
                ? bytesToBase64(formData.avatarData.bytes)
                : formData.avatarData === null
                ? ''
                : undefined,
            avatarContentType: formData.avatarData?.contentType,
            toolIds: formData.selectedToolIds,
            customTools: formData.customTools,
            contextOptions: contextOptionsToSave,
            authProviders: formData.authProviders,
            environmentVariables: formData.environmentVariables,
            fileIdsToKeep,
            filesToAdd,
            conversationStarters: formData.conversationStarters,
            crewMemberIds: formData.crewMemberIds,
          };

          await api.guides.guides.update(entityId, updateDto);
          showToast({ type: 'success', title: 'Guide updated successfully' });
        } else {
          const updateDto: UpdateAssistantDto = {
            projectId,
            name: formData.name,
            description: formData.description,
            instructions,
            modelId: formData.modelId || undefined,
            temperature: temperatureToSave,
            topP: topPToSave,
            reasoningEffort: reasoningEffortToSave,
            samplingParametersJson,
            avatarImageBytes:
              formData.avatarData?.bytes && Array.isArray(formData.avatarData.bytes) && formData.avatarData.bytes.length > 0
                ? bytesToBase64(formData.avatarData.bytes)
                : formData.avatarData === null
                ? ''
                : undefined,
            avatarContentType: formData.avatarData?.contentType,
            toolIds: formData.selectedToolIds,
            customTools: formData.customTools,
            contextOptions: contextOptionsToSave,
            fileIdsToKeep,
            filesToAdd,
            conversationStarters: formData.conversationStarters,
          };

          await api.guides.assistants.update(entityId, updateDto);
          showToast({ type: 'success', title: 'Assistant updated successfully' });
        }
        
        // Reload the entity to get updated data and cache-busted avatar
        await loadEntity(entityId);
      } else {
        // Create new entity
        if (isGuide) {
          const createDto: CreateGuideDto = {
            projectId,
            name: formData.name,
            description: formData.description,
            instructions,
            homePageMarkdown,
            modelId: formData.modelId || undefined,
            temperature: temperatureToSave,
            topP: topPToSave,
            reasoningEffort: reasoningEffortToSave,
            samplingParametersJson,
            avatarImageBytes:
              formData.avatarData?.bytes && Array.isArray(formData.avatarData.bytes) && formData.avatarData.bytes.length > 0
                ? bytesToBase64(formData.avatarData.bytes)
                : formData.avatarData === null
                ? ''
                : undefined,
            avatarContentType: formData.avatarData?.contentType,
            toolIds: formData.selectedToolIds,
            customTools: formData.customTools,
            contextOptions: contextOptionsToSave,
            authProviders: formData.authProviders,
            environmentVariables: formData.environmentVariables,
            files: formData.newFiles.length > 0 ? formData.newFiles : undefined,
            conversationStarters: formData.conversationStarters,
            crewMemberIds: formData.crewMemberIds,
          };

          const newGuide = await api.guides.guides.create(createDto);
          showToast({ type: 'success', title: 'Guide created successfully' });
          navigate(`/projects/${projectId}/guides/guide/${newGuide.id}`);
          return;
        } else {
          const createDto: CreateAssistantDto = {
            projectId,
            name: formData.name,
            description: formData.description,
            instructions,
            modelId: formData.modelId || undefined,
            temperature: temperatureToSave,
            topP: topPToSave,
            reasoningEffort: reasoningEffortToSave,
            samplingParametersJson,
            avatarImageBytes:
              formData.avatarData?.bytes && Array.isArray(formData.avatarData.bytes) && formData.avatarData.bytes.length > 0
                ? bytesToBase64(formData.avatarData.bytes)
                : formData.avatarData === null
                ? ''
                : undefined,
            avatarContentType: formData.avatarData?.contentType,
            toolIds: formData.selectedToolIds,
            customTools: formData.customTools,
            contextOptions: contextOptionsToSave,
            files: formData.newFiles.length > 0 ? formData.newFiles : undefined,
            conversationStarters: formData.conversationStarters,
          };

          const newAssistant = await api.guides.assistants.create(createDto);
          showToast({ type: 'success', title: 'Assistant created successfully' });
          navigate(`/projects/${projectId}/guides/assistant/${newAssistant.id}`);
          return;
        }
      }

      setIsDirty(false);
    } catch (error: any) {
      showToast({ type: 'error', title: `Failed to save ${entityType}`, message: error.message });
    } finally {
      setSaving(false);
    }
  };

  const handleCancel = () => {
    if (isDirty) {
      setShowUnsavedDialog(true);
    } else {
      // Navigate to the appropriate tab based on entity type
      const tab = isGuide ? 'guides' : 'assistants';
      navigate(`/projects/${projectId}/guides?tab=${tab}`);
    }
  };

  const handleConfirmLeave = () => {
    setShowUnsavedDialog(false);
    setIsDirty(false);
    // Navigate to the appropriate tab based on entity type
    const tab = isGuide ? 'guides' : 'assistants';
    navigate(`/projects/${projectId}/guides?tab=${tab}`);
  };

  const handleExport = async () => {
    if (!isGuide || !entityId) return;

    try {
      const blob = await api.guides.guides.export(entityId);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `guide-${formData.name.replace(/\s+/g, '-')}-${Date.now()}.zip`;
      a.click();
      URL.revokeObjectURL(url);
      showToast({ type: 'success', title: 'Guide exported successfully' });
    } catch (error: any) {
      showToast({ type: 'error', title: 'Failed to export guide', message: error.message });
    }
  };

  const updateForm = useCallback((updates: Partial<FormData>) => {
    const updateKeys = Object.keys(updates) as (keyof FormData)[];
    let hasChanges = false;

    for (const key of updateKeys) {
      const currentValue = formData[key];
      const nextValue = normalizeFormValue(
        key,
        updates[key] as FormData[typeof key]
      ) as FormData[typeof key];
      if (!areFormValuesEqual(currentValue, nextValue)) {
        hasChanges = true;
        break;
      }
    }

    if (!hasChanges) {
      return;
    }

    setFormData((prev) => {
      const next = { ...prev };
      for (const key of updateKeys) {
        (next as any)[key] = normalizeFormValue(
          key,
          updates[key] as FormData[typeof key]
        );
      }
      return next;
    });
    setIsDirty(true);
  }, [formData]);

  const handlePreviewMarkdown = useCallback((fileId: string) => {
    setPreviewFileId(fileId);
    setShowMarkdownPreview(true);
  }, []);

  const handleDownloadFile = useCallback(async (fileId: string, fileName: string) => {
    if (!entityId) return;
    
    try {
      const url = `${API_BASE_URL}/assistants/${entityId}/files/${fileId}/download`;
      const response = await fetch(url);

      if (!response.ok) {
        throw new Error('Failed to download file');
      }

      const blob = await response.blob();
      const blobUrl = window.URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = blobUrl;
      link.download = fileName;
      document.body.appendChild(link);
      link.click();
      document.body.removeChild(link);
      window.URL.revokeObjectURL(blobUrl);
      
      showToast({ type: 'success', title: 'File downloaded successfully' });
    } catch (error) {
      console.error('Failed to download file:', error);
      showToast({ type: 'error', title: 'Failed to download file' });
    }
  }, [entityId, showToast]);

  useEffect(() => {
    if (!formData.modelId || catalogModels.length === 0) {
      return;
    }

    const model = catalogModels.find((catalogModel) => catalogModel.modelId === formData.modelId);
    const normalizedReasoningEffort = normalizeReasoningEffortForModel(model, formData.reasoningEffort);

    if (normalizedReasoningEffort !== formData.reasoningEffort) {
      setFormData((prev) => ({ ...prev, reasoningEffort: normalizedReasoningEffort }));
    }
  }, [catalogModels, formData.modelId, formData.reasoningEffort]);

  const handleModelChange = useCallback((newModelId?: string, selectedModel?: ModelDto) => {
    const normalizedModelId = newModelId;
    const model = selectedModel
      ?? catalogModels.find((catalogModel) => catalogModel.modelId === normalizedModelId);
    const normalizedReasoningEffort = normalizeReasoningEffortForModel(model, formData.reasoningEffort);
    updateForm({
      modelId: normalizedModelId,
      reasoningEffort: normalizedReasoningEffort
    });
  }, [catalogModels, formData.reasoningEffort, updateForm]);

  if (loading) {
    return (
      <div className="h-screen flex items-center justify-center bg-gray-50">
        <LoadingSpinner />
      </div>
    );
  }

  return (
    <div className="h-screen flex flex-col bg-gray-50">
      <EditorHeader
        isEditing={isEditing}
        saving={saving}
        showExport={isGuide}
        entityType={entityType}
        entityName={formData.name}
        hasValidationErrors={hasValidationErrors}
        onCancel={handleCancel}
        onSave={handleSave}
        onExport={handleExport}
        tourScreenId={`guideBuilder.${activeTab}`}
      />

      <EditorTabs
        activeTab={activeTab}
        showCrew={isGuide}
        showAuth={isGuide}
        onTabChange={setActiveTab}
      />

      <div className="flex-1 overflow-auto bg-gray-50 px-8">
        <div className="max-w-7xl mx-auto py-6 space-y-6">
          {activeTab === 'general' && (
            <GeneralTab
              name={formData.name}
              description={formData.description}
              instructions={formData.instructions}
              homePageMarkdown={formData.homePageMarkdown}
              contextOptions={formData.contextOptions}
              conversationStarters={formData.conversationStarters}
              avatarData={formData.avatarData}
              currentAvatarUrl={avatarBlobUrl || undefined}
              showHomePage={isGuide}
              onNameChange={(name) => updateForm({ name })}
              onDescriptionChange={(description) => updateForm({ description })}
              onInstructionsChange={(instructions) => updateForm({ instructions })}
              onHomePageMarkdownChange={(homePageMarkdown) => updateForm({ homePageMarkdown })}
              onContextOptionsChange={(contextOptions) => updateForm({ contextOptions })}
              onConversationStartersChange={(conversationStarters) => updateForm({ conversationStarters })}
              onAvatarChange={(avatarData) => {
                updateForm({ avatarData });
                if (avatarBlobUrl && avatarBlobUrl.startsWith('blob:')) {
                  URL.revokeObjectURL(avatarBlobUrl);
                }
                setAvatarBlobUrl(null);
              }}
              instructionsEditorRef={instructionsEditorRef}
              homePageEditorRef={homePageEditorRef}
              instructionsListenerRegistered={instructionsListenerRegistered}
              homePageListenerRegistered={homePageListenerRegistered}
            />
          )}

          {activeTab === 'configuration' && (
            <ConfigurationTab
              modelId={formData.modelId}
              temperature={formData.temperature}
              topP={formData.topP}
              reasoningEffort={formData.reasoningEffort}
              samplingOverrides={formData.samplingOverrides}
              onModelIdChange={handleModelChange}
              onTemperatureChange={(temperature) => updateForm({ temperature })}
              onTopPChange={(topP) => updateForm({ topP })}
              onReasoningEffortChange={(reasoningEffort) => updateForm({ reasoningEffort })}
              onSamplingOverridesChange={(samplingOverrides) => updateForm({ samplingOverrides })}
            />
          )}

          {activeTab === 'tools' && (
            <ToolsTab
              selectedToolIds={formData.selectedToolIds}
              customTools={formData.customTools}
              contextOptions={formData.contextOptions}
              environmentVariables={formData.environmentVariables}
              onSelectedToolIdsChange={(selectedToolIds) => updateForm({ selectedToolIds })}
              onCustomToolsChange={(customTools) => updateForm({ customTools })}
              onEnvironmentVariablesChange={(environmentVariables) => updateForm({ environmentVariables })}
              onValidationChange={setHasValidationErrors}
              onDirtyChange={() => setIsDirty(true)}
            />
          )}

          {activeTab === 'files' && (
            <FilesTab
              existingFiles={formData.existingFiles}
              newFiles={formData.newFiles}
              onExistingFilesChange={(existingFiles) => updateForm({ existingFiles })}
              onNewFilesChange={(newFiles) => updateForm({ newFiles })}
              onPreviewMarkdown={handlePreviewMarkdown}
              onDownloadFile={handleDownloadFile}
            />
          )}

          {activeTab === 'environment' && (
            <EnvironmentConfig
              entityLabel={isGuide ? 'guide' : 'assistant'}
              variables={formData.environmentVariables}
              onChange={(environmentVariables) => updateForm({ environmentVariables })}
            />
          )}

          {activeTab === 'auth' && isGuide && (
            <div className="bg-white rounded-lg border border-gray-200 p-6">
              <AuthConfig
                providers={formData.authProviders}
                onChange={(authProviders) => updateForm({ authProviders })}
              />
            </div>
          )}

          {activeTab === 'crew' && isGuide && (
            <CrewTab
              selectedAssistantIds={formData.crewMemberIds}
              onChange={(crewMemberIds) => updateForm({ crewMemberIds })}
            />
          )}
        </div>
      </div>

      {/* Unsaved Changes Dialog */}
      <ConfirmationDialog
        isOpen={showUnsavedDialog}
        onClose={() => setShowUnsavedDialog(false)}
        onConfirm={handleConfirmLeave}
        title="Unsaved Changes"
        message="You have unsaved changes that will be lost if you leave. Are you sure you want to continue?"
        confirmText="Leave"
        cancelText="Stay"
        confirmButtonClass="bg-amber-600 hover:bg-amber-700 text-white"
      />

      {/* Markdown Preview Modal */}
      {previewFileId && (
        <MarkdownPreviewModal
          isOpen={showMarkdownPreview}
          onClose={() => {
            setShowMarkdownPreview(false);
            setPreviewFileId(null);
          }}
          fileId={previewFileId}
          assistantId={!isGuide ? entityId : undefined}
          guideId={isGuide ? entityId : undefined}
        />
      )}
    </div>
  );
}
