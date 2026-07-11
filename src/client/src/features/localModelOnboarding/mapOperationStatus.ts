import type { AddModelErrorDto, LlamaOperationStatusDto, ModelDownloadOperationDto } from '../../types/settings';

export function mapLlamaOperationStatusToDownloadDto(
  operation: LlamaOperationStatusDto
): ModelDownloadOperationDto {
  return {
    operationId: operation.operationId,
    status: operation.status,
    routerModelId: operation.routerModelId,
    progress: operation.progress ?? undefined,
    errorMessage: operation.errorMessage ?? undefined,
    logLine: operation.logLine ?? undefined,
    error: operation.error ?? undefined,
  };
}

export function parseAddModelErrorFromUnknown(error: unknown): AddModelErrorDto | null {
  if (!error || typeof error !== 'object') {
    return null;
  }
  const body = (error as { body?: unknown }).body;
  if (!body || typeof body !== 'object') {
    return null;
  }
  const candidate = body as Partial<AddModelErrorDto>;
  if (typeof candidate.code !== 'string' || typeof candidate.step !== 'string' || typeof candidate.message !== 'string') {
    return null;
  }
  return {
    code: candidate.code,
    step: candidate.step,
    message: candidate.message,
    remediation: typeof candidate.remediation === 'string' ? candidate.remediation : undefined,
  };
}
