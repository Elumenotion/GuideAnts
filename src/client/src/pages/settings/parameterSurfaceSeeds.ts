export interface NonLocalParameterSurface {
  samplingParametersJson: string;
  reasoningChoicesJson: string;
}

export const EMPTY_PARAMETER_SURFACE: NonLocalParameterSurface = {
  samplingParametersJson: '{}',
  reasoningChoicesJson: '',
};

const OPENAI_CHAT_SAMPLING = JSON.stringify({
  temperature: {
    key: 'temperature',
    label: 'Temperature',
    description: 'Controls randomness: lower is focused, higher is creative',
    min: 0,
    max: 2,
    step: 0.1,
    default: 1,
    displayOrder: 0,
    exposedInGuideBuilder: true,
  },
  top_p: {
    key: 'top_p',
    label: 'Top P',
    description: 'Nucleus sampling: controls diversity of token selection',
    min: 0,
    max: 1,
    step: 0.05,
    default: 1,
    displayOrder: 1,
    exposedInGuideBuilder: true,
  },
});

const ANTHROPIC_SAMPLING = JSON.stringify({
  temperature: {
    key: 'temperature',
    label: 'Temperature',
    description: 'Controls randomness: lower is focused, higher is creative',
    min: 0,
    max: 1,
    step: 0.05,
    default: 1,
    displayOrder: 0,
    exposedInGuideBuilder: true,
  },
  top_p: {
    key: 'top_p',
    label: 'Top P',
    description: 'Nucleus sampling: controls diversity of token selection',
    min: 0,
    max: 1,
    step: 0.05,
    default: 1,
    displayOrder: 1,
    exposedInGuideBuilder: true,
  },
});

const GEMINI_PRO_SAMPLING = JSON.stringify({
  temperature: {
    key: 'temperature',
    label: 'Temperature',
    description: 'Controls randomness: lower is focused, higher is creative',
    min: 0,
    max: 2,
    step: 0.1,
    default: 1,
    displayOrder: 0,
    exposedInGuideBuilder: true,
  },
  top_p: {
    key: 'top_p',
    label: 'Top P',
    description: 'Nucleus sampling: controls diversity of token selection',
    min: 0,
    max: 1,
    step: 0.05,
    default: 0.95,
    displayOrder: 1,
    exposedInGuideBuilder: true,
  },
});

const OPENAI_RESPONSES_REASONING = JSON.stringify(['none', 'low', 'medium', 'high', 'xhigh']);
const ANTHROPIC_REASONING = JSON.stringify(['minimal', 'low', 'medium', 'high']);
const GEMINI_REASONING = JSON.stringify(['low', 'medium', 'high']);

export const PARAMETER_SURFACE_SEEDS: Record<string, NonLocalParameterSurface> = {
  openai_chat_standard: {
    samplingParametersJson: OPENAI_CHAT_SAMPLING,
    reasoningChoicesJson: '',
  },
  openai_responses_reasoning: {
    samplingParametersJson: '{}',
    reasoningChoicesJson: OPENAI_RESPONSES_REASONING,
  },
  anthropic_standard: {
    samplingParametersJson: ANTHROPIC_SAMPLING,
    reasoningChoicesJson: ANTHROPIC_REASONING,
  },
  huggingface_chat_standard: {
    samplingParametersJson: OPENAI_CHAT_SAMPLING,
    reasoningChoicesJson: '',
  },
  google_gemini_25_pro: {
    samplingParametersJson: GEMINI_PRO_SAMPLING,
    reasoningChoicesJson: GEMINI_REASONING,
  },
  google_gemini_25_flash: {
    samplingParametersJson: GEMINI_PRO_SAMPLING,
    reasoningChoicesJson: GEMINI_REASONING,
  },
};

export function resolveParameterSurfaceSeed(seedKey?: string | null): NonLocalParameterSurface {
  const key = seedKey?.trim();
  if (!key) {
    return { ...EMPTY_PARAMETER_SURFACE };
  }
  const seed = PARAMETER_SURFACE_SEEDS[key];
  return seed ? { ...seed } : { ...EMPTY_PARAMETER_SURFACE };
}
