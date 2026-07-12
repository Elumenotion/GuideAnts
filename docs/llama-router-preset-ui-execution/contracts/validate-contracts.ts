import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const contractsDir = dirname(fileURLToPath(import.meta.url));

interface CatalogGetResponse {
  schemaVersion: number;
  task: string;
  catalogVersion: string;
  models: unknown[];
}

interface ImmutableOperationInput {
  definitionId: string;
  definitionVersion: string;
  repository: string;
  resolvedRevision: string;
  modelFiles: string[];
  mmprojFiles: string[];
  routerModelId: string;
  runtimeProfileId: string;
  routerPreset: Record<string, string>;
}

const fixtureFiles = readdirSync(contractsDir)
  .filter((name) => name.endsWith('.fixture.json'))
  .sort();

const schemaFiles = readdirSync(contractsDir)
  .filter((name) => name.startsWith('schema.') && name.endsWith('.json'))
  .sort();

const failures: string[] = [];

for (const name of [...schemaFiles, ...fixtureFiles]) {
  const path = join(contractsDir, name);
  try {
    JSON.parse(readFileSync(path, 'utf8')) as unknown;
  } catch (error) {
    failures.push(`${name}: ${error instanceof Error ? error.message : String(error)}`);
  }
}

const catalog = JSON.parse(
  readFileSync(join(contractsDir, 'catalog-get-response.fixture.json'), 'utf8'),
) as CatalogGetResponse;
if (catalog.schemaVersion !== 1 || catalog.task !== 'llama') {
  failures.push('catalog-get-response.fixture.json: expected schemaVersion=1 task=llama');
}

const mtp = JSON.parse(
  readFileSync(join(contractsDir, 'immutable-operation-input.fixture.json'), 'utf8'),
) as ImmutableOperationInput;
if (mtp.definitionId.endsWith('-mtp')) {
  if (mtp.mmprojFiles.length === 0) {
    failures.push('immutable-operation-input.fixture.json: MTP vision rows require mmprojFiles');
  }
  if (!('image-min-tokens' in mtp.routerPreset)) {
    failures.push('immutable-operation-input.fixture.json: MTP vision rows require routerPreset.image-min-tokens');
  }
  if (mtp.routerPreset['spec-type'] !== 'draft-mtp') {
    failures.push('immutable-operation-input.fixture.json: MTP rows require routerPreset.spec-type=draft-mtp');
  }
}

if (failures.length > 0) {
  for (const failure of failures) {
    console.error(`FAIL ${failure}`);
  }
  process.exit(1);
}

console.log(
  `PASS parsed ${fixtureFiles.length} fixtures and ${schemaFiles.length} schema files under contracts/`,
);
