import React, { useState, useCallback, useEffect } from 'react';

interface SidebarContainerProps {
    children: React.ReactNode;
    defaultWidth?: number;
    minWidth?: number;
    maxWidth?: number;
    disabled?: boolean;
    className?: string;
    onWidthChange?: (width: number) => void;
    onCollapseChange?: (isCollapsed: boolean) => void;
    initialCollapsed?: boolean;
    'data-tour-id'?: string;
    // Mobile specific props
    isMobile?: boolean;
    onMobileClose?: () => void;
}

export function SidebarContainer({
    children,
    defaultWidth = 256,
    minWidth = 200,
    maxWidth = 600,
    disabled = false,
    className = '',
    onWidthChange,
    onCollapseChange,
    initialCollapsed = false,
    'data-tour-id': dataTourId,
    isMobile = false,
    onMobileClose,
}: SidebarContainerProps) {
    const [isCollapsed, setIsCollapsed] = useState(initialCollapsed);
    const [width, setWidth] = useState(defaultWidth);
    const [isDragging, setIsDragging] = useState(false);

    const handleMouseDown = useCallback((e: React.MouseEvent) => {
        if (disabled || isMobile) return; // Disable resize on mobile
        e.preventDefault();
        setIsDragging(true);
    }, [disabled, isMobile]);

    const handleMouseUp = useCallback(() => {
        setIsDragging(false);
    }, []);

    const handleMouseMove = useCallback((e: MouseEvent) => {
        if (isDragging && !disabled && !isMobile) {
            const newWidth = Math.min(Math.max(e.clientX, minWidth), maxWidth);
            setWidth(newWidth);
            onWidthChange?.(newWidth);
        }
    }, [isDragging, disabled, isMobile, minWidth, maxWidth, onWidthChange]);

    useEffect(() => {
        if (isDragging) {
            document.addEventListener('mousemove', handleMouseMove);
            document.addEventListener('mouseup', handleMouseUp);
        }

        return () => {
            document.removeEventListener('mousemove', handleMouseMove);
            document.removeEventListener('mouseup', handleMouseUp);
        };
    }, [isDragging, handleMouseMove, handleMouseUp]);

    const handleToggleCollapse = useCallback(() => {
        if (disabled) return;
        const newCollapsed = !isCollapsed;
        setIsCollapsed(newCollapsed);
        onCollapseChange?.(newCollapsed);
    }, [isCollapsed, disabled, onCollapseChange]);

    return (
        <div 
            className={`relative flex flex-shrink-0 min-h-0 h-full ${className}`}
            style={{ 
                // On mobile, width is handled by parent TwoColumnLayout/CSS
                width: !isMobile ? (isCollapsed ? '40px' : `${width}px`) : '100%',
                transition: isDragging ? 'none' : 'width 0.3s ease',
                opacity: disabled ? 0.6 : 1,
                pointerEvents: disabled ? 'none' : 'auto'
            }}
            data-tour-id={dataTourId}
        >
            <div className="flex-1 overflow-hidden min-h-0 h-full flex flex-col">
                {/* Mobile Close Button Header */}
                {isMobile && (
                    <div className="flex items-center justify-between p-3 border-b bg-gray-50 md:hidden shrink-0">
                        <h2 className="text-sm font-semibold text-gray-700">Menu</h2>
                        <button
                            onClick={onMobileClose}
                            className="p-2 rounded-md hover:bg-gray-200 text-gray-500"
                            aria-label="Close sidebar"
                        >
                            <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                            </svg>
                        </button>
                    </div>
                )}

                <div className={`flex-1 h-full transition-transform duration-300 ${
                    !isMobile && isCollapsed ? '-translate-x-full' : 'translate-x-0'
                }`}>
                    {React.Children.map(children, child => {
                        if (React.isValidElement(child)) {
                            return React.cloneElement(child as React.ReactElement<any>, { isCollapsed: !isMobile && isCollapsed });
                        }
                        return child;
                    })}
                </div>
            </div>

            {/* Desktop Collapse/Expand Button - Hidden on Mobile */}
            {!isMobile && (
                <button
                    onClick={handleToggleCollapse}
                    className={`absolute top-2 z-20 w-6 h-6 bg-gray-200 rounded-full flex items-center justify-center hover:bg-gray-300 focus:outline-none transition-colors disabled:opacity-50 disabled:cursor-not-allowed ${isCollapsed ? 'right-2' : '-mr-3 right-0'}`}
                    title={isCollapsed ? "Expand sidebar" : "Collapse sidebar"}
                    disabled={disabled}
                >
                    <span className="transform transition-transform text-xs">
                        {isCollapsed ? '→' : '←'}
                    </span>
                </button>
            )}

            {/* Desktop Resize Handle - Hidden on Mobile */}
            {!isCollapsed && !disabled && !isMobile && (
                <div
                    className="absolute right-0 top-0 w-1 h-full cursor-col-resize bg-transparent hover:bg-gray-300 transition-colors"
                    onMouseDown={handleMouseDown}
                />
            )}
        </div>
    );
}
