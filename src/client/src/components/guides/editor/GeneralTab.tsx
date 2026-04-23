import React, { useCallback, useEffect, useRef } from 'react';
import { useSearchParams } from 'react-router-dom';
import { BasicInfo } from './BasicInfo';
import LexicalEditor from '../../notebook/conversations/LexicalEditor';
import type { LexicalEditorRef } from '../../notebook/conversations/LexicalEditor';
import { ContextOptions } from './ContextOptions';
import { ConversationStarters } from './ConversationStarters';
import { ContextOptionDto } from '../../../types/guides';

interface GeneralTabProps {
  name: string;
  description: string;
  instructions: string;
  homePageMarkdown: string;
  contextOptions: ContextOptionDto[];
  conversationStarters: string[];
  avatarData?: { bytes: number[]; contentType: string } | null;
  currentAvatarUrl?: string;
  showHomePage: boolean; // Control whether to show Home Page sub-tab
  onNameChange: (name: string) => void;
  onDescriptionChange: (description: string) => void;
  onInstructionsChange: (instructions: string) => void;
  onHomePageMarkdownChange: (markdown: string) => void;
  onContextOptionsChange: (options: ContextOptionDto[]) => void;
  onConversationStartersChange: (starters: string[]) => void;
  onAvatarChange: (data: { bytes: number[]; contentType: string } | null) => void;
  instructionsEditorRef: React.RefObject<LexicalEditorRef | null>;
  homePageEditorRef: React.RefObject<LexicalEditorRef | null>;
  instructionsListenerRegistered: React.MutableRefObject<boolean>;
  homePageListenerRegistered: React.MutableRefObject<boolean>;
}

