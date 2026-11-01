import { describe, expect, it } from 'vitest';
import {
  MODEL_OPTION_LABEL_MAX,
  modelOptionLabel,
} from '../modelOptionLabel';

describe('modelOptionLabel', () => {
  it('returns the display name alone when there is no description', () => {
    expect(modelOptionLabel('qwen3.8-27b')).toEqual({
      label: 'qwen3.8-27b',
      title: 'qwen3.8-27b',
    });
    expect(modelOptionLabel('qwen3.8-27b', null)).toEqual({
      label: 'qwen3.8-27b',
      title: 'qwen3.8-27b',
    });
  });

  it('joins a short description with the default separator, untruncated', () => {
    expect(modelOptionLabel('qwen3.8-27b-local', 'Dense Qwen 3.8 vision model.')).toEqual({
      label: 'qwen3.8-27b-local — Dense Qwen 3.8 vision model.',
      title: 'qwen3.8-27b-local — Dense Qwen 3.8 vision model.',
    });
  });

  it('uses the separator the caller provides', () => {
    expect(modelOptionLabel('a', 'b', ' - ').label).toBe('a - b');
  });

  it('truncates a long label to the cap with a trailing ellipsis', () => {
    const description =
      'Same MAX weights (Qwen3.8-27B-GGUF) served by the Max AI stack (http://192.168.0.111:8112). Use this row to run the model on Max; the -local row targets the local stack.';
    const { label, title } = modelOptionLabel('qwen3.8-27b-max', description);
    expect(label.length).toBe(MODEL_OPTION_LABEL_MAX);
    expect(label.endsWith('…')).toBe(true);
    expect(label.startsWith('qwen3.8-27b-max — ')).toBe(true);
    expect(title).toBe(`qwen3.8-27b-max — ${description}`);
  });

  it('keeps the label at or under the cap even for an oversized display name', () => {
    const { label, title } = modelOptionLabel('x'.repeat(200), 'y'.repeat(200));
    expect(label.length).toBeLessThanOrEqual(MODEL_OPTION_LABEL_MAX);
    expect(title.length).toBeGreaterThan(MODEL_OPTION_LABEL_MAX);
  });
});
