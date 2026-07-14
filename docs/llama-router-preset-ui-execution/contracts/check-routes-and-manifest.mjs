#!/usr/bin/env node
/** D8 route + D11 manifest collision check for Phase 0 frozen contracts. */
import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const contractsDir = dirname(fileURLToPath(import.meta.url));

const d8PublicRoutes = [
  'GET /api/settings/llama/catalog',
  'GET /api/settings/llama/catalog/{catalogId}/quants',
  'POST /api/settings/models:add',
  'GET /api/settings/llama/operations/{operationId}',
  'GET /api/settings/llama/installations/{modelId}',
  'POST /api/settings/llama/installations/{modelId}/change-quant',
  'POST /api/settings/llama/installations/{modelId}/repair',
  'POST /api/settings/llama/installations/{modelId}/customize',
  'POST /api/settings/llama/installations/{modelId}/adopt',
  'GET /api/settings/llama/router/entries',
  'PUT /api/settings/llama/router/entries/{alias}',
  'GET /api/settings/llama/migration/status',
  'GET /api/settings/llama/migration/issues',
];

const d8InternalRoutes = [
  'GET /admin/catalog',
  'GET /admin/catalog/{catalogId}/quants',
  'POST /downloads',
  'GET /downloads/{operationId}',
  'GET /router/entries',
  'POST /router/entries',
];

const existingPublicLlamaRoutes = [
  'GET /api/settings/llama/runtime/inventory',
  'POST /api/settings/llama/runtime/load',
  'POST /api/settings/llama/runtime/unload',
  'GET /api/settings/llama/runtime/status',
  'POST /api/settings/llama/downloads',
  'GET /api/settings/llama/downloads/{operationId}',
];

function normalize(route) {
  return route.replace(/\{[^}]+\}/g, '{}').toUpperCase();
}

function findCollisions(candidateRoutes, existingRoutes) {
  const existing = new Set(existingRoutes.map(normalize));
  return candidateRoutes.filter((route) => existing.has(normalize(route)));
}

const manifestPath = join(contractsDir, 'manifest.catalog.fixture.json');
const schemaPath = join(contractsDir, 'schema.llama.json');
const modelSchemaPath = join(contractsDir, '..', '..', 'native-ai-migration', 'catalog', 'schema.model.json');

const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
const failures = [];

if (manifest.schemaVersion !== 1) failures.push('manifest.catalog.fixture.json: schemaVersion must be 1');
if (manifest.task !== 'llama') failures.push('manifest.catalog.fixture.json: task must be llama');
if (!Array.isArray(manifest.models)) failures.push('manifest.catalog.fixture.json: models[] required');
if ('entries' in manifest) failures.push('manifest.catalog.fixture.json: D11 forbids entries[] root shape');

const modelSchema = JSON.parse(readFileSync(modelSchemaPath, 'utf8'));
if (!('entries' in modelSchema.properties) || 'models' in modelSchema.properties) {
  failures.push('schema.model.json: expected entries[] catalog shape remains distinct from llama models[] root');
}

const llamaSchema = JSON.parse(readFileSync(schemaPath, 'utf8'));
const llamaProps = llamaSchema.properties || {};
if (!('models' in llamaProps) || llamaProps.task?.const !== 'llama') {
  failures.push('schema.llama.json: must declare task=llama and models[] root');
}

const publicCollisions = findCollisions(d8PublicRoutes, existingPublicLlamaRoutes);
const internalCollisions = findCollisions(d8InternalRoutes, d8PublicRoutes);
if (publicCollisions.length > 0) {
  failures.push(`D8 public route collisions with existing llama routes: ${publicCollisions.join(', ')}`);
}
if (internalCollisions.length > 0) {
  failures.push(`D8 internal/public route collisions: ${internalCollisions.join(', ')}`);
}

const fixtureCount = readdirSync(contractsDir).filter((name) => name.endsWith('.fixture.json')).length;
if (fixtureCount < 20) {
  failures.push(`expected at least 20 fixture files, found ${fixtureCount}`);
}

if (failures.length > 0) {
  for (const failure of failures) {
    console.error(`FAIL ${failure}`);
  }
  process.exit(1);
}

console.log(
  `PASS D8/D11 collision check: ${d8PublicRoutes.length} public routes, ${d8InternalRoutes.length} internal routes, ${fixtureCount} fixtures, manifest uses models[] root`,
);
