# Lexical Markdown Formatting Loss Issues

This document catalogs all the formatting preservation issues discovered by our comprehensive test suite. These represent bugs in the Lexical markdown transformers that need to be fixed.

## Critical Issues (Data Loss)

### 1. Nested List Indentation Loss
**Status**: ❌ BROKEN  
**Impact**: HIGH - List structure completely lost

```markdown
# Input
- Level 1
  - Level 2 (2 spaces)
    - Level 3 (4 spaces)

# Output  
- Level 1
- Level 2
- Level 3
```

**Problem**: All list items are flattened to the same level, losing hierarchical structure.

### 2. Table Formatting Loss
**Status**: ❌ BROKEN  
**Impact**: HIGH - Table structure may be lost

```markdown
# Input
| Left | Center | Right |
|:-----|:------:|------:|
| A    | B      | C     |

# Output
TBD - needs testing
```

**Problem**: Table alignment, spacing, and possibly entire table structure lost.

### 3. Multiple Blank Lines Collapsed
**Status**: ❌ BROKEN  
**Impact**: MEDIUM - Intentional spacing lost

```markdown
# Input
Paragraph 1.


Paragraph 2 (with 3 blank lines above).

# Output
Paragraph 1.

Paragraph 2 (with 1 blank line above).
```

**Problem**: Multiple consecutive blank lines are collapsed to single blank lines.

### 4. Trailing Spaces Stripped
**Status**: ❌ BROKEN  
**Impact**: MEDIUM - Line breaks lost

```markdown
# Input
Line with trailing spaces  
Next line (should be on new line due to trailing spaces).

# Output
Line with trailing spaces
Next line (no line break).
```

**Problem**: Trailing spaces that create line breaks in markdown are stripped.

## Medium Priority Issues

### 5. Code Block Trailing Spaces
**Status**: ❌ BROKEN  
**Impact**: MEDIUM - Code formatting altered

```markdown
# Input
```
Code with trailing spaces    
```

# Output
```
Code with trailing spaces
```
```

**Problem**: Trailing spaces in code blocks are stripped.

### 6. Mixed Deep Nesting
**Status**: ❌ BROKEN  
**Impact**: MEDIUM - Complex structure lost

```markdown
# Input
1. Ordered
   - Unordered under ordered (3 spaces)
     1. Ordered under unordered (5 spaces)
        - Unordered level 4 (7 spaces)

# Output
1. Ordered
- Unordered under ordered
1. Ordered under unordered  
- Unordered level 4
```

**Problem**: Mixed list types with proper indentation are flattened.

### 7. Nested Blockquotes
**Status**: ❌ UNKNOWN - needs testing  
**Impact**: MEDIUM

```markdown
# Input
> Level 1 quote
> > Level 2 quote
> > > Level 3 quote
> Back to level 1

# Output
TBD - needs testing
```

### 8. Lists with Embedded Content
**Status**: ❌ UNKNOWN - needs testing  
**Impact**: MEDIUM

```markdown
# Input
1. List item with code block:
   ```javascript
   function test() { return true; }
   ```
2. Next item

# Output
TBD - needs testing
```

## Low Priority Issues (Cosmetic)

### 9. Whitespace Normalization
**Status**: ❌ BROKEN  
**Impact**: LOW - Minor formatting differences

- Extra spaces between words may be normalized
- Tab characters may be converted to spaces
- Inconsistent spacing around headers/lists

### 10. Unicode and Special Characters
**Status**: ✅ LIKELY OK - needs verification  
**Impact**: LOW

Emojis and unicode characters appear to be preserved correctly.

## Test Coverage Summary

| Feature Category | Tests Written | Status |
|------------------|---------------|--------|
| Text Formatting | ✅ Complete | ✅ PASSING |
| Headings | ✅ Complete | ✅ PASSING |
| Simple Lists | ✅ Complete | ✅ PASSING |
| Nested Lists | ✅ Complete | ❌ FAILING |
| Links | ✅ Complete | ✅ PASSING |
| Images | ✅ Complete | ✅ PASSING |
| Code Blocks | ✅ Complete | ❌ UNKNOWN |
| Blockquotes | ✅ Complete | ❌ UNKNOWN |
| Tables | ✅ Complete | ❌ UNKNOWN |
| Spacing/Breaks | ✅ Complete | ❌ FAILING |
| Mixed Content | ✅ Complete | ❌ UNKNOWN |

## Fixing Strategy

### Phase 1: Critical Fixes
1. **Fix nested list indentation** - This is the most visible issue
2. **Fix table preservation** - Tables are a core feature
3. **Fix trailing space handling** - Affects line breaks

### Phase 2: Medium Priority
4. Fix multiple blank line preservation
5. Fix code block whitespace preservation
6. Fix mixed nesting scenarios

### Phase 3: Polish
7. Improve whitespace normalization
8. Handle edge cases and special characters

## Implementation Notes

The issues appear to be in the Lexical markdown transformers:
- `$convertFromMarkdownString()` - May not be parsing nested structures correctly
- `$convertToMarkdownString()` - May not be serializing with proper indentation/spacing

**Root Cause**: The Lexical markdown transformers prioritize basic conversion over perfect preservation. They may need to be:
1. Enhanced with better indentation/spacing logic
2. Replaced with more accurate custom transformers
3. Supplemented with parallel markdown storage to avoid round-trip losses

## Running the Tests

```bash
cd client
npm test -- LexicalRoundTrip.test.tsx
```

The tests will fail and show detailed console output documenting exactly what formatting is lost for each scenario. 