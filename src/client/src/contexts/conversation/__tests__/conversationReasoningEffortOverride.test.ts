import { describe, it, expect, beforeEach } from 'vitest';
import {
  getConversationReasoningEffortOverride,
  setConversationReasoningEffortOverride,
  clearConversationReasoningEffortOverride,
} from '../conversationReasoningEffortOverride';

describe('conversationReasoningEffortOverride', () => {
  beforeEach(() => {
    // Clear all entries between tests by setting null for known keys
    setConversationReasoningEffortOverride('test-convo-1', null);
    setConversationReasoningEffortOverride('test-convo-2', null);
  });

  it('returns null for an unknown conversation', () => {
    expect(getConversationReasoningEffortOverride('unknown')).toBeNull();
  });

  it('returns null for null/undefined conversationId', () => {
    expect(getConversationReasoningEffortOverride(null)).toBeNull();
    expect(getConversationReasoningEffortOverride(undefined)).toBeNull();
  });

  it('set then get returns the value', () => {
    setConversationReasoningEffortOverride('test-convo-1', 'High');
    expect(getConversationReasoningEffortOverride('test-convo-1')).toBe('High');
  });

  it('set(null) clears the override', () => {
    setConversationReasoningEffortOverride('test-convo-1', 'High');
    expect(getConversationReasoningEffortOverride('test-convo-1')).toBe('High');
    setConversationReasoningEffortOverride('test-convo-1', null);
    expect(getConversationReasoningEffortOverride('test-convo-1')).toBeNull();
  });

  it('two conversations are independent', () => {
    setConversationReasoningEffortOverride('test-convo-1', 'High');
    setConversationReasoningEffortOverride('test-convo-2', 'Low');
    expect(getConversationReasoningEffortOverride('test-convo-1')).toBe('High');
    expect(getConversationReasoningEffortOverride('test-convo-2')).toBe('Low');
  });

  it('set with null conversationId is a no-op', () => {
    expect(() => setConversationReasoningEffortOverride(null, 'High')).not.toThrow();
    expect(() => setConversationReasoningEffortOverride(undefined, 'High')).not.toThrow();
  });

  it('clear removes the entry (owned by the keyed ConversationProvider unmount)', () => {
    setConversationReasoningEffortOverride('test-convo-1', 'High');
    expect(getConversationReasoningEffortOverride('test-convo-1')).toBe('High');
    clearConversationReasoningEffortOverride('test-convo-1');
    expect(getConversationReasoningEffortOverride('test-convo-1')).toBeNull();
  });

  it('clear is a no-op for null conversationId', () => {
    expect(() => clearConversationReasoningEffortOverride(null)).not.toThrow();
    expect(() => clearConversationReasoningEffortOverride(undefined)).not.toThrow();
  });
});
