# Conversation UI Test Coverage Audit

## Current State Analysis

### Test Files Present
1. **AssistantCell.test.tsx** - Tests read-only assistant messages
2. **CellList.test.tsx** - Tests message list container (heavily mocked)
3. **DraftUserCell.test.tsx** - Tests new message composition
4. **UserCell.test.tsx** - Tests user message display/editing
5. **LexicalRoundTrip.test.tsx** - Tests markdown/Lexical conversion
6. **LexicalSupportedFormats.test.tsx** - Tests specific markdown formats
7. **MessageDiff.test.tsx** - Tests diff display for edited messages
8. **LineBreakFidelity.test.tsx** - Tests blank line preservation

### Critical Gaps Identified

#### 1. **EditableAssistantCell** - NO TESTS!
This is why we missed the regression where it was using textarea instead of LexicalEditor.

**Needs testing:**
- Rich text editing with LexicalEditor
- Markdown formatting toolbar functionality
- Save/Cancel actions
- Keyboard shortcuts (Ctrl+Enter, Escape)
- Loading states
- Error handling
- Avatar display
- Integration with parent components

#### 2. **Integration Testing**
CellList.test.tsx uses heavy mocking, preventing detection of:
- Component prop mismatches
- Actual editing flow
- State management issues
- Child component regressions

#### 3. **Full Screen Editor**
No dedicated tests for:
- Full screen editing mode
- Toolbar button integration in full screen
- Mode switching (compose vs edit)
- Cancel/Save flow in different modes

#### 4. **Accessibility Testing**
Limited coverage of:
- Keyboard navigation
- Screen reader announcements
- ARIA labels and roles
- Focus management

#### 5. **Visual Regression Testing**
No tests for:
- Button positioning
- Toolbar layout
- Responsive design breakpoints
- CSS class applications

## Recommended Test Strategy

### 1. Create EditableAssistantCell.test.tsx
```typescript
// Key test cases:
- Renders with LexicalEditor (not textarea!)
- Initializes with provided content
- Shows toolbar with formatting options
- Handles save action (button click, Ctrl+Enter)
- Handles cancel action (button click, Escape)
- Shows loading state during save
- Displays error messages
- Syncs editor content changes
- Renders assistant avatar
```

### 2. Enhance Integration Tests
```typescript
// Remove heavy mocking from CellList tests
// Test actual component interactions:
- Edit flow: AssistantCell → EditableAssistantCell → AssistantCell
- State management during edits
- Error propagation
- Loading states across components
```

### 3. Add E2E-style Component Tests
```typescript
// Test complete user workflows:
- Click edit → type markdown → save → verify display
- Start edit → cancel → verify no changes
- Edit with formatting → save → verify formatted output
- Full screen edit flow
```

### 4. Visual/Layout Tests
```typescript
// Using Testing Library queries:
- Verify button positions (e.g., edited badge on left)
- Check toolbar button presence and order
- Validate responsive classes are applied
```

### 5. Accessibility Tests
```typescript
// Comprehensive a11y coverage:
- All interactive elements have proper labels
- Focus management during mode switches
- Keyboard-only operation
- Screen reader announcements for state changes
```

## Implementation Priority

1. **URGENT**: Create EditableAssistantCell.test.tsx
2. **HIGH**: Reduce mocking in CellList.test.tsx
3. **HIGH**: Add FullScreenEditor.test.tsx
4. **MEDIUM**: Enhance existing tests with a11y checks
5. **LOW**: Add visual regression tests

## Test Quality Metrics to Track

- **Component Coverage**: Every exported component has a test file
- **User Flow Coverage**: Each major user action has an integration test
- **Prop Coverage**: All component props are tested
- **State Coverage**: All state transitions are verified
- **Error Coverage**: All error states are tested
- **Accessibility Coverage**: All WCAG 2.1 Level AA criteria tested 