import { useMemo, useEffect } from 'react';
import PreviewContainer from './PreviewContainer';

interface VideoPlayerProps {
    blob: Blob;
    className?: string;
    /** When true, renders without PreviewContainer wrapper (for use in modals/overlays) */
    inlineMode?: boolean;
}

export default function VideoPlayer({ blob, className = '', inlineMode = false }: VideoPlayerProps) {
    const objectUrl = useMemo(() => URL.createObjectURL(blob), [blob]);

    useEffect(() => {
        return () => {
            URL.revokeObjectURL(objectUrl);
        };
    }, [objectUrl]);

    const videoElement = (
        <video controls src={objectUrl} className="max-w-full max-h-full rounded">
            Your browser does not support the video tag.
        </video>
    );

    if (inlineMode) {
        return (
            <div className={`w-full h-full flex items-center justify-center p-4 ${className}`.trim()}>
                {videoElement}
            </div>
        );
    }

    return (
        <PreviewContainer contentClassName={`flex items-center justify-center p-4 ${className}`.trim()}>
            {videoElement}
        </PreviewContainer>
    );
} 