const GeneralTabComponent = ({
  name,
  description,
  instructions,
  homePageMarkdown,
  contextOptions,
  conversationStarters,
  avatarData,
  currentAvatarUrl,
  showHomePage,
  onNameChange,
  onDescriptionChange,
  onInstructionsChange,
  onHomePageMarkdownChange,
  onContextOptionsChange,
  onConversationStartersChange,
  onAvatarChange,
  instructionsEditorRef,
  homePageEditorRef,
  instructionsListenerRegistered,
  homePageListenerRegistered,
}: GeneralTabProps) => {
  const [searchParams, setSearchParams] = useSearchParams();
  
  // Keep stable references to change handlers and initial values
  // These refs reset when GeneralTab remounts (when switching main tabs)
  const onInstructionsChangeRef = useRef(onInstructionsChange);
  const onHomePageMarkdownChangeRef = useRef(onHomePageMarkdownChange);
  const initialInstructionsRef = useRef(instructions);
  const initialHomePageMarkdownRef = useRef(homePageMarkdown);
  const hasInitializedInstructionsRef = useRef(false);
  const hasInitializedHomePageRef = useRef(false);

  useEffect(() => { onInstructionsChangeRef.current = onInstructionsChange; }, [onInstructionsChange]);
  useEffect(() => { onHomePageMarkdownChangeRef.current = onHomePageMarkdownChange; }, [onHomePageMarkdownChange]);
  
  // Persist subTab in URL for navigation stability
  const subTab = (searchParams.get('subTab') || 'basic') as 'basic' | 'instructions' | 'homepage' | 'context' | 'starters';

  const setSubTab = useCallback((tab: 'basic' | 'instructions' | 'homepage' | 'context' | 'starters') => {
    setSearchParams((prev) => {
      const newParams = new URLSearchParams(prev);
      newParams.set('subTab', tab);
      return newParams;
    });
  }, [setSearchParams]);

  // Stable onReady handlers that set the content and register listeners
  // These callbacks MUST NOT change on every render to avoid cursor jumping
  const handleInstructionsReady = useCallback(() => {
    // Set initial value once per GeneralTab mount (editor instance)
    if (!hasInitializedInstructionsRef.current) {
      instructionsEditorRef.current?.setValue(initialInstructionsRef.current);
      hasInitializedInstructionsRef.current = true;
    }
    
    // Register listener once per editor instance
    if (!instructionsListenerRegistered.current) {
      instructionsEditorRef.current?.registerChangeListener((markdown) => {
        onInstructionsChangeRef.current(markdown);
      });
      instructionsListenerRegistered.current = true;
    }
  }, [instructionsEditorRef, instructionsListenerRegistered]);

  const handleHomePageReady = useCallback(() => {
    // Set initial value once per GeneralTab mount (editor instance)
    if (!hasInitializedHomePageRef.current) {
      homePageEditorRef.current?.setValue(initialHomePageMarkdownRef.current);
      hasInitializedHomePageRef.current = true;
    }
    
    // Register listener once per editor instance
    if (!homePageListenerRegistered.current) {
      homePageEditorRef.current?.registerChangeListener((markdown) => {
        onHomePageMarkdownChangeRef.current(markdown);
      });
      homePageListenerRegistered.current = true;
    }
  }, [homePageEditorRef, homePageListenerRegistered]);

  const subTabs = [
    { id: 'basic' as const, label: 'Basic Info' },
    { id: 'instructions' as const, label: 'Instructions' },
    ...(showHomePage ? [{ id: 'homepage' as const, label: 'Home Page' }] : []),
    { id: 'starters' as const, label: 'Conversation Starters' },
    { id: 'context' as const, label: 'Context Options' },
  ];

  return (
    <div className="bg-white rounded-lg border border-gray-200 overflow-hidden" data-tour-id="guide.general.container">
      <div className="flex border-b border-gray-200" data-tour-id="guide.general.subtabs">
        {subTabs.map((tab) => (
          <button
            key={tab.id}
            type="button"
            onClick={() => setSubTab(tab.id)}
            className={`px-6 py-3 text-sm font-medium ${subTab === tab.id
                ? 'text-blue-600 bg-blue-50 border-b-2 border-blue-600'
                : 'text-gray-600 hover:text-gray-900 hover:bg-gray-50'
              }`}
            data-tour-id={
              tab.id === 'basic' ? 'guide.general.subtab.basic' :
              tab.id === 'instructions' ? 'guide.general.subtab.instructions' :
              tab.id === 'homepage' ? 'guide.general.subtab.homepage' :
              tab.id === 'starters' ? 'guide.general.subtab.starters' :
              'guide.general.subtab.context'
            }
          >
            {tab.label}
          </button>
        ))}
      </div>

      <div className="p-6">
        <div className={subTab === 'basic' ? '' : 'hidden'} data-tour-id="guide.general.basic.section">
          <BasicInfo
            name={name}
            description={description}
            avatarData={avatarData}
            currentAvatarUrl={currentAvatarUrl}
            onNameChange={onNameChange}
            onDescriptionChange={onDescriptionChange}
            onAvatarChange={onAvatarChange}
          />
        </div>

        <div className={subTab === 'instructions' ? 'space-y-4' : 'hidden'} data-tour-id="guide.content.instructions.section">
          <div>
            <h2 className="text-lg font-semibold text-gray-900">Instructions</h2>
            <p className="text-sm text-gray-600 mt-1">
              Define how the guide should behave and respond. Be specific about tone, style, and any constraints.
            </p>
          </div>
          <LexicalEditor
            ref={instructionsEditorRef}
            placeholder="You are a helpful assistant that..."
            showToolbar={true}
            className="min-h-[400px] border border-gray-300 rounded-md"
            toolbarClassName="border-b border-gray-200 bg-gray-50"
            onReady={handleInstructionsReady}
            data-tour-id="guide.content.instructions.editor"
          />
        </div>

        <div className={subTab === 'homepage' ? 'space-y-4' : 'hidden'} data-tour-id="guide.content.homepage.section">
          <div>
            <h2 className="text-lg font-semibold text-gray-900">Home Page</h2>
            <p className="text-sm text-gray-600 mt-1">
              This content will be displayed on the guide's home page when users first access it.
            </p>
          </div>
          <LexicalEditor
            ref={homePageEditorRef}
            placeholder="# Welcome to [Guide Name]&#10;&#10;This guide helps you with..."
            showToolbar={true}
            className="min-h-[400px] border border-gray-300 rounded-md"
            toolbarClassName="border-b border-gray-200 bg-gray-50"
            onReady={handleHomePageReady}
            data-tour-id="guide.content.homePage.editor"
          />
        </div>

        {subTab === 'context' && (
          <div className="space-y-4" data-tour-id="guide.config.context.section">
            <div>
              <h2 className="text-lg font-semibold text-gray-900">Context Options</h2>
              <p className="text-sm text-gray-600 mt-1">
                Context Options are like sticky notes that the guide can see during conversations. They help the guide remember useful information without users having to repeat it every time.
              </p>

              <div className="mt-4 bg-green-50 border border-green-200 rounded-md p-4" data-tour-id="guide.config.context.howItWorks">
                <h3 className="text-sm font-semibold text-green-900 mb-2">How It Works</h3>
                <div className="space-y-2 text-sm text-green-900">
                  <div>
                    <span className="font-semibold">1. Add keys below</span> - Give each setting a name (e.g., <code className="bg-green-100 px-1 rounded">outlook.timezone</code>)
                  </div>
                  <div>
                    <span className="font-semibold">2. Values are optional</span> - You can pre-fill values (like <code className="bg-green-100 px-1 rounded">America/New_York</code>) OR leave them empty for the guide to ask users or discover from conversation
                  </div>
                  <div>
                    <span className="font-semibold">3. Reference them in Instructions</span> - Tell the guide when to use or collect these values (e.g., "If outlook.timezone is not set, ask the user for their timezone and save it")
                  </div>
                  <div>
                    <span className="font-semibold">4. The guide uses them automatically</span> - Once set, these values are available in all future conversations with that user
                  </div>
                </div>
                <div className="mt-3 p-3 bg-white rounded border border-green-300">
                  <p className="text-xs font-semibold text-green-800 mb-1">💡 Two Ways to Use:</p>
                  <div className="text-xs text-green-900 space-y-2">
                    <div>
                      <strong>Pre-filled values:</strong><br />
                      <code className="bg-green-100 px-1">system.currentDate</code> = <code className="bg-green-100 px-1">[@currentDate]</code> → Always uses today's date
                    </div>
                    <div>
                      <strong>Empty values (guide collects):</strong><br />
                      <code className="bg-green-100 px-1">outlook.timezone</code> = <em>(empty)</em> → Guide asks user and saves it for next time
                    </div>
                  </div>
                </div>
                {contextOptions.some(opt => !opt.value || opt.value.trim() === '') && (
                  <div className="mt-3 p-2 bg-amber-50 border border-amber-300 rounded">
                    <p className="text-xs text-amber-900">
                      <strong>ℹ️ Note:</strong> You have keys with empty values. The "Set Context Options" tool will be automatically enabled so the guide can collect and save these values during conversations.
                    </p>
                  </div>
                )}
              </div>

              <div className="mt-4 bg-blue-50 border border-blue-200 rounded-md p-4" data-tour-id="guide.config.context.smartFns.help">
                <h3 className="text-sm font-semibold text-blue-900 mb-2">Built-in Smart Values</h3>
                <p className="text-sm text-blue-800 mb-3">
                  Use these special functions in the <strong>Value</strong> field - they're automatically resolved at runtime:
                </p>
                <table className="min-w-full text-sm">
                  <thead>
                    <tr className="border-b border-blue-200">
                      <th className="text-left py-2 px-3 text-blue-900 font-semibold">Example Key</th>
                      <th className="text-left py-2 px-3 text-blue-900 font-semibold">Value (use this)</th>
                      <th className="text-left py-2 px-3 text-blue-900 font-semibold">Resolves To</th>
                    </tr>
                  </thead>
                  <tbody className="text-blue-900">
                    <tr className="border-b border-blue-100">
                      <td className="py-2 px-3 font-mono text-xs">today</td>
                      <td className="py-2 px-3 font-mono text-xs bg-blue-100 rounded">[@currentDate]</td>
                      <td className="py-2 px-3 font-mono text-xs">2025-10-06</td>
                    </tr>
                    <tr className="border-b border-blue-100">
                      <td className="py-2 px-3 font-mono text-xs">user</td>
                      <td className="py-2 px-3 font-mono text-xs bg-blue-100 rounded">[@userName]</td>
                      <td className="py-2 px-3 font-mono text-xs">Sarah Johnson</td>
                    </tr>
                    <tr className="border-b border-blue-100">
                      <td className="py-2 px-3 font-mono text-xs">email</td>
                      <td className="py-2 px-3 font-mono text-xs bg-blue-100 rounded">[@userEmail]</td>
                      <td className="py-2 px-3 font-mono text-xs">sarah@company.com</td>
                    </tr>
                    <tr>
                      <td className="py-2 px-3 font-mono text-xs">files</td>
                      <td className="py-2 px-3 font-mono text-xs bg-blue-100 rounded">[@files]</td>
                      <td className="py-2 px-3 font-mono text-xs">{`{"files":["../README.md",...]}`}</td>
                    </tr>
                  </tbody>
                </table>
              </div>
            </div>
            <div data-tour-id="guide.config.context.grid">
            <ContextOptions options={contextOptions} onChange={onContextOptionsChange} />
            </div>
          </div>
        )}

        {subTab === 'starters' && (
          <div data-tour-id="guide.starters.section">
          <ConversationStarters
            starters={conversationStarters}
            onChange={onConversationStartersChange}
          />
          </div>
        )}
      </div>
    </div>
  );
};

