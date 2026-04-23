import { useMemo, useEffect } from 'react';
import PreviewContainer from './PreviewContainer';

interface AudioPlayerProps {
    blob: Blob;
    className?: string;
    /** When true, renders without PreviewContainer wrapper (for use in modals/overlays) */
    inlineMode?: boolean;
}

export default function AudioPlayer({ blob, className = '', inlineMode = false }: AudioPlayerProps) {
    const objectUrl = useMemo(() => URL.createObjectURL(blob), [blob]);

    useEffect(() => {
        return () => {
            URL.revokeObjectURL(objectUrl);
        };
    }, [objectUrl]);

    const audioElement = (
        <audio 
            controls 
            src={objectUrl} 
            className="w-full max-w-2xl"
        >
            Your browser does not support the audio element.
        </audio>
    );

    if (inlineMode) {
        return (
            <div className={`w-full h-full flex items-center justify-center p-4 ${className}`.trim()}>
                {audioElement}
            </div>
        );
    }

    return (
        <PreviewContainer contentClassName={`flex items-center justify-center p-4 ${className}`.trim()}>
            {audioElement}
        </PreviewContainer>
    );
} 