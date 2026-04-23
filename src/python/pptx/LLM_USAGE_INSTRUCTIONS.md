# Presentation API - LLM Usage Instructions

## Overview
The Presentation API provides a suite of tools for creating, updating, and managing presentations stored in structured data formats. Presentations are persisted as JSON files, and output (Markdown, PPTX, HTML) is generated automatically after every edit.  
**All functions are available as tools: call them directly–NO Python code required.**

---

## Core Concepts

1. **Presentation Storage:** Presentations are kept as JSON files, each with a unique UUID and a set of slides.
2. **Automatic Output Generation:** After every edit, the system automatically regenerates .md, .pptx, and .html files. You can also generate them on demand.
3. **Persistent File Paths:** Once a presentation is linked to output files (via generation or import), those exact paths are stored and reused consistently.
4. **Relative Image Paths:** When using images, make sure the paths are relative to the current working directory for correct resolution.
5. **Automatic Layout:** The system detects the appropriate slide layout depending on your supplied markdown content.
6. **Always Use Presentation Names in User Communication:** Users should be addressed using the presentation's name, never any system ID.
7. **Idempotent Creation:** `create_presentation` returns the existing presentation if a matching name exists, rather than creating duplicates.

---

## File Import and Update Precedence Policy

Whenever a user provides a file (such as a .md, .pptx, or similar), and instructs that this file should be used to create, update, import, or overwrite a presentation, THE FILE IS THE SINGLE AUTHORITATIVE SOURCE for that presentation.

