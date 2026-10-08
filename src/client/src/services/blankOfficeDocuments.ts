import blankDocxUrl from '../assets/office-templates/blank.docx?url';
import blankXlsxUrl from '../assets/office-templates/blank.xlsx?url';
import blankPptxUrl from '../assets/office-templates/blank.pptx?url';

export type BlankOfficeDocumentKind = 'docx' | 'xlsx' | 'pptx';

export interface BlankOfficeDocumentDefinition {
    kind: BlankOfficeDocumentKind;
    /** Context-menu label. */
    menuLabel: string;
    /** Lower-case noun used in messages, e.g. "Couldn't create Word document". */
    displayName: string;
    defaultFileName: string;
    contentType: string;
    templateUrl: string;
}

export const BLANK_OFFICE_DOCUMENTS: readonly BlankOfficeDocumentDefinition[] = [
    {
        kind: 'docx',
        menuLabel: 'New Word Document',
        displayName: 'Word document',
        defaultFileName: 'New Document.docx',
        contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
        templateUrl: blankDocxUrl,
    },
    {
        kind: 'xlsx',
        menuLabel: 'New Excel Spreadsheet',
        displayName: 'Excel spreadsheet',
        defaultFileName: 'New Spreadsheet.xlsx',
        contentType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
        templateUrl: blankXlsxUrl,
    },
    {
        kind: 'pptx',
        menuLabel: 'New PowerPoint Presentation',
        displayName: 'PowerPoint presentation',
        defaultFileName: 'New Presentation.pptx',
        contentType: 'application/vnd.openxmlformats-officedocument.presentationml.presentation',
        templateUrl: blankPptxUrl,
    },
];

export function getBlankOfficeDocument(kind: BlankOfficeDocumentKind): BlankOfficeDocumentDefinition {
    const definition = BLANK_OFFICE_DOCUMENTS.find((d) => d.kind === kind);
    if (!definition) {
        throw new Error(`Unknown Office document kind: ${kind}`);
    }
    return definition;
}

/** Same " (2)", " (3)" scheme as the markdown flow, matched case-insensitively. */
export function getUniqueFileName(proposed: string, existingNames: readonly string[]): string {
    const existing = new Set(existingNames.map((name) => name.toLowerCase()));
    if (!existing.has(proposed.toLowerCase())) return proposed;
    const dot = proposed.lastIndexOf('.');
    const base = dot > 0 ? proposed.substring(0, dot) : proposed;
    const ext = dot > 0 ? proposed.substring(dot) : '';
    let i = 2;
    let candidate = `${base} (${i})${ext}`;
    while (existing.has(candidate.toLowerCase())) {
        i += 1;
        candidate = `${base} (${i})${ext}`;
    }
    return candidate;
}

export async function createBlankOfficeFile(
    kind: BlankOfficeDocumentKind,
    existingNames: readonly string[],
): Promise<File> {
    const definition = getBlankOfficeDocument(kind);
    const response = await fetch(definition.templateUrl);
    if (!response.ok) {
        throw new Error(`Failed to load the blank ${definition.displayName} template (HTTP ${response.status}).`);
    }
    // The UI host answers unknown paths with index.html and a 200; never upload that as a .docx.
    const responseType = response.headers.get('content-type') ?? '';
    if (responseType.toLowerCase().startsWith('text/html')) {
        throw new Error(`The blank ${definition.displayName} template was served as HTML; the asset is missing from this build.`);
    }
    const blob = await response.blob();
    return new File([blob], getUniqueFileName(definition.defaultFileName, existingNames), {
        type: definition.contentType,
    });
}
