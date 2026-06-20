import { describe, it, expect } from 'vitest';
import { deriveToolSourceStatus, buildToolSourceCardViewModel } from '../toolSourceCardViewModel';
import { CustomToolDto } from '../../../../../types/guides';

const validWebSpec = JSON.stringify({
  openapi: '3.0.0',
  info: { title: 'Test', version: '1.0.0' },
  servers: [{ url: 'https://api.example.com' }],
  paths: {
    '/ping': {
      get: { operationId: 'ping', responses: { '200': { description: 'OK' } } },
    },
  },
});

const clientSpec = JSON.stringify({
  openapi: '3.0.0',
  info: { title: 'Client', version: '1.0.0' },
  servers: [{ url: 'client://worm-commander-client' }],
  paths: {
    '/action': {
      post: { operationId: 'doThing', responses: { '200': { description: 'OK' } } },
    },
  },
});

describe('toolSourceCardViewModel', () => {
  it('prioritizes invalid-json over other statuses', () => {
    expect(deriveToolSourceStatus('{bad', null, false, false)).toBe('invalid-json');
  });

  it('prioritizes needs-attention over custom', () => {
    expect(deriveToolSourceStatus(validWebSpec, 'missing field', false, true)).toBe(
      'needs-attention'
    );
  });

  it('shows custom when flagged in descriptor', () => {
    const customSpec = JSON.stringify({
      ...JSON.parse(validWebSpec),
      'x-guideants-custom-descriptor': true,
    });
    expect(deriveToolSourceStatus(customSpec, null, false, true)).toBe('custom');
  });

  it('builds web API card view model', () => {
    const tool: CustomToolDto = {
      name: 'api.example.com',
      openApiSpec: validWebSpec,
      apiHost: 'api.example.com',
    };
    const vm = buildToolSourceCardViewModel(tool, [tool], 0);
    expect(vm.sourceKindLabel).toBe('Web API');
    expect(vm.connectorKeyLabel).toBe('API host');
    expect(vm.status).toBe('valid');
    expect(vm.operationCount).toBe(1);
  });

  it('builds client actions card with client bridge label', () => {
    const tool: CustomToolDto = {
      name: 'worm-commander-client',
      openApiSpec: clientSpec,
      apiHost: 'worm-commander-client',
    };
    const vm = buildToolSourceCardViewModel(tool, [tool], 0);
    expect(vm.sourceKindLabel).toBe('Client Actions');
    expect(vm.connectorKeyLabel).toBe('Client bridge');
    expect(vm.connectorKeyValue).toBe('worm-commander-client');
  });
});
