import React, { useState, useEffect } from 'react';
import { ProjectDetailsDto } from '../../types/project';
import { TwoColumnLayout } from './TwoColumnLayout';
import { TourStartButton } from '../../tour/TourStartButton';
import { SettingsButton } from '../common/SettingsButton';
import { HiMenu } from 'react-icons/hi';

interface ProjectLayoutProps {
    project: ProjectDetailsDto;
    sidebar: React.ReactNode;
    content: React.ReactNode;
    onBack: () => void;
    canEdit: boolean;
    onEdit?: () => void;
    className?: string;
    title?: string; // Optional override for title (useful for notebook view)
    subtitle?: string; // Optional subtitle (useful for notebook view)
    backButtonLabel?: string; // Optional label for the back button (defaults to "Back to Projects")
    editButtonLabel?: string; // Optional label for the edit button (defaults to "Edit Project")
    tourScreenId?: string; // Optional tour screen ID to show help button
}

function ProjectHeader({ 
    project, 
    onEdit, 
    onBack, 
    canEdit,
    title, 
    subtitle,
    editButtonLabel = "Edit Project",
    tourScreenId,
    onMobileSidebarToggle,
}: { 
    project: ProjectDetailsDto; 
    onEdit?: () => void; 
    onBack: () => void;
    canEdit: boolean;
    title?: string;
    subtitle?: string;
    backButtonLabel?: string;
    editButtonLabel?: string;
    tourScreenId?: string;
    onMobileSidebarToggle?: () => void;
}) {
    const displayTitle = title || project.title;
    const displaySubtitle = subtitle || project.description;

    return (
        <div className="border-b py-2 px-4 bg-white">
            <div className="flex items-center justify-between">
                <div className="flex items-center min-w-0">
                    {/* Mobile hamburger menu */}
                    {onMobileSidebarToggle && (
                        <button
                            onClick={onMobileSidebarToggle}
                            className="md:hidden p-2 mr-2 rounded hover:bg-gray-100"
                            aria-label="Toggle sidebar"
                        >
                            <HiMenu className="w-5 h-5" />
                        </button>
                    )}
                    <h1 className="text-lg font-bold mr-2 truncate">{displayTitle}</h1>
                    {displaySubtitle && (
                        <>
                            <span className="text-gray-600 mr-2 flex-shrink-0 hidden sm:inline">—</span>
                            <span className="text-gray-600 truncate hidden sm:inline">{displaySubtitle}</span>
                        </>
                    )}
                </div>
                <div className="flex gap-2 flex-shrink-0">
                    {canEdit && onEdit && (
                        <button
                            onClick={onEdit}
                            className="h-10 px-4 text-sm bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors flex items-center"
                        >
                            {editButtonLabel}
                        </button>
                    )}
                    <button
                        onClick={onBack}
                        aria-label="Back to Home"
                        title="Home"
                        className="h-10 w-10 border rounded-md hover:bg-gray-50 transition-colors flex items-center justify-center"
                    >
                        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="currentColor" className="w-4 h-4">
                            <path d="M11.47 3.84a.75.75 0 0 1 1.06 0l8 8a.75.75 0 1 1-1.06 1.06l-.72-.72V19.5A2.25 2.25 0 0 1 16.5 21.75h-2.25a.75.75 0 0 1-.75-.75v-3.75a1.5 1.5 0 0 0-1.5-1.5h-1.5a1.5 1.5 0 0 0-1.5 1.5V21a.75.75 0 0 1-.75.75H4.5A2.25 2.25 0 0 1 2.25 19.5v-7.32l-.72.72a.75.75 0 1 1-1.06-1.06l8-8Z" />
                        </svg>
                        <span className="sr-only">Home</span>
                    </button>
                    <SettingsButton />
                    {tourScreenId && <TourStartButton screenId={tourScreenId} inline />}
                </div>
            </div>
            <div className="text-sm text-gray-600 mt-0.5">
                Created on {new Date(project.created).toLocaleDateString()}
            </div>
        </div>
    );
}

export function ProjectLayout({
    project,
    sidebar,
    content,
    onBack,
    canEdit,
    onEdit,
    className = '',
    title,
    subtitle,
    backButtonLabel,
    editButtonLabel,
    tourScreenId,
}: ProjectLayoutProps) {
    // Track sidebar size and collapse state so the main layout can stay in sync
    const [sidebarWidth, setSidebarWidth] = useState<number>(() => {
        const stored = localStorage.getItem('sidebarWidth');
        return stored ? Number(stored) : 256;
    });

    const [sidebarCollapsed, setSidebarCollapsed] = useState<boolean>(() => {
        const stored = localStorage.getItem('sidebarCollapsed');
        return stored ? stored === 'true' : false;
    });

    // Mobile state
    const [isMobile, setIsMobile] = useState(false);
    const [isMobileSidebarOpen, setIsMobileSidebarOpen] = useState(false);

    useEffect(() => {
        const checkMobile = () => setIsMobile(window.innerWidth < 768);
        checkMobile();
        window.addEventListener('resize', checkMobile);
        return () => window.removeEventListener('resize', checkMobile);
    }, []);

    // Throttle width updates to animation frames for smoother resizing
    const widthRaf = React.useRef<number | null>(null);
    const handleWidthChange = React.useCallback((w: number) => {
        if (widthRaf.current) cancelAnimationFrame(widthRaf.current);
        widthRaf.current = requestAnimationFrame(() => {
            setSidebarWidth(w);
            localStorage.setItem('sidebarWidth', w.toString());
        });
    }, []);

    // Inject callbacks into an existing SidebarContainer (if present) so that
    // width / collapse changes propagate upward.
    const enhancedSidebar = React.isValidElement(sidebar)
        ? React.cloneElement(sidebar as React.ReactElement<any>, {
              defaultWidth: sidebarWidth,
              initialCollapsed: sidebarCollapsed,
              onWidthChange: handleWidthChange,
              onCollapseChange: (c: boolean) => {
                  setSidebarCollapsed(c);
                  localStorage.setItem('sidebarCollapsed', c.toString());
              },
              // Mobile props
              isMobile,
              isMobileOpen: isMobileSidebarOpen,
              onMobileClose: () => setIsMobileSidebarOpen(false),
          })
        : sidebar;

    React.useEffect(() => {
        return () => {
            if (widthRaf.current) cancelAnimationFrame(widthRaf.current);
        };
    }, []);

    return (
        <div className={`h-screen flex flex-col overflow-hidden ${className}`}>
            <ProjectHeader
                project={project}
                onEdit={onEdit}
                onBack={onBack}
                canEdit={canEdit}
                title={title}
                subtitle={subtitle}
                backButtonLabel={backButtonLabel}
                editButtonLabel={editButtonLabel}
                tourScreenId={tourScreenId}
                onMobileSidebarToggle={() => setIsMobileSidebarOpen(prev => !prev)}
            />

            <TwoColumnLayout
                leftColumn={enhancedSidebar}
                leftColumnWidth={sidebarWidth}
                isLeftCollapsed={sidebarCollapsed}
                rightColumn={
                    <div className="flex-1 p-2 md:p-4 flex flex-col min-h-0">
                        {content}
                    </div>
                }
                isMobile={isMobile}
                isMobileSidebarOpen={isMobileSidebarOpen}
                onMobileSidebarClose={() => setIsMobileSidebarOpen(false)}
            />
        </div>
    );
} 
