/**
 * Opt-in ConversationContext stub. Imported by test-utils so component tests get a
 * lightweight useConversation without mounting the real provider stack.
 *
 * Tests that need the real provider (e.g. useConversationActions) must call
 * vi.unmock('../contexts/ConversationContext') at the top of the file.
 */
import { vi } from 'vitest';
import { conversationContextStubModule } from './conversationContextStub';

vi.mock('../contexts/ConversationContext', () => conversationContextStubModule);
