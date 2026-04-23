import React, { useState } from 'react';
import { resolveAgainstApiBase } from '../../config/apiConfig';
import { ProjectDetailsDto } from '../../types/project';
import { NotebookDetailsDto } from '../../types/notebook';
import { NotebookTemplateDto } from '../../types/project';
import { TwoColumnLayout } from './TwoColumnLayout';
import { TourStartButton } from '../../tour/TourStartButton';
import { HiMenu } from 'react-icons/hi';

interface NotebookLayoutProps {
    project: ProjectDetailsDto;
    notebook: NotebookDetailsDto;
    notebookTemplate?: NotebookTemplateDto; // Template info for the banner
    sidebar: React.ReactNode;
    content: React.ReactNode;
    onBack: () => void;
    canEdit: boolean;
    onEdit?: () => void;
    className?: string;
    tourScreenId?: string; // Optional tour screen ID to show help button
    // Mobile sidebar state passed from parent (NotebookDetails)
    isMobileSidebarOpen?: boolean;
    onMobileSidebarToggle?: () => void;
    onMobileSidebarClose?: () => void;
    /** Centered header area (e.g. service toolbar). Hidden on &lt; md when the child handles a compact mobile trigger. */
    headerCenter?: React.ReactNode;
}

function NotebookHeader({ 
    project, 
    notebook,
    notebookTemplate,
    onEdit, 
    onBack, 
    canEdit,
    tourScreenId,
    onMobileSidebarToggle,
    headerCenter,
    isMobile,
}: { 
    project: ProjectDetailsDto; 
    notebook: NotebookDetailsDto;
    notebookTemplate?: NotebookTemplateDto;
    onEdit?: () => void; 
    onBack: () => void;
    canEdit: boolean;
    tourScreenId?: string;
    onMobileSidebarToggle?: () => void;
    headerCenter?: React.ReactNode;
    isMobile: boolean;
}) {
    return (
        <div className="border-b bg-white">
            {/* Main Header */}
            <div className="py-2 px-2 md:px-4">
                <div className="flex items-center justify-between gap-2 min-w-0">
                    <div className="flex items-center min-w-0 flex-1 md:max-w-[40%]">
                        {/* Mobile Hamburger Menu */}
                        <button
                            onClick={onMobileSidebarToggle}
                            className="md:hidden mr-2 p-1.5 text-gray-500 hover:bg-gray-100 rounded-md focus:outline-none"
                            aria-label="Toggle sidebar"
                        >
                            <HiMenu className="w-5 h-5" />
                        </button>

                        {/* Template Avatar Icon */}
                        {notebookTemplate?.avatarUrl && (() => {
                            let src = notebookTemplate.avatarUrl.startsWith('http')
                                ? notebookTemplate.avatarUrl
                                : resolveAgainstApiBase(notebookTemplate.avatarUrl).toString();
                            // Append projectId to avatar URL if it contains /api/ and doesn't already have projectId
                            if (src && src.includes('/api/') && !src.includes('projectId=')) {
                                src = `${src}${src.includes('?') ? '&' : '?'}projectId=${project.id}`;
                            }
                            return (
                                <div 
                                    className="flex-shrink-0 mr-3 cursor-pointer hidden sm:block"
                                    title={`${notebookTemplate.templateName}${notebookTemplate.description ? ` - ${notebookTemplate.description}` : ''}`}
                                >
                                    <img 
                                        src={src}
                                        alt={notebookTemplate.templateName}
                                        className="w-6 h-6 rounded"
                                    />
                                </div>
                            );
                        })()}
                        <h1 className="text-lg font-bold truncate">{notebook.title}</h1>
                    </div>
                    {headerCenter && (
                        <div
                            data-testid="notebook-header-center"
                            className={
                                isMobile
                                    ? 'flex-shrink-0 flex items-center'
                                    : 'hidden md:flex flex-1 min-w-0 justify-center items-center px-1'
                            }
                        >
                            {headerCenter}
                        </div>
                    )}
                    <div className="flex gap-1 sm:gap-2 flex-shrink-0 items-center">
                        {canEdit && onEdit && (
                            <button
                                onClick={onEdit}
                                className="hidden sm:block px-3 py-1 text-sm bg-blue-600 text-white rounded hover:bg-blue-700 transition-colors"
                            >
                                Edit Notebook
                            </button>
                        )}
                        <button
                            onClick={onBack}
                            className="hidden sm:block px-3 py-1 text-sm border rounded hover:bg-gray-50 transition-colors"
                        >
                            Back to Project
                        </button>
                        {/* Mobile-only Back Icon */}
                        <button
                            onClick={onBack}
                            className="sm:hidden p-1.5 text-gray-500 hover:bg-gray-100 rounded-md"
                            title="Back to Project"
                        >
                            <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 19l-7-7m0 0l7-7m-7 7h18" />
                            </svg>
                        </button>
                        
                        {tourScreenId && <TourStartButton screenId={tourScreenId} inline />}
                    </div>
                </div>
            </div>
        </div>
    );
}