// Wrap in React.memo with custom comparison to prevent unnecessary re-renders
// Only re-render if the actual values change, not just object references
export const GeneralTab = React.memo(GeneralTabComponent, (prevProps, nextProps) => {
  // Compare primitive values
  if (prevProps.name !== nextProps.name) return false;
  if (prevProps.description !== nextProps.description) return false;
  if (prevProps.instructions !== nextProps.instructions) return false;
  if (prevProps.homePageMarkdown !== nextProps.homePageMarkdown) return false;
  if (prevProps.showHomePage !== nextProps.showHomePage) return false;
  if (prevProps.currentAvatarUrl !== nextProps.currentAvatarUrl) return false;
  
  // Compare avatarData
  if (prevProps.avatarData !== nextProps.avatarData) {
    if (!prevProps.avatarData || !nextProps.avatarData) return false;
    if (prevProps.avatarData.contentType !== nextProps.avatarData.contentType) return false;
    if (prevProps.avatarData.bytes.length !== nextProps.avatarData.bytes.length) return false;
  }
  
  // Compare arrays by length and content (not just reference)
  if (prevProps.contextOptions.length !== nextProps.contextOptions.length) return false;
  // Also check if context option contents have changed
  if (prevProps.contextOptions.some((opt, i) => 
    opt.key !== nextProps.contextOptions[i]?.key || opt.value !== nextProps.contextOptions[i]?.value
  )) return false;
  
  if (prevProps.conversationStarters.length !== nextProps.conversationStarters.length) return false;
  // Check if conversation starters contents have changed
  if (prevProps.conversationStarters.some((s, i) => s !== nextProps.conversationStarters[i])) return false;
  
  // All callbacks should be stable (memoized), refs are always stable
  // Return true to prevent re-render (props are equal)
  return true;
});

