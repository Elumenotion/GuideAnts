import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import JSZip from 'jszip';
import { describe, expect, it } from 'vitest';

const templatesDir = resolve(dirname(fileURLToPath(import.meta.url)), '../../assets/office-templates');

const loadTemplate = (fileName: string) => JSZip.loadAsync(readFileSync(resolve(templatesDir, fileName)));

describe('blank Office templates', () => {
  it.each([
    ['blank.docx', 'word/document.xml'],
    ['blank.xlsx', 'xl/workbook.xml'],
    ['blank.pptx', 'ppt/presentation.xml'],
  ])('%s is an OOXML package containing %s', async (fileName, mainPart) => {
    const zip = await loadTemplate(fileName);

    expect(zip.file('[Content_Types].xml')).not.toBeNull();
    expect(zip.file(mainPart)).not.toBeNull();
  });

  it('blank.pptx has exactly one slide', async () => {
    const zip = await loadTemplate('blank.pptx');

    expect(zip.file(/^ppt\/slides\/slide\d+\.xml$/)).toHaveLength(1);
  });

  it('blank.xlsx has a single worksheet named Sheet1', async () => {
    const zip = await loadTemplate('blank.xlsx');
    const workbook = await zip.file('xl/workbook.xml')!.async('string');

    expect(workbook).toContain('name="Sheet1"');
    expect(zip.file(/^xl\/worksheets\/sheet\d+\.xml$/)).toHaveLength(1);
  });

  it.each(['blank.docx', 'blank.xlsx', 'blank.pptx'])(
    '%s does not credit the generator library in its metadata',
    async (fileName) => {
      const zip = await loadTemplate(fileName);
      const core = await zip.file('docProps/core.xml')!.async('string');

      expect(core).not.toMatch(/python-docx|python-pptx|openpyxl/);
    }
  );
});
