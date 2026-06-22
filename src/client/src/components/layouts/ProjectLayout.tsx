import React, { useState, useEffect } from 'react';
import { ProjectDetailsDto } from '../../types/project';
import { TwoColumnLayout } from './TwoColumnLayout';
import { TourStartButton } from '../../tour/TourStartButton';
import { HomeButton } from '../common/HomeButton';
import { SettingsButton } from '../common/SettingsButton';
import { HeaderActionsBar } from '../common/HeaderActionsBar';
import { GuideAntsGuideButton } from '../../features/guideantsGuide/GuideAntsGuideButton';
import { HeaderUserMenu } from '../common/HeaderUserMenu';
import { HiMenu } from 'react-icons/hi';
import { FiEdit2 } from 'react-icons/fi';

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
    canEdit,
    title, 
    subtitle,
    editButtonLabel = "Edit Project",
    tourScreenId,
    onMobileSidebarToggle,
}: { 
    project: ProjectDetailsDto; 
    onEdit?: () => void; 
    canEdit: boolean;
    title?: string;
    subtitle?: string;
    editButtonLabel?: string;
    tourScreenId?: string;
    onMobileSidebarToggle?: () => void;
}) {
    const displayTitle = title || project.title;
    const displaySubtitle = subtitle || project.description;

    return (
        <div className="border-b py-2 px-4 bg-white">
            <div className="flex items-center justify-between gap-2">
                <div className="flex min-w-0 flex-1 items-center">
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
                <HeaderActionsBar>
                    <GuideAntsGuideButton />
                    {canEdit && onEdit && (
                        <button
                            onClick={onEdit}
                            className="h-10 w-10 border border-gray-300 rounded-md hover:bg-gray-50 transition-colors flex items-center justify-center text-gray-700 bg-white"
                            aria-label={editButtonLabel}
                            title={editButtonLabel}
                        >
                            <FiEdit2 className="w-4 h-4" />
                        </button>
                    )}
                    <HomeButton />
                    <SettingsButton />
                    <HeaderUserMenu />
                    <TourStartButton screenId={tourScreenId ?? 'projectDetails'} inline />
                </HeaderActionsBar>
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
    onBack: _onBack,
    canEdit,
    onEdit,
    className = '',
    title,
    subtitle,
    backButtonLabel: _backButtonLabel,
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
                canEdit={canEdit}
                title={title}
                subtitle={subtitle}
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
