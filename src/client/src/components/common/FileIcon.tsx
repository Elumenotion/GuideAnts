import {
    AiFillFile,
    AiFillFileImage,
    AiFillFilePdf,
    AiFillFileText,
    AiFillFileWord,
    AiFillFileExcel,
    AiFillFilePpt,
    AiFillFileZip,
    AiFillFileMarkdown,
    AiFillHtml5,
} from 'react-icons/ai';
import {
    DiJavascript1,
    DiPython,
    DiJava,
    DiCss3,
    DiDatabase,
    DiGit,
    DiDocker,
} from 'react-icons/di';
import {
    BsFiletypeJson,
    BsFiletypeXml,
    BsFiletypeSql,
    BsFiletypeScss,
    BsFiletypeJsx,
    BsFiletypeTsx,
    BsFiletypeYml,
    BsFileEarmarkCode,
    BsFileEarmarkMusic,
    BsFileEarmarkPlay,
    BsFileEarmarkRichtext,
} from 'react-icons/bs';

interface FileIconProps {
    contentType: string;
    className?: string;
    fileName?: string; // Added to check file extensions
}

export function FileIcon({ contentType, className = "w-4 h-4 mr-2", fileName = "" }: FileIconProps) {
    // Helper function to get file extension
    const getFileExtension = (filename: string): string => {
        return filename.split('.').pop()?.toLowerCase() || '';
    };

    // Get icon based on content type and file extension
    const getIcon = () => {
        const extension = getFileExtension(fileName);

        // Common document formats
        if (contentType.includes('msword') || extension === 'doc' || extension === 'docx') {
            return <AiFillFileWord className={`${className} text-blue-600`} />;
        }
        if (contentType.includes('spreadsheet') || extension === 'xls' || extension === 'xlsx') {
            return <AiFillFileExcel className={`${className} text-green-600`} />;
        }
        if (contentType.includes('presentation') || extension === 'ppt' || extension === 'pptx') {
            return <AiFillFilePpt className={`${className} text-orange-600`} />;
        }

        // Images
        if (contentType.startsWith('image/')) {
            return <AiFillFileImage className={`${className} text-blue-500`} />;
        }

        // PDFs
        if (contentType === 'application/pdf') {
            return <AiFillFilePdf className={`${className} text-red-500`} />;
        }

        // Audio
        if (contentType.startsWith('audio/')) {
            return <BsFileEarmarkMusic className={`${className} text-purple-500`} />;
        }

        // Video
        if (contentType.startsWith('video/')) {
            return <BsFileEarmarkPlay className={`${className} text-blue-500`} />;
        }

        // Archives
        if (contentType.includes('zip') || contentType.includes('compressed') || 
            ['zip', 'rar', '7z', 'tar', 'gz'].includes(extension)) {
            return <AiFillFileZip className={`${className} text-yellow-600`} />;
        }

        // Code files
        if (contentType.startsWith('text/') || contentType === 'application/json' || 
            ['js', 'ts', 'py', 'java', 'c', 'cpp', 'cs', 'php', 'rb'].includes(extension)) {
            
            // JavaScript/TypeScript
            if (extension === 'js' || contentType.includes('javascript')) {
                return <DiJavascript1 className={`${className} text-yellow-400`} />;
            }
            if (extension === 'jsx') {
                return <BsFiletypeJsx className={`${className} text-blue-400`} />;
            }
            if (extension === 'ts') {
                return <BsFileEarmarkCode className={`${className} text-blue-600`} />;
            }
            if (extension === 'tsx') {
                return <BsFiletypeTsx className={`${className} text-blue-600`} />;
            }

            // Python
            if (extension === 'py' || contentType.includes('python')) {
                return <DiPython className={`${className} text-blue-500`} />;
            }

            // Java
            if (extension === 'java') {
                return <DiJava className={`${className} text-red-500`} />;
            }

            // Web files
            if (extension === 'html' || contentType.includes('html')) {
                return <AiFillHtml5 className={`${className} text-orange-500`} />;
            }
            if (extension === 'css' || contentType.includes('css')) {
                return <DiCss3 className={`${className} text-blue-500`} />;
            }
            if (extension === 'scss' || extension === 'sass') {
                return <BsFiletypeScss className={`${className} text-pink-500`} />;
            }

            // Data files
            if (extension === 'json' || contentType.includes('json')) {
                return <BsFiletypeJson className={`${className} text-yellow-500`} />;
            }
            if (extension === 'xml' || contentType.includes('xml')) {
                return <BsFiletypeXml className={`${className} text-orange-400`} />;
            }
            if (extension === 'sql' || contentType.includes('sql')) {
                return <BsFiletypeSql className={`${className} text-blue-400`} />;
            }
            if (extension === 'yml' || extension === 'yaml') {
                return <BsFiletypeYml className={`${className} text-purple-400`} />;
            }

            // Config/DevOps files
            if (extension === 'gitignore' || extension === 'git') {
                return <DiGit className={`${className} text-red-600`} />;
            }
            if (extension === 'dockerfile' || fileName.toLowerCase() === 'dockerfile') {
                return <DiDocker className={`${className} text-blue-600`} />;
            }
        }

        // Markdown
        if (contentType === 'text/markdown' || extension === 'md') {
            return <AiFillFileMarkdown className={`${className} text-blue-500`} />;
        }

        // Rich Text
        if (contentType.includes('rtf') || extension === 'rtf') {
            return <BsFileEarmarkRichtext className={`${className} text-blue-500`} />;
        }

        // Database files
        if (['db', 'sqlite', 'mdb'].includes(extension)) {
            return <DiDatabase className={`${className} text-gray-600`} />;
        }

        // Text files
        if (contentType.startsWith('text/')) {
            return <AiFillFileText className={`${className} text-gray-500`} />;
        }

        // Default file icon
        return <AiFillFile className={`${className} text-gray-400`} />;
    };

    return getIcon();
} 