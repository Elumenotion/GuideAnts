import { getApiOrigin } from '../../config/apiConfig';
import { withAuthFetchInit } from '../../services/authService';

export type SandboxGuideScope = {
  projectId: string;
  guideId: string;
};

export type SandboxAdminCallResult = {
  status: 'ok' | 'error';
  endpoint: string;
  httpStatus?: number;
  message?: string;
  data?: unknown;
  content?: string;
};

function truncateMessage(value: string, maxLength = 1200): string {
  if (value.length <= maxLength) {
    return value;
  }

  return `${value.slice(0, maxLength)}...`;
}

export function buildGuideScopeQuery(scope: SandboxGuideScope): URLSearchParams {
  const query = new URLSearchParams();
  query.set('projectId', scope.projectId);
  query.set('guideId', scope.guideId);
  return query;
}

export async function callSandboxAdminEndpoint(
  method: 'GET' | 'PUT' | 'POST',
  endpointSegment: string,
  options?: { query?: URLSearchParams; body?: string; contentType?: string },
): Promise<SandboxAdminCallResult> {
  const url = new URL(`/api/system-guide/sandbox-admin/${endpointSegment}`, getApiOrigin());
  for (const [key, value] of options?.query ?? []) {
    url.searchParams.set(key, value);
  }

  const endpoint = `${url.pathname}${url.search}`;
  const headers = new Headers();

  if (options?.body !== undefined) {
    headers.set('Content-Type', options.contentType ?? 'application/json');
  }

  let response: Response;
  try {
    response = await fetch(
      url.toString(),
      withAuthFetchInit({
        method,
        headers,
        body: options?.body,
      }),
    );
  } catch (error) {
    const message = error instanceof Error
      ? error.message
      : 'Network error while calling sandbox admin endpoint.';

    return {
      status: 'error',
      endpoint,
      message: truncateMessage(message),
    };
  }

  const rawBody = await response.text();
  if (!response.ok) {
    const baseMessage = rawBody.trim() || response.statusText || 'Sandbox admin request failed.';
    return {
      status: 'error',
      endpoint,
      httpStatus: response.status,
      message: truncateMessage(baseMessage),
    };
  }

  if (response.status === 204 || rawBody.trim().length === 0) {
    return {
      status: 'ok',
      endpoint,
      httpStatus: response.status,
    };
  }

  const contentType = response.headers.get('Content-Type')?.toLowerCase() ?? '';
  if (contentType.includes('application/json')) {
    try {
      return {
        status: 'ok',
        endpoint,
        httpStatus: response.status,
        data: JSON.parse(rawBody),
      };
    } catch {
      return {
        status: 'ok',
        endpoint,
        httpStatus: response.status,
        content: rawBody,
      };
    }
  }

  return {
    status: 'ok',
    endpoint,
    httpStatus: response.status,
    content: rawBody,
  };
}

export async function sandboxAdminGetSetupStatus(scope: SandboxGuideScope): Promise<SandboxAdminCallResult> {
  return callSandboxAdminEndpoint('GET', 'setup-status', { query: buildGuideScopeQuery(scope) });
}

export async function sandboxAdminSetRequirements(
  scope: SandboxGuideScope,
  content: string,
): Promise<SandboxAdminCallResult> {
  return callSandboxAdminEndpoint('PUT', 'requirements', {
    query: buildGuideScopeQuery(scope),
    body: content,
    contentType: 'text/plain',
  });
}

export async function sandboxAdminSetInstallScripts(
  scope: SandboxGuideScope,
  content: string,
): Promise<SandboxAdminCallResult> {
  return callSandboxAdminEndpoint('PUT', 'install-scripts', {
    query: buildGuideScopeQuery(scope),
    body: content,
    contentType: 'application/json',
  });
}

export async function sandboxAdminSetAptPackages(content: string): Promise<SandboxAdminCallResult> {
  return callSandboxAdminEndpoint('PUT', 'apt-packages', {
    body: content,
    contentType: 'text/plain',
  });
}

export async function sandboxAdminGetAptPackages(): Promise<SandboxAdminCallResult> {
  return callSandboxAdminEndpoint('GET', 'apt-packages');
}

export async function sandboxAdminApply(scope: SandboxGuideScope): Promise<SandboxAdminCallResult> {
  return callSandboxAdminEndpoint('POST', 'apply', {
    query: buildGuideScopeQuery(scope),
    body: JSON.stringify({ targets: ['pip', 'installScripts'] }),
  });
}

export async function sandboxAdminApplyApt(): Promise<SandboxAdminCallResult> {
  return callSandboxAdminEndpoint('POST', 'apply', {
    body: JSON.stringify({ targets: ['apt'] }),
  });
}

export async function sandboxAdminGetApplyJob(jobId: string): Promise<SandboxAdminCallResult> {
  return callSandboxAdminEndpoint('GET', `apply/jobs/${encodeURIComponent(jobId)}`);
}
