import PreviewContainer from './PreviewContainer';

interface ImageViewerProps {
    src: string;
    alt: string;
    /** When true, renders without PreviewContainer wrapper (for use in modals/overlays) */
    inlineMode?: boolean;
    className?: string;
}

export const ImageViewer = ({ src, alt, inlineMode = false, className = '' }: ImageViewerProps) => {
    const imageElement = (
        <img 
            src={src} 
            alt={alt}
            className="max-w-full max-h-full object-contain"
        />
    );

    if (inlineMode) {
        return (
            <div className={`w-full h-full flex justify-center items-center ${className}`.trim()}>
                {imageElement}
            </div>
        );
    }

    return (
        <PreviewContainer contentClassName={`flex justify-center items-center bg-gray-100 dark:bg-gray-800 ${className}`}>
            {imageElement}
        </PreviewContainer>
    );
};

