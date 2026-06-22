import { useState, useEffect, useCallback, useRef } from 'react';
import { createPortal } from 'react-dom';
import type { PublishedGuideDto, PublishGuideDto, UpdatePublishedGuideDto } from '../../types/guides';
import { api } from '../../services/api';
import {
  patchClaudeSkillPackEnv,
  sanitizeClaudeSkillDownloadFileName,
  triggerBlobDownload,
} from '../../utils/claudeSkillPackDownload';
import { GeneralTab } from './configTabs/GeneralTab';
import { InterfaceTab } from './configTabs/InterfaceTab';
import { FeaturesTab } from './configTabs/FeaturesTab';
import { LimitsTab } from './configTabs/LimitsTab';
import { AuthTab } from './configTabs/AuthTab';
import { McpTab } from './configTabs/McpTab';
import {
  ApisTab,
  type WireApiAliasKey,
  type WireApiAliasState,
  type WireApiEndpointFlagKey,
  type WireApiEndpointFlagsState,
  type WireApiMaxRequestSizeKey,
  type WireApiMaxRequestSizesState,
} from './configTabs/ApisTab';

interface PublishGuideDialogProps {
  guideName: string;
  guideId: string;
  publishedGuide?: PublishedGuideDto | null;
  onPublish?: (config: Omit<PublishGuideDto, 'projectId'>) => void;
  onUpdate?: (config: UpdatePublishedGuideDto) => void;
  onDeactivate?: () => void;
  onReactivate?: () => void;
  onCancel: () => void;
  onPublishedGuideUpdated?: (publishedGuide: PublishedGuideDto) => void;
}

type TabId = 'general' | 'interface' | 'features' | 'limits' | 'auth' | 'apis' | 'mcp';

function getInitialWireApiEndpointFlags(
  flags?: Partial<WireApiEndpointFlagsState> | null
): WireApiEndpointFlagsState {
  return {
    models: flags?.models !== false,
    chatCompletions: flags?.chatCompletions !== false,
    responses: flags?.responses !== false,
    embeddings: flags?.embeddings !== false,
    imageGenerations: flags?.imageGenerations !== false,
    audioTranscriptions: flags?.audioTranscriptions !== false,
    audioSpeech: flags?.audioSpeech !== false,
  };
}

function getInitialWireApiAliases(aliasMap?: Record<string, string> | null): WireApiAliasState {
  return {
    guide: aliasMap?.guide || 'guide',
    embeddings: aliasMap?.embeddings || 'embeddings',
    image: aliasMap?.image || 'image',
    transcription: aliasMap?.transcription || 'transcription',
    speech: aliasMap?.speech || 'speech',
  };
}

function trimDefinedRecord<T extends string>(record: Partial<Record<T, string>>): Partial<Record<T, string>> {
  const result: Partial<Record<T, string>> = {};
  for (const key of Object.keys(record) as T[]) {
    const value = record[key];
    const trimmed = typeof value === 'string' ? value.trim() : '';
    if (trimmed) {
      result[key] = trimmed;
    }
  }
  return result;
}

function compactNumberRecord<T extends string>(record: Partial<Record<T, number>>): Partial<Record<T, number>> {
  const result: Partial<Record<T, number>> = {};
  for (const key of Object.keys(record) as T[]) {
    const value = record[key];
    if (typeof value === 'number' && value > 0) {
      result[key] = value;
    }
  }
  return result;
}

