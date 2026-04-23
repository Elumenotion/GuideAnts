import { useState, useCallback } from 'react';

export interface UseSidebarSelectionCoordinatorOptions {
  sections: string[]; // e.g., ['notebooks', 'files', 'links']
}

export interface UseSidebarSelectionCoordinatorReturn {
  activeSection: string | null;
  activateSection: (section: string) => void;
  deactivateSection: () => void;
  isSectionActive: (section: string) => boolean;
}

export function useSidebarSelectionCoordinator({
  sections
}: UseSidebarSelectionCoordinatorOptions): UseSidebarSelectionCoordinatorReturn {
  const [activeSection, setActiveSection] = useState<string | null>(null);

  const activateSection = useCallback((section: string) => {
    if (sections.includes(section)) {
      setActiveSection(section);
    } else {
      console.warn(`Attempted to activate unknown section: ${section}`);
    }
  }, [sections]);

  const deactivateSection = useCallback(() => {
    setActiveSection(null);
  }, []);

  const isSectionActive = useCallback((section: string) => {
    return activeSection === section;
  }, [activeSection]);

  return {
    activeSection,
    activateSection,
    deactivateSection,
    isSectionActive
  };
}

