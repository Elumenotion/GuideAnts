import { useMemo, useEffect } from 'react';
import PreviewContainer from './PreviewContainer';

interface PdfViewerProps {
    blob: Blob;
    className?: string;
    /** When true, renders without PreviewContainer wrapper (for use in modals/overlays) */
    inlineMode?: boolean;
}

export default function PdfViewer({ blob, className = '', inlineMode = false }: PdfViewerProps) {
    const objectUrl = useMemo(() => URL.createObjectURL(blob), [blob]);

    useEffect(() => {
        return () => {
            URL.revokeObjectURL(objectUrl);
        };
    }, [objectUrl]);

    const embedElement = (
        <embed 
            src={objectUrl} 
            type="application/pdf" 
            title="PDF preview" 
            className="w-full h-full rounded"
        />
    );

    if (inlineMode) {
        return (
            <div className={`w-full h-full ${className}`.trim()}>
                {embedElement}
            </div>
        );
    }

    return (
        <PreviewContainer contentClassName={`flex flex-col ${className}`}>
            <div className="w-full flex-1 min-h-0">
                {embedElement}
            </div>
        </PreviewContainer>
    );
} 