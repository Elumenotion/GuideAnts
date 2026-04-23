import React, { createContext, useCallback, useContext, useMemo, useRef, useState } from 'react';

type JoyrideStep = {
  target: string;
  content: string;
  placement?: 'top' | 'bottom' | 'left' | 'right' | 'auto';
  spotlightPadding?: number;
  popoverOffset?: number; // Distance from element (perpendicular)
  popoverAlignOffset?: number; // Position along edge (parallel)
  onHighlight?: () => void; // Optional action to perform when this step is highlighted
  onDeselect?: () => void; // Optional action to perform when leaving this step
};

type RegisteredTours = Map<string, JoyrideStep[]>;

type TourContextValue = {
  registerTourSteps: (screenId: string, steps: JoyrideStep[]) => () => void;
  startTour: (screenId: string) => Promise<void>;
  isRegistered: (screenId: string) => boolean;
  isRunning: boolean;
  activeScreenId?: string;
};

const TourContext = createContext<TourContextValue | undefined>(undefined);

export function TourProvider({ children }: { children: React.ReactNode }) {
  const registryRef = useRef<RegisteredTours>(new Map());
  const [isRunning, setIsRunning] = useState(false);
  const [activeScreenId, setActiveScreenId] = useState<string | undefined>(undefined);
  const driverModuleRef = useRef<typeof import('driver.js') | null>(null);
  const driverInstanceRef = useRef<{ drive: () => void; destroy?: () => void } | null>(null);

  const registerTourSteps = useCallback((screenId: string, newSteps: JoyrideStep[]) => {
    registryRef.current.set(screenId, newSteps);
    return () => {
      if (registryRef.current.get(screenId) === newSteps) {
        registryRef.current.delete(screenId);
      }
    };
  }, []);

  const isRegistered = useCallback((screenId: string) => registryRef.current.has(screenId), []);

  const startTour = useCallback(async (screenId: string) => {
    const candidate = registryRef.current.get(screenId);
    if (!candidate || candidate.length === 0) {
      throw new Error(`No tour registered for screen "${screenId}".`);
    }
    
    // Filter to only include steps with valid targets
    // But allow steps that might appear dynamically (like context menus and dropdowns)
    const validSteps = candidate.filter(s => {
      if (!s.target) return false;
      const element = document.querySelector(s.target);
      // Allow steps that don't exist yet if they're likely to appear dynamically
      if (!element && (s.target.includes('context-menu') || s.target.includes('.dropdown'))) {
        return true; // Keep dynamic element steps even if not present yet
      }
      return element !== null;
    });
    
    if (validSteps.length === 0) {
      throw new Error(`No valid tour targets found for screen "${screenId}".`);
    }
    
    if (!driverModuleRef.current) {
      const mod = await import('driver.js');
      driverModuleRef.current = mod;
    }
    setActiveScreenId(screenId);
    setIsRunning(true);

    const { driver } = driverModuleRef.current;
    
    // Track which step we're on
    let currentStepIndex = -1;
    
    // Helper to close all context menus
    const closeAllContextMenus = () => {
      window.dispatchEvent(new Event('close-context-menus'));
    };
    
    // Helper to wait for an element to exist
    const waitForElement = (selector: string, timeout = 2000): Promise<Element | null> => {
      return new Promise((resolve) => {
        const element = document.querySelector(selector);
        if (element) {
          resolve(element);
          return;
        }
        
        const startTime = Date.now();
        const interval = setInterval(() => {
          const el = document.querySelector(selector);
          if (el) {
            clearInterval(interval);
            resolve(el);
          } else if (Date.now() - startTime > timeout) {
            clearInterval(interval);
            resolve(null);
          }
        }, 50);
      });
    };
    
    // Build steps with per-step lifecycle hooks
    const driverSteps = validSteps.map((step, index) => {
      const isContextMenuStep = step.target.includes('context-menu');
      const isDropdownStep = step.target.includes('.dropdown');
      
      return {
        element: step.target,
        popover: {
          description: step.content,
          side: (step.placement === 'auto' ? 'left' : (step.placement || 'left')) as 'left' | 'right' | 'top' | 'bottom',
          align: 'start' as const,
          offset: step.popoverOffset,
          alignOffset: step.popoverAlignOffset,
          // Per-step lifecycle hooks
          onPopoverRender: async (popover: any, options: any) => {
            const { state } = options;
            const newIndex = state.activeIndex ?? index;
            
            // Apply custom positioning offsets if specified
            if (step.popoverOffset !== undefined || step.popoverAlignOffset !== undefined) {
              const popoverElement = popover.wrapper as HTMLElement;
              if (popoverElement) {
                // Apply offsets using CSS transforms
                const offsetX = step.popoverOffset ?? 0;
                const offsetY = step.popoverAlignOffset ?? 0;
                popoverElement.style.transform = `translate(${offsetX}px, ${offsetY}px)`;
              }
            }
            
            // Close menus when leaving a previous step
            if (currentStepIndex >= 0 && currentStepIndex !== newIndex) {
              const previousStep = validSteps[currentStepIndex];
              const wasPreviousStepContextMenu = previousStep?.target.includes('context-menu');
              const wasPreviousStepDropdown = previousStep?.target.includes('.dropdown');
              
              // Only close dynamic element if we're leaving one AND not going to another of the same type
              if (wasPreviousStepContextMenu && !isContextMenuStep) {
                if (previousStep?.onDeselect) {
                  previousStep.onDeselect();
                }
                closeAllContextMenus();
              } else if (wasPreviousStepDropdown && !isDropdownStep) {
                if (previousStep?.onDeselect) {
                  previousStep.onDeselect();
                }
              } else if (!wasPreviousStepContextMenu && !wasPreviousStepDropdown && previousStep?.onDeselect) {
                // For non-dynamic steps, just call onDeselect
                previousStep.onDeselect();
              }
            }
            
            currentStepIndex = newIndex;
            
            // If this is a dynamic element step (context menu or dropdown), ensure it's open BEFORE driver.js positions the popover
            if (isContextMenuStep || isDropdownStep) {
              // Both context menus and dropdowns work the same way now:
              // The data-tour-id is on the conditionally rendered element itself
              let dynamicElement = document.querySelector(step.target);
              
              if (!dynamicElement) {
                // Element doesn't exist yet
                // Hide popover while we open the dynamic element and reposition
                const popoverElement = document.querySelector('.driver-popover') as HTMLElement;
                if (popoverElement) {
                  popoverElement.style.visibility = 'hidden';
                }
                
                if (isContextMenuStep) {
                  // Close any other menus first
                  closeAllContextMenus();
                  // Small delay to let close event propagate
                  await new Promise(resolve => setTimeout(resolve, 50));
                }
                
                // Trigger element to open
                if (step.onHighlight) {
                  step.onHighlight();
                }
                
                // Wait for element to appear
                dynamicElement = await waitForElement(step.target, 2000);
                
                if (dynamicElement) {
                  // Ensure element is above overlay
                  (dynamicElement as HTMLElement).style.zIndex = '10001';
                  
                  // Force a reflow to ensure layout is complete
                  void (dynamicElement as HTMLElement).offsetHeight;
                  
                  // Wait for element to be fully rendered and positioned
                  await new Promise(resolve => setTimeout(resolve, 200));
                  
                  // Re-apply custom positioning after dynamic element is fully positioned
                  const popoverElement = document.querySelector('.driver-popover') as HTMLElement;
                  
                  if (isDropdownStep && (step.popoverOffset !== undefined || step.popoverAlignOffset !== undefined)) {
                    // For dropdown steps, position relative to the button, not the dropdown panel
                    const buttonWrapper = document.querySelector('[data-tour-id="conversation.assistant.selector"]') as HTMLElement;
                    
                    if (popoverElement && buttonWrapper) {
                      const buttonRect = buttonWrapper.getBoundingClientRect();
                      
                      // Position popover below and to the left of the button
                      const popoverX = buttonRect.right - 600; // Left edge relative to button right
                      const popoverY = buttonRect.bottom + 10; // Below the button with 10px gap
                      
                      popoverElement.style.inset = `${popoverY}px auto auto ${popoverX}px`;
                      popoverElement.style.transform = 'none';
                    }
                  } else if (isContextMenuStep && popoverElement) {
                    // For context menu steps, position popover relative to the TRIGGER element (root folder),
                    // not the context menu itself. The menu is just displayed alongside.
                    // Find the root folder element that triggered the menu
                    const rootFolder = document.querySelector('[data-tour-id="sidebar.folder.root"]') as HTMLElement
                      || document.querySelector('[data-tour-id="notebook.folder.root"]') as HTMLElement;
                    
                    if (rootFolder) {
                      const triggerRect = rootFolder.getBoundingClientRect();
                      const placement = step.placement || 'right';
                      
                      let popoverX: number;
                      let popoverY: number;
                      
                      if (placement === 'right') {
                        // Position to the right of the context menu (which is to the right of trigger)
                        const menuElement = dynamicElement as HTMLElement;
                        const menuRect = menuElement?.getBoundingClientRect();
                        popoverX = menuRect ? menuRect.right + 10 : triggerRect.right + 200;
                        popoverY = triggerRect.top;
                      } else if (placement === 'left') {
                        const popoverRect = popoverElement.getBoundingClientRect();
                        popoverX = triggerRect.left - popoverRect.width - 10;
                        popoverY = triggerRect.top;
                      } else if (placement === 'bottom') {
                        popoverX = triggerRect.left;
                        popoverY = triggerRect.bottom + 10;
                      } else {
                        // top or default
                        const popoverRect = popoverElement.getBoundingClientRect();
                        popoverX = triggerRect.left;
                        popoverY = triggerRect.top - popoverRect.height - 10;
                      }
                      
                      // Apply any additional offsets
                      if (step.popoverOffset !== undefined) {
                        popoverX += step.popoverOffset;
                      }
                      if (step.popoverAlignOffset !== undefined) {
                        popoverY += step.popoverAlignOffset;
                      }
                      
                      popoverElement.style.inset = `${popoverY}px auto auto ${popoverX}px`;
                      popoverElement.style.transform = 'none';
                    }
                  } else if (step.popoverOffset !== undefined || step.popoverAlignOffset !== undefined) {
                    // For other steps with custom offsets, use the transform approach
                    const popoverEl = document.querySelector('.driver-popover') as HTMLElement;
                    if (popoverEl) {
                      const offsetX = step.popoverOffset ?? 0;
                      const offsetY = step.popoverAlignOffset ?? 0;
                      popoverEl.style.transform = `translate(${offsetX}px, ${offsetY}px)`;
                    }
                  }
                  
                  // Show popover after repositioning
                  const finalPopover = document.querySelector('.driver-popover') as HTMLElement;
                  if (finalPopover) {
                    finalPopover.style.visibility = 'visible';
                  }
                } else {
                  console.warn(`Dynamic element ${step.target} did not appear after onHighlight`);
                  // Show popover anyway even if element didn't appear
                  const finalPopover = document.querySelector('.driver-popover') as HTMLElement;
                  if (finalPopover) {
                    finalPopover.style.visibility = 'visible';
                  }
                }
              } else {
                // Element already exists (transitioning between steps that share same element)
                // Just ensure z-index is correct
                (dynamicElement as HTMLElement).style.zIndex = '10001';
                
                // Small delay to let driver.js catch up
                await new Promise(resolve => setTimeout(resolve, 50));
              }
            } else {
              // Standard step, call onHighlight if it exists
              if (step.onHighlight) {
                step.onHighlight();
              }
            }
          }
        }
      };
    });
    
    // Create driver instance
    const instance = driver({
      steps: driverSteps,
      allowClose: true,
      onDestroyed: () => {
        // Call onDeselect for current step
        if (currentStepIndex >= 0) {
          const currentStep = validSteps[currentStepIndex];
          if (currentStep?.onDeselect) {
            currentStep.onDeselect();
          }
        }
        closeAllContextMenus();
        setIsRunning(false);
        setActiveScreenId(undefined);
      }
    }) as { drive: () => void; destroy?: () => void };
    
    driverInstanceRef.current = instance;
    instance.drive();
  }, []);

  const value = useMemo(
    () => ({ registerTourSteps, startTour, isRegistered, isRunning, activeScreenId }),
    [registerTourSteps, startTour, isRegistered, isRunning, activeScreenId]
  );

  return (
    <TourContext.Provider value={value}>
      {children}
    </TourContext.Provider>
  );
}

export function useTour() {
  const ctx = useContext(TourContext);
  if (!ctx) throw new Error('useTour must be used within TourProvider');
  return ctx;
}


