// No React import needed
import PreviewContainer from './PreviewContainer';

interface TextViewerProps {
    /** Text content to display */
    text: string;
    /** Optional TailwindCSS classes applied to the wrapping <pre> */
    className?: string;
    /** When true, renders without PreviewContainer wrapper (for use in modals/overlays) */
    inlineMode?: boolean;
    /** Hide the PreviewContainer header when inlineMode is false */
    hidePreviewHeader?: boolean;
}

/**
 * Displays plain text / JSON content inside a scrollable <pre> block.
 */
export default function TextViewer({ text, className = '', inlineMode = false, hidePreviewHeader = false }: TextViewerProps) {
    const preElement = (
        <pre
            className={`whitespace-pre-wrap break-words bg-gray-50 p-4 rounded select-text w-full h-full overflow-auto ${className}`.trim()}
        >
            {text}
        </pre>
    );

    if (inlineMode) {
        return (
            <div className="w-full h-full">
                {preElement}
            </div>
        );
    }

    return (
        <PreviewContainer hideHeader={hidePreviewHeader}>
            <pre
                className={`whitespace-pre-wrap break-words bg-gray-50 p-4 rounded select-text ${className}`.trim()}
            >
                {text}
            </pre>
        </PreviewContainer>
    );
} 