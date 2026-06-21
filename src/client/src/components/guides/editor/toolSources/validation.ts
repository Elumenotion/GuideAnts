import { HTTP_METHODS } from './openApiToolSourceConstants';

export function validateOpenApiSpec(spec: string): string | null {
  try {
    const parsed = JSON.parse(spec);

    if (!parsed.openapi && !parsed.swagger) {
      return 'Missing OpenAPI version field. Spec must have either "openapi" (3.x) or "swagger" (2.x) property.';
    }

    if (!parsed.info || typeof parsed.info !== 'object') {
      return 'Missing required "info" object. OpenAPI specs must include title and version.';
    }

    if (!parsed.info.title) {
      return 'Missing required "info.title" field.';
    }

    const hasOpenApi3Server =
      parsed.servers && Array.isArray(parsed.servers) && parsed.servers.length > 0 && parsed.servers[0].url;
    const hasSwagger2Host = parsed.host;

    if (!hasOpenApi3Server && !hasSwagger2Host) {
      return 'Missing server URL. Add a "servers" array with a URL (OpenAPI 3.x) or "host" field (Swagger 2.x).';
    }

    if (parsed.paths && typeof parsed.paths === 'object') {
      const missingOperationIds: string[] = [];

      for (const [path, pathItem] of Object.entries(parsed.paths)) {
        if (typeof pathItem === 'object' && pathItem !== null) {
          for (const method of HTTP_METHODS) {
            const operation = (pathItem as Record<string, unknown>)[method];
            if (operation && typeof operation === 'object') {
              const op = operation as Record<string, unknown>;
              if (
                !op.operationId ||
                typeof op.operationId !== 'string' ||
                op.operationId.trim() === ''
              ) {
                missingOperationIds.push(`${method.toUpperCase()} ${path}`);
              }
            }
          }
        }
      }

      if (missingOperationIds.length > 0) {
        const operations = missingOperationIds.slice(0, 3).join(', ');
        const more =
          missingOperationIds.length > 3 ? ` and ${missingOperationIds.length - 3} more` : '';
        return `Missing operationId for: ${operations}${more}. Every operation must have a unique operationId.`;
      }
    }

    return null;
  } catch (error) {
    return error instanceof Error ? error.message : 'Invalid JSON format';
  }
}

export function isInvalidJson(spec: string): boolean {
  try {
    JSON.parse(spec);
    return false;
  } catch {
    return true;
  }
}

export function isCustomDescriptor(spec: string): boolean {
  try {
    const parsed = JSON.parse(spec);
    return parsed['x-guideants-custom-descriptor'] === true;
  } catch {
    return false;
  }
}