export function PublishGuideDialog({ 
  guideName, 
  guideId, 
  publishedGuide,
  onPublish,
  onUpdate,
  onDeactivate,
  onReactivate,
  onCancel,
  onPublishedGuideUpdated,
}: PublishGuideDialogProps) {
  const isEditMode = !!publishedGuide;
  const [activeTab, setActiveTab] = useState<TabId>('general');
  const [showDeactivateConfirm, setShowDeactivateConfirm] = useState(false);
  
  // General
  const [friendlyName, setFriendlyName] = useState(publishedGuide?.friendlyName || '');
  const [friendlyNameValidation, setFriendlyNameValidation] = useState<{
    validating: boolean;
    available?: boolean;
    reason?: string;
  }>({ validating: false });

  // Interface
  const [displayMode, setDisplayMode] = useState<'full' | 'last-turn'>(publishedGuide?.displayMode || 'full');
  const [commandMode, setCommandMode] = useState(publishedGuide?.commandMode || false);
  const [showTurnNavigation, setShowTurnNavigation] = useState(publishedGuide?.showTurnNavigation || false);
  const [collapsible, setCollapsible] = useState(publishedGuide?.collapsible || false);

  // Features
  const [showConversationStarters, setShowConversationStarters] = useState(publishedGuide?.showConversationStarters || false);
  const [showAttachments, setShowAttachments] = useState(publishedGuide?.showAttachments || false);

  // Limits
  const [maxUserMessageLength, setMaxUserMessageLength] = useState<number | undefined>(publishedGuide?.maxUserMessageLength);
  const [maxTurns, setMaxTurns] = useState<number | undefined>(publishedGuide?.maxTurns);
  const [retentionPeriod, setRetentionPeriod] = useState<number | undefined>(publishedGuide?.retentionPeriod);
  const [dailyChargeLimitUsd, setDailyChargeLimitUsd] = useState<number | undefined>(publishedGuide?.dailyChargeLimitUsd);
  const [billingPeriodChargeLimitUsd, setBillingPeriodChargeLimitUsd] = useState<number | undefined>(
    publishedGuide?.billingPeriodChargeLimitUsd
  );

  // Authentication
  const [authWebhookUrl, setAuthWebhookUrl] = useState(publishedGuide?.authValidationWebhookUrl || '');
  const [authWebhookTimeout, setAuthWebhookTimeout] = useState<number>(publishedGuide?.authWebhookTimeoutSeconds || 5);
  const [hasApiKey, setHasApiKey] = useState(publishedGuide?.hasApiKey || false);
  const [wireApiEnabled, setWireApiEnabled] = useState<boolean>(publishedGuide?.wireApiConfig?.enabled ?? false);
  const [wireApiProfile, setWireApiProfile] = useState<string>(publishedGuide?.wireApiConfig?.profile || '');
  const [wireApiEndpointFlags, setWireApiEndpointFlags] = useState<WireApiEndpointFlagsState>(
    getInitialWireApiEndpointFlags(publishedGuide?.wireApiConfig?.endpointFlags ?? null)
  );
  const [wireApiAliases, setWireApiAliases] = useState<WireApiAliasState>(
    getInitialWireApiAliases(publishedGuide?.wireApiConfig?.aliasMap ?? null)
  );
  const [wireApiMaxRequestSizes, setWireApiMaxRequestSizes] = useState<WireApiMaxRequestSizesState>(
    publishedGuide?.wireApiConfig?.maxRequestSizes ?? {}
  );

  // MCP
  const [mcpEnabled, setMcpEnabled] = useState(publishedGuide?.mcpEnabled || false);
  const [mcpDescription, setMcpDescription] = useState(publishedGuide?.mcpDescription || '');
  const [sessionApiKey, setSessionApiKey] = useState<string | null>(null);
  const [mcpPersisted, setMcpPersisted] = useState(
    !!(publishedGuide?.mcpEnabled && publishedGuide?.hasApiKey)
  );

  const isActive = publishedGuide?.active ?? true;
  const prevPublishedIdRef = useRef<string | undefined>(publishedGuide?.id);

  useEffect(() => {
    if (publishedGuide?.id && !prevPublishedIdRef.current) {
      prevPublishedIdRef.current = publishedGuide.id;
      if (mcpDescription.trim()) {
        setActiveTab('mcp');
      }
    }
  }, [publishedGuide?.id, mcpDescription]);

  useEffect(() => {
    setMcpPersisted(!!(publishedGuide?.mcpEnabled && publishedGuide?.hasApiKey));
    if (publishedGuide?.mcpEnabled) {
      setMcpEnabled(true);
    }
    if (publishedGuide?.hasApiKey) {
      setHasApiKey(true);
    }
  }, [publishedGuide?.id, publishedGuide?.mcpEnabled, publishedGuide?.hasApiKey]);

  // Debounced friendly name validation
  const validateFriendlyName = useCallback(async (name: string) => {
    if (!name.trim() || !guideId) {
      setFriendlyNameValidation({ validating: false });
      return;
    }

    // If it's the same as current, don't validate
    if (isEditMode && name === publishedGuide?.friendlyName) {
      setFriendlyNameValidation({ validating: false, available: true });
      return;
    }

    setFriendlyNameValidation({ validating: true });

    try {
      const result = await api.guides.guides.validateFriendlyName(guideId, name);
      setFriendlyNameValidation({
        validating: false,
        available: result.available,
        reason: result.reason,
      });
    } catch (error) {
      setFriendlyNameValidation({
        validating: false,
        available: false,
        reason: 'Failed to validate friendly name',
      });
    }
  }, [guideId, isEditMode, publishedGuide?.friendlyName]);

  // Debounce validation (500ms)
  useEffect(() => {
    if (!friendlyName.trim()) {
      setFriendlyNameValidation({ validating: false });
      return;
    }

    const timer = setTimeout(() => {
      validateFriendlyName(friendlyName);
    }, 500);

    return () => clearTimeout(timer);
  }, [friendlyName, validateFriendlyName]);

  const setWireApiEndpointFlag = (key: WireApiEndpointFlagKey, value: boolean) => {
    setWireApiEndpointFlags((prev) => ({ ...prev, [key]: value }));
  };

  const setWireApiAlias = (key: WireApiAliasKey, value: string) => {
    setWireApiAliases((prev) => ({ ...prev, [key]: value }));
  };

  const setWireApiMaxRequestSize = (key: WireApiMaxRequestSizeKey, value: number | undefined) => {
    setWireApiMaxRequestSizes((prev) => ({
      ...prev,
      [key]: value,
    }));
  };

  const handleSubmit = (e?: React.FormEvent | React.MouseEvent) => {
    if (e) {
      e.preventDefault();
    }
    
    // Don't submit if friendly name validation is in progress or not confirmed available (unless it's the same as current)
    if (friendlyName.trim() && (!isEditMode || friendlyName !== publishedGuide?.friendlyName) && (friendlyNameValidation.validating || friendlyNameValidation.available !== true)) {
      return;
    }

    // Prevent both friendly name and authentication (webhook or API key)
    if (friendlyName.trim() && (authWebhookUrl.trim() || hasApiKey)) {
      alert('A Published Guide cannot have both a Public URL (Friendly Name) and Authentication. Public URLs are for anonymous access only. Please clear one of them.');
      return;
    }

    const isAppIdentityAuth = publishedGuide?.authMode === 'AppIdentity';

    const aliasMap = trimDefinedRecord(wireApiAliases);
    const maxRequestSizes = compactNumberRecord(wireApiMaxRequestSizes);
    const wireApiConfig = {
      enabled: wireApiEnabled,
      profile: wireApiProfile.trim() || undefined,
      endpointFlags: {
        models: wireApiEndpointFlags.models,
        chatCompletions: wireApiEndpointFlags.chatCompletions,
        responses: wireApiEndpointFlags.responses,
        embeddings: wireApiEndpointFlags.embeddings,
        imageGenerations: wireApiEndpointFlags.imageGenerations,
        audioTranscriptions: wireApiEndpointFlags.audioTranscriptions,
        audioSpeech: wireApiEndpointFlags.audioSpeech,
      },
      aliasMap: Object.keys(aliasMap).length > 0 ? aliasMap : undefined,
      maxRequestSizes: Object.keys(maxRequestSizes).length > 0 ? maxRequestSizes : undefined,
    };

    const config = {
      friendlyName: friendlyName.trim() || undefined,
      displayMode,
      commandMode,
      showTurnNavigation,
      collapsible,
      showConversationStarters,
      showAttachments,
      maxUserMessageLength: maxUserMessageLength || undefined,
      maxTurns: maxTurns || undefined,
      retentionPeriod: retentionPeriod ?? undefined,
      dailyChargeLimitUsd: dailyChargeLimitUsd ?? undefined,
      billingPeriodChargeLimitUsd: billingPeriodChargeLimitUsd ?? undefined,
      ...(isAppIdentityAuth
        ? {}
        : {
            authValidationWebhookUrl: authWebhookUrl.trim() || undefined,
            authWebhookTimeoutSeconds: authWebhookUrl.trim() ? authWebhookTimeout : undefined,
          }),
      wireApiConfig,
      mcpEnabled,
      mcpDescription: mcpDescription.trim() || undefined,
    };

    if (isEditMode && onUpdate) {
      onUpdate(config);
    } else if (!isEditMode && onPublish) {
      onPublish(config);
    }
  };

  const handleDeactivateClick = () => {
    setShowDeactivateConfirm(true);
  };

  const handleDeactivateConfirm = () => {
    if (onDeactivate) {
      onDeactivate();
    }
    setShowDeactivateConfirm(false);
  };

  const handleEnableMcpAccess = async () => {
    const pubId = publishedGuide?.id;
    if (!pubId) {
      throw new Error('Publish the guide first');
    }

    let apiKey = sessionApiKey;
    if (!hasApiKey) {
      const response = await api.guides.guides.generateApiKey(guideId, pubId);
      apiKey = response.apiKey;
      setSessionApiKey(apiKey);
      setHasApiKey(true);
    }

    const updated = await api.guides.guides.updatePublished(guideId, pubId, {
      mcpEnabled: true,
      mcpDescription: mcpDescription.trim() || undefined,
    });

    setMcpEnabled(true);
    setMcpPersisted(true);
    onPublishedGuideUpdated?.(updated);
  };

  const handleDownloadClaudeSkill = async () => {
    const pubId = publishedGuide?.id;
    if (!pubId) {
      throw new Error('Guide must be published first');
    }
    if (!mcpPersisted) {
      throw new Error('Enable MCP access before downloading the skill pack');
    }

    const zipBlob = await api.guides.guides.downloadClaudeSkill(guideId, pubId);
    const patchedBlob = await patchClaudeSkillPackEnv(zipBlob, sessionApiKey);
    const baseName = sanitizeClaudeSkillDownloadFileName(friendlyName.trim() || guideName);
    triggerBlobDownload(patchedBlob, `${baseName}-claude-skill.zip`);
  };

  const handleSessionApiKeyChange = (apiKey: string | null) => {
    setSessionApiKey(apiKey);
    if (!apiKey && mcpPersisted) {
      setMcpPersisted(false);
      setMcpEnabled(false);
    }
  };

  const handleApiKeyChange = async (nextHasKey: boolean) => {
    setHasApiKey(nextHasKey);
    const pubId = publishedGuide?.id;
    if (!nextHasKey && pubId && mcpPersisted) {
      const updated = await api.guides.guides.updatePublished(guideId, pubId, {
        mcpEnabled: false,
      });
      setMcpEnabled(false);
      setMcpPersisted(false);
      onPublishedGuideUpdated?.(updated);
    }
  };

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
        if (e.key === 'Escape' && !showDeactivateConfirm) {
            onCancel();
        }
        if (e.key === 'Enter' && !showDeactivateConfirm && !e.shiftKey) {
            const target = e.target as HTMLElement;
            // Allow textarea to handle enter (new line)
            if (target.tagName === 'TEXTAREA') return;
            // Allow buttons to handle enter (click)
            if (target.tagName === 'BUTTON') return;
            
            e.preventDefault();
            handleSubmit();
        }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [onCancel, showDeactivateConfirm, handleSubmit]);

  const tabs: { id: TabId; label: string }[] = [
    { id: 'general', label: 'General' },
    { id: 'interface', label: 'Interface' },
    { id: 'features', label: 'Features' },
    { id: 'limits', label: 'Limits' },
    { id: 'auth', label: 'Auth' },
    { id: 'apis', label: 'APIs' },
    { id: 'mcp', label: 'MCP and Skills' },
  ];

  const dialogMarkup = (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
      <div 
        className="bg-white rounded-lg shadow-xl w-full max-w-3xl lg:max-w-4xl mx-4 max-h-[90vh] overflow-y-auto flex flex-col"
        tabIndex={-1}
      >
        <div className="sticky top-0 bg-white border-b border-gray-200 px-6 py-4 z-10">
          <h2 className="text-xl font-semibold text-gray-900">
            {isEditMode ? `Manage Published Guide: "${guideName}"` : `Publish "${guideName}"`}
          </h2>
          {isEditMode && publishedGuide && (
            <div className="mt-1 flex items-center gap-2">
              <span className={`text-xs px-2 py-0.5 rounded font-medium ${
                publishedGuide.active 
                  ? 'bg-green-100 text-green-700' 
                  : 'bg-gray-100 text-gray-600'
              }`}>
                {publishedGuide.active ? 'Active' : 'Inactive'}
              </span>
              <span className="text-xs text-gray-500">
                Created {new Date(publishedGuide.created).toLocaleDateString()}
              </span>
            </div>
          )}
          {!isEditMode && (
            <p className="text-sm text-gray-600 mt-1">
              Publishing this guide will create a dedicated notebook in this project and generate a unique
              shareable link for external users to interact with the guide.
            </p>
          )}
        </div>

        <div className="flex border-b border-gray-200 px-6 overflow-x-auto">
          {tabs.map((tab) => (
            <button
              key={tab.id}
              onClick={() => setActiveTab(tab.id)}
              className={`py-3 px-4 text-sm font-medium border-b-2 whitespace-nowrap transition-colors ${
                activeTab === tab.id
                  ? 'border-blue-500 text-blue-600'
                  : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
              }`}
            >
              {tab.label}
            </button>
          ))}
        </div>

        <form onSubmit={handleSubmit} className="p-6 space-y-6 flex-1 overflow-y-auto">
          {/* Inactive Warning Banner (edit mode only) */}
          {isEditMode && !isActive && (
            <div className="p-4 bg-amber-50 border border-amber-200 rounded-md mb-4">
              <div className="flex items-start gap-3">
                <svg className="w-5 h-5 text-amber-600 mt-0.5 flex-shrink-0" fill="currentColor" viewBox="0 0 20 20">
                  <path fillRule="evenodd" d="M8.257 3.099c.765-1.36 2.722-1.36 3.486 0l5.58 9.92c.75 1.334-.213 2.98-1.742 2.98H4.42c-1.53 0-2.493-1.646-1.743-2.98l5.58-9.92zM11 13a1 1 0 11-2 0 1 1 0 012 0zm-1-8a1 1 0 00-1 1v3a1 1 0 002 0V6a1 1 0 00-1-1z" clipRule="evenodd" />
                </svg>
                <div className="flex-1">
                  <h4 className="text-sm font-medium text-amber-900">This published guide is inactive</h4>
                  <p className="text-sm text-amber-700 mt-1">
                    The guide is not currently accessible to external users. Click "Reactivate" below to make it available again.
                  </p>
                </div>
              </div>
            </div>
          )}

          {/* Read-only Info (edit mode only) */}
          {isEditMode && publishedGuide && (
            <div className="bg-gray-50 rounded-md p-4 space-y-2 text-sm mb-4">
              <div className="flex justify-between">
                <span className="text-gray-600">Published Guide ID:</span>
                <code className="text-xs text-gray-900 font-mono select-all">{publishedGuide.id}</code>
              </div>
            </div>
          )}

          <fieldset disabled={isEditMode && !isActive} className={isEditMode && !isActive ? 'opacity-50' : ''}>
            {activeTab === 'general' && (
              <GeneralTab
                friendlyName={friendlyName}
                setFriendlyName={setFriendlyName}
                friendlyNameValidation={friendlyNameValidation}
                publishedGuide={publishedGuide || null}
                authWebhookUrl={authWebhookUrl}
                hasApiKey={hasApiKey}
              />
            )}

            {activeTab === 'interface' && (
              <InterfaceTab
                displayMode={displayMode}
                setDisplayMode={setDisplayMode}
                commandMode={commandMode}
                setCommandMode={setCommandMode}
                showTurnNavigation={showTurnNavigation}
                setShowTurnNavigation={setShowTurnNavigation}
                collapsible={collapsible}
                setCollapsible={setCollapsible}
              />
            )}

            {activeTab === 'features' && (
              <FeaturesTab
                showConversationStarters={showConversationStarters}
                setShowConversationStarters={setShowConversationStarters}
                showAttachments={showAttachments}
                setShowAttachments={setShowAttachments}
              />
            )}

            {activeTab === 'limits' && (
              <LimitsTab
                maxUserMessageLength={maxUserMessageLength}
                setMaxUserMessageLength={setMaxUserMessageLength}
                maxTurns={maxTurns}
                setMaxTurns={setMaxTurns}
                retentionPeriod={retentionPeriod}
                setRetentionPeriod={setRetentionPeriod}
                dailyChargeLimitUsd={dailyChargeLimitUsd}
                setDailyChargeLimitUsd={setDailyChargeLimitUsd}
                billingPeriodChargeLimitUsd={billingPeriodChargeLimitUsd}
                setBillingPeriodChargeLimitUsd={setBillingPeriodChargeLimitUsd}
              />
            )}

            {activeTab === 'auth' && (
              <AuthTab
                authWebhookUrl={authWebhookUrl}
                setAuthWebhookUrl={setAuthWebhookUrl}
                authWebhookTimeout={authWebhookTimeout}
                setAuthWebhookTimeout={setAuthWebhookTimeout}
                friendlyName={friendlyName}
                hasApiKey={hasApiKey}
                sessionApiKey={sessionApiKey}
                guideId={guideId}
                publishedGuideId={publishedGuide?.id}
                authMode={publishedGuide?.authMode}
                onApiKeyChange={handleApiKeyChange}
                onSessionApiKeyChange={handleSessionApiKeyChange}
              />
            )}

            {activeTab === 'apis' && (
              <ApisTab
                publishedGuideId={publishedGuide?.id}
                wireApiEnabled={wireApiEnabled}
                setWireApiEnabled={setWireApiEnabled}
                wireApiProfile={wireApiProfile}
                setWireApiProfile={setWireApiProfile}
                endpointFlags={wireApiEndpointFlags}
                setEndpointFlag={setWireApiEndpointFlag}
                aliases={wireApiAliases}
                setAlias={setWireApiAlias}
                maxRequestSizes={wireApiMaxRequestSizes}
                setMaxRequestSize={setWireApiMaxRequestSize}
                hasApiKey={hasApiKey}
                authWebhookUrl={authWebhookUrl}
              />
            )}

            {activeTab === 'mcp' && (
              <McpTab
                mcpEnabled={mcpEnabled}
                setMcpEnabled={setMcpEnabled}
                mcpDescription={mcpDescription}
                setMcpDescription={setMcpDescription}
                hasApiKey={hasApiKey}
                sessionApiKey={sessionApiKey}
                publishedGuideId={publishedGuide?.id}
                mcpPersisted={mcpPersisted}
                onEnableMcpAccess={publishedGuide?.id ? handleEnableMcpAccess : undefined}
                onDownloadClaudeSkill={publishedGuide?.id ? handleDownloadClaudeSkill : undefined}
              />
            )}
          </fieldset>
        </form>

        {/* Footer/Actions */}
        <div className="flex justify-between items-center p-6 border-t border-gray-200 bg-gray-50 rounded-b-lg">
          {isEditMode && (
            <div>
              {isActive ? (
                <button
                  type="button"
                  onClick={handleDeactivateClick}
                  className="px-4 py-2 text-red-700 hover:bg-red-50 rounded-md border border-red-300 text-sm font-medium"
                >
                  Deactivate
                </button>
              ) : (
                <button
                  type="button"
                  onClick={onReactivate}
                  className="px-4 py-2 bg-green-600 text-white rounded-md hover:bg-green-700 text-sm font-medium"
                >
                  Reactivate Guide
                </button>
              )}
            </div>
          )}
          {!isEditMode && <div />}
          
          <div className="flex gap-3">
            <button
              type="button"
              onClick={onCancel}
              className="px-4 py-2 text-gray-700 hover:bg-gray-100 rounded-md border border-gray-300 bg-white text-sm font-medium"
            >
              {isEditMode && !isActive ? 'Close' : 'Cancel'}
            </button>
            {(isEditMode ? isActive : true) && (
              <button
                type="button"
                onClick={handleSubmit}
                disabled={
                  (!!friendlyName.trim() && (!isEditMode || friendlyName !== publishedGuide?.friendlyName) && (friendlyNameValidation.validating || friendlyNameValidation.available !== true)) ||
                  (!!friendlyName.trim() && (!!authWebhookUrl.trim() || hasApiKey))
                }
                className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed text-sm font-medium shadow-sm"
              >
                {isEditMode ? 'Save Changes' : 'Publish Guide'}
              </button>
            )}
          </div>
        </div>

        {/* Deactivate Confirmation Dialog */}
        {showDeactivateConfirm && (
          <div className="absolute inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 rounded-lg">
            <div className="bg-white rounded-lg shadow-xl p-6 max-w-md mx-4">
              <h3 className="text-lg font-semibold text-gray-900 mb-2">
                Deactivate Published Guide?
              </h3>
              <p className="text-sm text-gray-600 mb-4">
                This will make the guide inaccessible to external users. The notebook and conversations
                will be preserved, and you can reactivate the guide later.
              </p>
              <div className="flex justify-end gap-2">
                <button
                  onClick={() => setShowDeactivateConfirm(false)}
                  className="px-4 py-2 text-gray-700 hover:bg-gray-100 rounded-md"
                >
                  Cancel
                </button>
                <button
                  onClick={handleDeactivateConfirm}
                  className="px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700"
                >
                  Deactivate
                </button>
              </div>
            </div>
          </div>
        )}
      </div>
    </div>
  );

  return createPortal(dialogMarkup, document.body);
}
