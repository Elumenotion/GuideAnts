import React from 'react';

interface TwoColumnLayoutProps {
    leftColumn: React.ReactNode;
    rightColumn: React.ReactNode;
    leftColumnWidth?: number;
    isLeftCollapsed?: boolean;
    onResize?: (width: number) => void;
    minWidth?: number;
    maxWidth?: number;
    className?: string;
    // Mobile specific props
    isMobile?: boolean;
    isMobileSidebarOpen?: boolean;
    onMobileSidebarClose?: () => void;
}

export function TwoColumnLayout({
    leftColumn,
    rightColumn,
    leftColumnWidth = 256,
    isLeftCollapsed = false,
    onResize: _onResize,
    minWidth: _minWidth = 200,
    maxWidth: _maxWidth = 600,
    className = '',
    isMobile = false,
    isMobileSidebarOpen = false,
    onMobileSidebarClose,
}: TwoColumnLayoutProps) {
    return (
        <div className={`flex flex-1 min-h-0 ${className}`}>
            {/* Mobile Backdrop */}
            {isMobile && isMobileSidebarOpen && (
                <div 
                    className="fixed inset-0 bg-black bg-opacity-50 z-40 md:hidden"
                    onClick={onMobileSidebarClose}
                    aria-hidden="true"
                />
            )}

            {/* Left Column */}
            <div 
                className={`
                    ${isMobile ? 'fixed inset-y-0 left-0 z-50 shadow-xl' : 'flex-shrink-0 min-h-0'} 
                    ${isMobile && !isMobileSidebarOpen ? '-translate-x-full' : ''}
                    ${isMobile ? 'transition-transform duration-300 ease-in-out' : 'transition-all duration-300'}
                `}
                style={!isMobile ? { 
                    width: isLeftCollapsed ? '40px' : `${leftColumnWidth}px`,
                    overflow: isLeftCollapsed ? 'hidden' : 'visible'
                } : {
                    width: '280px', // Fixed width for mobile drawer
                }}
            >
                {leftColumn}
            </div>

            {/* Right Column */}
            <div className="flex-1 flex flex-col min-w-0 min-h-0">
                {rightColumn}
            </div>
        </div>
    );
}
