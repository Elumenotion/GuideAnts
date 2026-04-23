# Layout Scenarios Documentation

This document outlines all layout scenarios handled by the PowerPoint generator, which the HTML generator must replicate.

## Slide Type Detection Logic

The system detects slide types based on:
- Presence of title (H1)
- Presence of subtitle (H2)
- Number of images
- Content types (bullets, paragraphs, tables, code blocks)

## Layout Scenarios

### 1. Title Slide (`title_slide`)
**Condition**: H1 only + exactly 1 image + no content

**Layouts by Image Orientation:**

#### 1a. Portrait Title Slide
- Image: Full slide height on LEFT, width calculated from aspect ratio
- Title: Centered vertically on RIGHT side
- Text alignment: Center

#### 1b. Landscape Title Slide
- Image: Full slide background (fills entire slide)
- Title: Centered both horizontally and vertically, overlaid on image
- Text alignment: Center
- Z-index: Title above image

#### 1c. Square Title Slide
- Title: Centered at top (header area)
- Image: Centered in body below title
- Text alignment: Center for title

---

### 2. Title Slide with Subtitle (`title_slide_subtitle`)
**Condition**: H1 + H2 + exactly 1 image + no content

**Layouts by Image Orientation:**

#### 2a. Portrait Title Slide with Subtitle
- Image: Full slide height on RIGHT, width calculated from aspect ratio
- Title: In header area (top left)
- Subtitle: Below title on left side
- Text alignment: Center for both

#### 2b. Landscape Title Slide with Subtitle
- Image: Full slide background (fills entire slide)
- Title: Centered vertically, overlaid on image
- Subtitle: Below title, also overlaid
- Text alignment: Center for both
- Z-index: Text above image

#### 2c. Square Title Slide with Subtitle
- Title: Centered at top (header area)
- Subtitle: Centered in top of body, below title
- Image: Centered in body below subtitle
- Text alignment: Center for all

---

### 3. Image Focused Slide (`image_focused`)
**Condition**: Has images + no content (or only H3/H4 headings)

**Variants:**

#### 3a. Single Image with Title
- Title: Centered at top (optional)
- Image: Centered below title, constrained to max dimensions
- Position: Below title at 2.5" from top

#### 3b. Single Image without Title
- Image: Fills entire slide, maintaining aspect ratio
- Position: Full slide (0,0) to (width, height)

#### 3c. Multiple Images with Title
- Title: Centered at top
- Images: Arranged side-by-side, centered horizontally
- Image size: 4.0" × 3.0" each
- Spacing: 0.5" between images
- Position: Below title at 2.5" from top

#### 3d. Multiple Images without Title
- Images: Arranged side-by-side, centered horizontally
- Image size: 5.5" × 4.0" each (larger than with title)
- Spacing: 0.5" between images
- Position: 1.75" from top

---

### 4. Image Content Slide (`image_content`)
**Condition**: Has images + has content (bullets, paragraphs, etc.)

**Variants:**

#### 4a. Content + Single Image
- Title: Left-aligned at top (if present)
- Content: Left column (6.5" width, starting at 1.5" from top)
  - Supports: Bullet lists, paragraphs, numbered lists
  - Mixed content types supported
- Image: Right column (4.5" width, starting at 1.5" from top)
- Layout: Two-column (content left, image right)

#### 4b. Content + Multiple Images
- Title: Left-aligned at top (if present)
- Content: Left column (6.5" width)
- Images: Right column, stacked vertically
  - Each image: 4.5" width
  - Height: content_height / num_images
  - Spacing: 0.2" between images

#### 4c. Images Only (no content)
- If no content exists but images are present:
  - Single image: Centered, max dimensions
  - Multiple images: Side-by-side, centered

---

### 5. Table Slide (`table`)
**Condition**: Has table in content

**Layout:**
- Title: Left-aligned at top (if present)
- Table: Full content width
- Position: Below title (1.5" from top if title, 1.0" if no title)
- Height: Calculated based on row count (max 5.5")
- Styling:
  - Header row: Primary color background, white text, bold
  - Data rows: Alternating light background
  - Supports images in cells (centered with padding)

---

### 6. Code Slide (`code`)
**Condition**: Has code block in content

**Layout:**
- Title: Left-aligned at top (if present)
- Code block: Full content width
- Position: Below title (1.5" from top)
- Height: 5" (fixed)
- Styling:
  - Background: Light gray (code_bg)
  - Border: Light gray (code_border)
  - Font: Monospace (Consolas)
  - Padding: 0.25" on all sides

---

### 7. Bullet Slide (`bullets`)
**Condition**: Has bullet lists or numbered lists (may have paragraphs between)

**Layout:**
- Title: Left-aligned at top (if present)
- Content: Full content width
- Position: Below title (1.5" from top)
- Height: 5.5"
- Supports:
  - Multiple separate bullet lists (separated by paragraphs)
  - Nested bullets (indentation levels)
  - Numbered lists
  - Mixed content: paragraphs, bullets, numbered lists, H3 headings
  - Text formatting: bold, italic, bold-italic

---

### 8. Title Subtitle Slide (`title_subtitle`)
**Condition**: Has H1 + H2, no images, has content

**Layout:**
- Title: Centered vertically (calculated position)
- Subtitle: Below title with spacing (0.4")
- Content: Below subtitle (if any)
- Text alignment: Center for title and subtitle

---

### 9. Title Content Slide (`title_content`)
**Condition**: Default fallback - has title, may have content

**Layout:**
- Title: Left-aligned at top
- Content: Full content width below title
- Position: Below title (1.5" from top)
- Height: 5.5"
- Supports:
  - Paragraphs
  - Bullet lists
  - Numbered lists
  - H3 headings
  - Mixed content types
  - Text formatting

---

## Common Elements

### Footer
- Position: Bottom of slide (0.4" from bottom edge)
- Width: Full content width
- Height: 0.3"
- Styling: Center-aligned, secondary color, 10pt font
- Configurable: Can be disabled on title slides via theme

### Text Formatting
All text supports:
- **Bold** (`**text**`)
- *Italic* (`*text*`)
- ***Bold Italic*** (`***text***`)

### Spacing
- Title space after: 12pt
- Body space after: 6pt
- Bullet space after: 8pt
- Paragraph spacing: 0.25"
- Line spacing: 1.2

### Colors (Default Theme)
- Primary: RGB(31, 56, 100) - Dark blue
- Secondary: RGB(89, 89, 89) - Gray
- Accent: RGB(0, 120, 212) - Blue
- Background: RGB(255, 255, 255) - White
- Light BG: RGB(248, 249, 250) - Light gray
- Code BG: RGB(248, 248, 248) - Very light gray
- Code Border: RGB(200, 200, 200) - Light gray border

### Typography (Default Theme)
- Title font: Calibri Light, 54pt, bold
- Subtitle font: Calibri, 32pt
- Body font: Calibri, 20pt
- Bullet font: Calibri, 20pt
- Code font: Consolas, 16pt

### Slide Dimensions
- Width: 13.33 inches (1280px at 96 DPI)
- Height: 7.5 inches (720px at 96 DPI)
- Aspect ratio: 16:9

### Margins
- Top: 0.5"
- Bottom: 0.5"
- Left: 1.0"
- Right: 1.0"
- Content area: 11.33" × 6.5"