export function NotebookLayout({
    project,
    notebook,
    notebookTemplate,
    sidebar,
    content,
    onBack,
    canEdit,
    onEdit,
    className = '',
    tourScreenId,
    isMobileSidebarOpen = false,
    onMobileSidebarToggle,
    onMobileSidebarClose,
    headerCenter,
}: NotebookLayoutProps) {
    // Track sidebar size and collapse state so the main layout can stay in sync
    const [sidebarWidth, setSidebarWidth] = useState<number>(() => {
        const stored = localStorage.getItem('notebookSidebarWidth');
        return stored ? Number(stored) : 256;
    });

    const [sidebarCollapsed, setSidebarCollapsed] = useState<boolean>(() => {
        const stored = localStorage.getItem('notebookSidebarCollapsed');
        return stored ? stored === 'true' : false;
    });

    // Throttle width updates to animation frames for smoother resizing
    const widthRaf = React.useRef<number | null>(null);
    const handleWidthChange = React.useCallback((w: number) => {
        if (widthRaf.current) cancelAnimationFrame(widthRaf.current);
        widthRaf.current = requestAnimationFrame(() => {
            setSidebarWidth(w);
            localStorage.setItem('notebookSidebarWidth', w.toString());
        });
    }, []);

    // Detect mobile state for passing down to layout components
    const [isMobile, setIsMobile] = useState<boolean>(() => window.innerWidth < 768);
    
    React.useEffect(() => {
        const checkMobile = () => {
            const mobile = window.innerWidth < 768;
            setIsMobile(mobile);
        };
        
        checkMobile();
        window.addEventListener('resize', checkMobile);
        return () => window.removeEventListener('resize', checkMobile);
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
                  localStorage.setItem('notebookSidebarCollapsed', c.toString());
              },
              // Pass mobile props to sidebar container
              isMobile,
              onMobileClose: onMobileSidebarClose
          })
        : sidebar;

    React.useEffect(() => {
        return () => {
            if (widthRaf.current) cancelAnimationFrame(widthRaf.current);
        };
    }, []);

    return (
        <div className={`h-screen flex flex-col overflow-hidden ${className}`}>
            <NotebookHeader
                project={project}
                notebook={notebook}
                notebookTemplate={notebookTemplate}
                onEdit={onEdit}
                onBack={onBack}
                canEdit={canEdit}
                tourScreenId={tourScreenId}
                onMobileSidebarToggle={onMobileSidebarToggle}
                headerCenter={headerCenter}
                isMobile={isMobile}
            />

            <TwoColumnLayout
                leftColumn={enhancedSidebar}
                leftColumnWidth={sidebarWidth}
                isLeftCollapsed={sidebarCollapsed}
                // Mobile props
                isMobile={isMobile}
                isMobileSidebarOpen={isMobileSidebarOpen}
                onMobileSidebarClose={onMobileSidebarClose}
                rightColumn={
                    <div className="flex-1 p-2 md:p-4 flex flex-col min-h-0">
                        {content}
                    </div>
                }
            />
        </div>
    );
}
