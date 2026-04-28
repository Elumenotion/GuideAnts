import type { ModelDto } from '../../types/guides';

function parseReasoningChoices(json?: string): string[] {
  if (!json) {
    return [];
  }

  try {
    const parsed = JSON.parse(json);
    if (!Array.isArray(parsed)) {
      return [];
    }

    return parsed
      .filter((value): value is string => typeof value === 'string')
      .map((value) => value.trim())
      .filter((value) => value.length > 0);
  } catch {
    return [];
  }
}

export function getReasoningChoicesForModel(model?: ModelDto): string[] {
  if (model?.reasoningChoices && model.reasoningChoices.length > 0) {
    return model.reasoningChoices
      .map((choice) => choice.trim())
      .filter((choice) => choice.length > 0);
  }

  return parseReasoningChoices(model?.reasoningChoicesJson);
}

export function normalizeReasoningEffortForModel(
  model: ModelDto | undefined,
  current?: string | null
): string | undefined {
  const choices = getReasoningChoicesForModel(model);
  if (choices.length === 0) {
    return undefined;
  }

  const requested = current?.trim();
  if (!requested) {
    const defaultChoice = model?.defaultReasoningChoice?.trim();
    if (defaultChoice) {
      const matchedDefault = choices.find(
        (choice) => choice.toLowerCase() === defaultChoice.toLowerCase()
      );
      if (matchedDefault) {
        return matchedDefault;
      }
    }

    return choices[0];
  }

  const matchedChoice = choices.find(
    (choice) => choice.toLowerCase() === requested.toLowerCase()
  );

  return matchedChoice ?? choices[0];
}

export function modelDeclaresSamplingParameter(model: ModelDto | undefined, key: string): boolean {
  return (model?.samplingParameterPolicy ?? []).some(
    (param) => param.key.toLowerCase() === key.toLowerCase()
  );
}

export function normalizeSamplingValueForModel(
  model: ModelDto | undefined,
  key: string,
  current?: number | null
): number | null {
  const param = (model?.samplingParameterPolicy ?? []).find(
    (candidate) => candidate.key.toLowerCase() === key.toLowerCase()
  );
  if (!param) {
    return null;
  }

  return typeof current === 'number' && !Number.isNaN(current)
    ? current
    : param.recommendedDefault;
}