- **Full Overwrite:** Discard the previous contents of the specified presentation and replace with the content and structure of the file unless the user explicitly says otherwise (such as, “merge,” “append,” or “partial update”).
- **No "Corrections" or Adjustments:** Do not add, remove, modify, reinterpret, or "improve" the user’s file content except as the user specifies.
- **Never Reference Prior State:** Ignore any previously stored content, memory, or logic for the affected presentation except by the explicit direction of the user.
- **Always Confirm:** After using a file as a presentation source, inform the user that the presentation now matches the given file exactly and ask if further action is needed.
- **If Ambiguity:** If user instruction is not clear (e.g., if they say "update" a presentation with a file and it's not clear whether a merge or full replacement is intended), ask for explicit confirmation before proceeding.

---

## Available Tools

### Configuration

#### `set_auto_generate`
Enable or disable automatic file generation after edits.

**Parameters:**
- `enabled` (boolean, required): True to auto-generate .md, .pptx, and .html after every edit; False to disable

**Default:** Enabled

**Usage:**
- When enabled (default), every slide edit automatically regenerates all output files.
- Disable temporarily when making many edits to avoid repeated regeneration.
- Re-enable when done to generate final outputs.

**Example:**
```
set_auto_generate(enabled=False)  # Disable during bulk edits
# ... make many edits ...
set_auto_generate(enabled=True)   # Re-enable
```

---

### Presentation Management

#### `create_presentation`
Create a new presentation, or return the existing one if the name already exists.

**Parameters:**
- `name` (string, required): Name of the new presentation
- `initial_content` (string, optional): Initial markdown content (only used if creating new)

**Returns:** Tuple of (presentation_id, presentation_dict)

**Behavior:**
- If a presentation with the given name already exists, returns that presentation (idempotent).
- If creating new, `initial_content` is parsed into slides.
- Auto-generates output files (.md, .pptx, .html) after creation.

**Example:**
```
create_presentation(name="Quarterly Review")
```

---

#### `list_presentations`
List all available presentations.

**Returns:** List of all presentations with `id`, `name`, `updated_at` fields

---

#### `delete_presentation`
Delete a specific presentation.

**Parameters:**
- `presentation_id` (string, required): Presentation UUID

---

#### `copy_presentation`
Create a copy of an existing presentation.

**Parameters:**
- `presentation_id` (string, required): The UUID of the existing presentation
- `new_name` (string, required): Name for the copy

**Returns:** The new presentation UUID

---

### Slide Operations

#### `add_slide`
Add a new slide to a given presentation.

**Parameters:**
- `presentation_id` (string, required)
- `title` (string, optional): Text for primary slide heading (H1)
- `subtitle` (string, optional): Text for secondary heading (H2)
- `content` (string, optional): Body content in markdown
- `position` (integer, optional): Placement in the slide order (default: end of deck)

**Content Examples:**
```markdown
# Title slide
add_slide(presentation_id="abc-123", title="Welcome")

# Slide with bullets
add_slide(presentation_id="abc-123", title="Features", content="- Feature 1\n- Feature 2\n  - Feature 2a")

# Table slide
add_slide(presentation_id="abc-123", title="Summary Table", content="| Q1 | Q2 |\n|----|----|\n|100 |110 |")

# Code or image slide
add_slide(presentation_id="abc-123", title="Sample Code", content="```python\ndef hello():\n  print('Hello!')\n```")
add_slide(presentation_id="abc-123", title="Project Logo", content="![Logo](./images/logo.png)")
```

---

#### `update_slide`
Update the content or headers of an existing slide. Only fields provided are changed.

**Parameters:**
- `presentation_id` (string, required)
- `slide_index` (integer, required)
- `title` (string, optional)
- `subtitle` (string, optional)
- `content` (string, optional)

---

#### `delete_slide`
Remove a slide from the presentation.

**Parameters:**
- `presentation_id` (string, required)
- `slide_index` (integer, required)

---

#### `get_slide`
Retrieve slide content and metadata for inspection or editing.

**Parameters:**
- `presentation_id` (string, required)
- `slide_index` (integer, required)

**Returns:** Complete slide data including `title`, `subtitle`, `content`, raw markdown, and IDs

---

#### `get_all_slides`
Retrieve all slides in a presentation as a list.

**Parameters:**
- `presentation_id` (string, required)

---

#### `reorder_slides`
Change the order of slides in a presentation.

**Parameters:**
- `presentation_id` (string, required)
- `new_order` (list of integers, required): Desired indices order

**Example:**
```
reorder_slides(presentation_id="abc-123", new_order=[2, 1, 0])
```

---

#### `duplicate_slide`
Duplicate a specific slide (inserted after original).

**Parameters:**
- `presentation_id` (string, required)
- `slide_index` (integer, required)

---

### Content Helpers

#### `append_to_slide`
Add markdown content to the end of an existing slide.

**Parameters:**
- `presentation_id` (string, required)
- `slide_index` (integer, required)
- `markdown_content` (string, required)

---

#### `prepend_to_slide`
Add markdown content to the beginning of an existing slide.

**Parameters:**
- `presentation_id` (string, required)
- `slide_index` (integer, required)
- `markdown_content` (string, required)

---

### Export & Generation

#### `generate_markdown`
Generate a .md file from the given presentation.

**Parameters:**
- `presentation_id` (string, required)
- `output_file` (string, optional): Path for output file. If omitted, uses stored path or derives from presentation name.

**Returns:** Path to the generated file (string)

**Behavior:**
- If `output_file` is provided, that path is stored for future regenerations.
- If omitted, uses the previously stored path, or derives `{presentation_name}.md` from the presentation name.

**Example:**
```
generate_markdown(presentation_id="abc-123", output_file="output.md")
generate_markdown(presentation_id="abc-123")  # Uses stored/derived path
```

---

#### `generate_pptx`
Export a PPTX file for the presentation.

**Parameters:**
- `presentation_id` (string, required)
- `output_file` (string, optional): Path for output file. If omitted, uses stored path or derives from presentation name.
- `theme_file` (string, optional): Choose a theme

**Returns:** Path to the generated file (string)

**Behavior:**
- If `output_file` is provided, that path is stored for future regenerations.
- If omitted, uses the previously stored path, or derives `{presentation_name}.pptx`.

**Example:**
```
generate_pptx(presentation_id="abc-123", output_file="output.pptx", theme_file="pptx_theme_blue.json")
generate_pptx(presentation_id="abc-123")  # Uses stored/derived path
```

---

#### `generate_html`
Export a standalone HTML file with base64-encoded images.

**Parameters:**
- `presentation_id` (string, required)
- `output_file` (string, optional): Path for output file. If omitted, uses stored path or derives from presentation name.
- `theme_file` (string, optional)

**Returns:** Path to the generated file (string)

**Behavior:**
- If `output_file` is provided, that path is stored for future regenerations.
- If omitted, uses the previously stored path, or derives `{presentation_name}.html`.
- Images embedded as base64 URIs – HTML files are fully portable.
- HTML layout matches the PPTX output, including themes.

**Example:**
```
generate_html(presentation_id="abc-123", output_file="output.html", theme_file="pptx_theme.json")
generate_html(presentation_id="abc-123")  # Uses stored/derived path
```

---

#### `import_from_markdown`
Import a markdown file as a new presentation.

**Parameters:**
- `markdown_file` (string, required): Path to .md file
- `presentation_name` (string, optional): Name for presentation; defaults to filename

**Returns:** UUID of the new presentation

**Behavior:**
- The imported file path is stored as the presentation's markdown file path.
- Sibling paths are automatically derived (e.g., `mydeck.md` → `mydeck.pptx`, `mydeck.html`).
- Future regenerations will update these same files consistently.

**IMPORTANT:**  
When using this or any import method based on a user file, always follow the File Import and Update Precedence Policy above.

---

### Utility Functions

#### `get_slide_count`
Returns the slide count for a given presentation.

**Parameters:**
- `presentation_id` (string, required)

---

#### `search_slides`
Find all slides containing a given text.

**Parameters:**
- `presentation_id` (string, required)
- `search_text` (string, required)

**Returns:** List of slide indices where text found

---

## Working with Presentation Names vs IDs

- Use `list_presentations` to look up IDs by name.
- Internally, always refer to presentations by ID for all tool/API calls.
- When talking with users, ONLY use the presentation name, never internal IDs.

**Communication Examples:**
- ✅ "I've added a slide to 'Quarterly Review'."
- ❌ "I've added a slide to presentation abc-123-def-456."

---

## Example Complete Workflow

```
# Create a new presentation (or get existing if name matches)
create_presentation(name="Quarterly Review")

# Add slides (auto-generates output files after each edit)
add_slide(presentation_id="abc-123", title="2024 Review")
add_slide(presentation_id="abc-123", title="Highlights", content="- Revenue up 20%\n- 500 new customers")

# Count slides
get_slide_count(presentation_id="abc-123")

# Export - specify paths once, they're stored for future use
generate_markdown(presentation_id="abc-123", output_file="quarterly.md")
generate_pptx(presentation_id="abc-123", output_file="quarterly.pptx")
generate_html(presentation_id="abc-123", output_file="quarterly.html")

# Later regeneration - paths remembered, no need to specify again
generate_pptx(presentation_id="abc-123")  # Uses stored path "quarterly.pptx"

# Import from markdown - links the file paths automatically
import_from_markdown(markdown_file="mydeck.md", presentation_name="My Deck")
# Now mydeck.md, mydeck.pptx, mydeck.html are all linked
```

---

## Image Path Handling

- Use **relative paths** for all images in content.
- On export, images referenced this way will be found and included (base64-embedded for HTML).
- Example:
```
add_slide(presentation_id="abc-123", title="Chart", content="![Q2 Chart](./charts/q2.png)")
```

---

## Supported Markdown Elements

You may use:
- Headers: `#`, `##`, `###`
- Lists: `-`, `*`, `1.`
- Code blocks (triple backticks, language optional)
- Tables: pipe-delimited
- Images: markdown format `![alt](path)`
- Paragraphs, bold, and horizontal rules (`---` for slide breaks/import)

---

## Slide Layout Detection

Slide layout is inferred based on:
| Content type                        | Chosen layout         |
|-------------------------------------|-----------------------|
| H1 + one image, no content          | title_slide           |
| H1 + H2 + one image, no content     | title_slide_subtitle  |
| Contains a table                    | table                 |
| Contains code block                 | code                  |
| Any image, no content               | image_focused         |
| Image(s) and content                | image_content         |
| Contains bullet list                | bullets               |
| H1 + H2                             | title_subtitle        |
| Other                               | title_content         |

---

## Best Practices

1. Use clear, descriptive presentation names.
2. Always refer to presentations by name to users.
3. Store image paths relative to working directory.
4. Always check slide count before using indices.
5. Use `list_presentations` for presentation lookup.
6. Exported HTML files are fully standalone and distributable.
7. **Bulk edits:** Disable auto-generation with `set_auto_generate(False)` when making many consecutive edits, then re-enable to generate final output.
8. **File paths:** Specify output file paths once—they're remembered. No need to repeat paths on subsequent calls.
9. If a user provides a file to use for a deck, that file defines the authoritative content–overwrite the presentation accordingly and do not preserve prior slides or memory.

---

## Important Notes

- All presentations exist between tool calls–state is persistent.
- Slide numbering is zero-based.
- **Auto-generation:** Output files (.md, .pptx, .html) are regenerated automatically after every edit. Use `set_auto_generate(False)` to disable during bulk operations.
- **File path persistence:** Once output file paths are set (via generation or import), they are stored in the presentation and reused consistently. This prevents file naming mismatches.
- Output files default to the notebook's working directory if no path is specified.
- Theme files (`pptx_theme.json`, `pptx_theme_blue.json`, etc.) are available for stylistic consistency.
- When importing or updating with a user file, always implement the File Import/Update Precedence Policy.

---

**Final Reminder:**  
Whenever a user supplies a file for import or update, that file becomes the single source of truth for the specified presentation. Ignore prior content, apply the new content immediately, and confirm this replacement to the user. Only merge or preserve prior content at the user’s explicit direction.

