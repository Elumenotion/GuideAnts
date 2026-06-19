"""
Main API module for presentation management.
Provides all public functions for creating, editing, and exporting presentations.
"""

import os
import re
import uuid
from . import storage
from . import markdown_ops
from .storage import presentation_lock, index_lock

# Sentinel value to detect when a parameter was explicitly provided vs not provided
_NOT_PROVIDED = object()

# Auto-generation settings
_auto_generate_enabled = True


def set_auto_generate(enabled):
    """
    Enable or disable automatic file generation on edits.
    
    Args:
        enabled: True to auto-generate .md, .pptx, and .html on edits, False to disable
    """
    global _auto_generate_enabled
    _auto_generate_enabled = enabled
    status = "enabled" if enabled else "disabled"
    print(f"Auto-generation {status}")


def _sanitize_filename(name):
    """Sanitize a presentation name for use as a filename."""
    # Replace invalid characters with underscores
    sanitized = re.sub(r'[<>:"/\\|?*]', '_', name)
    # Remove leading/trailing spaces and dots
    sanitized = sanitized.strip(' .')
    # Limit length
    if len(sanitized) > 200:
        sanitized = sanitized[:200]
    return sanitized or "presentation"


def _sort_slides_by_order(slides):
    """
    Sort slides by their order property WITHOUT modifying order values.
    
    Use this for read-only operations where you need slides in correct order
    but don't want to change the stored order values (important during
    parallel add_slide calls where intended order must be preserved).
    
    Args:
        slides: List of slide dictionaries
        
    Returns:
        List of slides sorted by order (order values unchanged)
    """
    if not slides:
        return slides
    return sorted(slides, key=lambda s: s.get('order', 0))


def _normalize_slides(slides):
    """
    Sort slides by order AND re-normalize order values to be sequential.
    
    Use this ONLY for write operations that modify the slide list (delete, reorder)
    where you need to ensure order values are sequential (0, 1, 2, ...).
    
    DO NOT use this during/after parallel add_slide calls - it will corrupt
    the intended order values.
    
    Args:
        slides: List of slide dictionaries
        
    Returns:
        List of slides sorted by order with normalized order values (0, 1, 2, ...)
    """
    if not slides:
        return slides
    
    # Sort by the 'order' property
    sorted_slides = sorted(slides, key=lambda s: s.get('order', 0))
    
    # Re-normalize order values to be sequential
    for i, slide in enumerate(sorted_slides):
        slide['order'] = i
    
    return sorted_slides


def _auto_generate_outputs(presentation_id, theme_file=None):
    """
    Automatically generate .md, .pptx, and .html files using stored paths or defaults.
    Called after any modification to a presentation.
    
    Uses stored file paths (markdown_file, pptx_file, html_file) if available,
    otherwise generates paths from the presentation name and stores them.
    
    Args:
        presentation_id: Presentation UUID
        theme_file: Optional theme file for PPTX and HTML generation
    """
    if not _auto_generate_enabled:
        return
    
    try:
        presentation = storage.load_presentation(presentation_id)
    except FileNotFoundError:
        return  # Presentation was deleted, nothing to generate
    
    cwd = os.getcwd()
    files_updated = False
    
    # Determine file paths - use stored paths or derive from name
    md_path = presentation.get('markdown_file')
    pptx_path = presentation.get('pptx_file')
    html_path = presentation.get('html_file')
    
    # If no stored paths, derive from presentation name
    if not md_path or not pptx_path or not html_path:
        name = presentation.get('name', 'presentation')
        safe_name = _sanitize_filename(name)
        
        if not md_path:
            md_path = os.path.join(cwd, f"{safe_name}.md")
            presentation['markdown_file'] = md_path
            files_updated = True
        if not pptx_path:
            pptx_path = os.path.join(cwd, f"{safe_name}.pptx")
            presentation['pptx_file'] = pptx_path
            files_updated = True
        if not html_path:
            html_path = os.path.join(cwd, f"{safe_name}.html")
            presentation['html_file'] = html_path
            files_updated = True
    
    # Save updated file paths before generation (but don't touch slide order -
    # parallel add_slide calls store intended order values that must be preserved)
    if files_updated:
        storage.save_presentation(presentation)
    
    # Sort slides by order for rendering (don't modify stored order values)
    slides = presentation.get('slides', [])
    sorted_slides = sorted(slides, key=lambda s: s.get('order', 0))
    
    # Generate markdown using sorted slides
    markdown_text = markdown_ops.assemble_slides_to_markdown(sorted_slides)
    
    # If no slides, create placeholder markdown to avoid zero-byte file
    if not markdown_text.strip():
        name = presentation.get('name', 'Presentation')
        markdown_text = f"# {name}\n\n*(No slides yet)*\n"
    
    # Ensure directory exists for markdown
    md_dir = os.path.dirname(os.path.abspath(md_path))
    if md_dir:
        os.makedirs(md_dir, exist_ok=True)
    
    with open(md_path, 'w', encoding='utf-8') as f:
        f.write(markdown_text)
    
    # Generate PPTX using the markdown file we just wrote
    pptx_success = False
    try:
        pptx_dir = os.path.dirname(os.path.abspath(pptx_path))
        if pptx_dir:
            os.makedirs(pptx_dir, exist_ok=True)
        _generate_pptx_internal(md_path, pptx_path, theme_file)
        pptx_success = True
    except Exception as e:
        print(f"[Auto-generated] {os.path.basename(md_path)} (PPTX generation failed: {e})")
    
    # Generate HTML using the markdown file we just wrote
    html_success = False
    try:
        from . import html_generator
        html_dir = os.path.dirname(os.path.abspath(html_path))
        if html_dir:
            os.makedirs(html_dir, exist_ok=True)
        html_generator.generate_html(md_path, html_path, theme_file)
        html_success = True
    except Exception as e:
        print(f"[Auto-generated] {os.path.basename(md_path)} (HTML generation failed: {e})")


def _generate_pptx_internal(markdown_file, output_file, theme_file=None):
    """
    Internal PPTX generation logic.
    
    Args:
        markdown_file: Path to the source markdown file
        output_file: Path for output .pptx file
        theme_file: Optional theme JSON file
    """
    import importlib.util
    
    PowerPointGenerator = None
    
    # Strategy 1: Try to import as a module (if it's in Python path)
    try:
        from md_to_pptx_professional import PowerPointGenerator
    except ImportError:
        pass
    
    # Strategy 2: Try common relative paths from the module location
    if PowerPointGenerator is None:
        module_dir = os.path.dirname(os.path.abspath(__file__))
        base_dir = os.path.dirname(os.path.dirname(os.path.dirname(__file__)))
        possible_paths = [
            os.path.join(module_dir, 'md_to_pptx_professional.py'),
            os.path.join(module_dir, '..', 'Resources', 'md_to_pptx_professional.py'),
            os.path.join(
                base_dir, 'server', 'ContentFiles', '133707e1-7c82-49a4-9395-57e25e22376a',
                'notebooks', '12a000b6-1436-4058-9d06-d085eb5e78b5', 'Output',
                'md_to_pptx_professional.py'
            ),
            os.path.join(os.getcwd(), 'md_to_pptx_professional.py'),
        ]
        
        for generator_path in possible_paths:
            if os.path.exists(generator_path):
                spec = importlib.util.spec_from_file_location("md_to_pptx_professional", generator_path)
                module = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(module)
                PowerPointGenerator = module.PowerPointGenerator
                break
    
    if PowerPointGenerator is None:
        raise ImportError(
            "Could not find PowerPointGenerator (md_to_pptx_professional.py). "
            "Please ensure the file is accessible in the Python path or provide the path."
        )
    
    generator = PowerPointGenerator(markdown_file, output_file, theme_file=theme_file)
    generator.generate()


# Custom exceptions
class PresentationNotFoundError(Exception):
    """Raised when a presentation ID doesn't exist."""
    pass


class InvalidSlideIndexError(Exception):
    """Raised when a slide index is out of range."""
    pass


def _format_presentation_not_found_error(presentation_id):
    """
    Format a helpful error message for invalid presentation IDs with retry instructions.
    
    Args:
        presentation_id: The invalid presentation ID
        
    Returns:
        str: Formatted error message with retry instructions
    """
    # Get list of all presentations for suggestions
    index = storage.load_index()
    presentations = index.get("presentations", [])
    
    error_msg = f"Presentation with ID '{presentation_id}' not found.\n\n"
    error_msg += "To find the correct presentation ID:\n"
    error_msg += "1. Use list_presentations() to see all available presentations with their IDs\n"
    error_msg += "2. If you know the presentation name, search the list for the matching name and use its ID\n\n"
    
    if presentations:
        error_msg += f"Available presentations ({len(presentations)} total):\n"
        for p in presentations[:10]:  # Show first 10 to avoid overwhelming output
            error_msg += f"  - '{p.get('name')}' (ID: {p.get('id')})\n"
        if len(presentations) > 10:
            error_msg += f"  ... and {len(presentations) - 10} more\n"
    else:
        error_msg += "No presentations are currently available.\n"
    
    return error_msg


# Presentation Management

def create_presentation(name, initial_content=None):
    """
    Create a new presentation or return existing one if name already exists.
    
    This operation is atomic - parallel calls with the same name will be serialized
    by the index lock. The lock is re-entrant so nested calls to update_index work.
    
    Args:
        name: Presentation name
        initial_content: Optional initial markdown content (only used if creating new)
        
    Returns:
        tuple: (presentation_id, presentation_dict)
    """
    # Use index_lock to make check-and-create atomic
    # The lock is re-entrant so nested update_index calls will succeed
    with index_lock():
        # Check if presentation with this name already exists
        existing_id = storage.find_presentation_by_name(name)
        if existing_id:
            presentation = storage.load_presentation(existing_id)
            pres_id = presentation['id']
            slide_count = len(presentation.get('slides', []))
            print(f"Found existing presentation '{name}' (ID: {pres_id}) with {slide_count} slide(s)")
            return pres_id, presentation
        
        # Create new presentation (nested update_index will use re-entrant lock)
        presentation = storage.create_new_presentation(name, initial_content)
        pres_id = presentation['id']
        slide_count = len(presentation.get('slides', []))
        print(f"Created presentation '{name}' (ID: {pres_id}) with {slide_count} slide(s)")
    
    _auto_generate_outputs(pres_id)
    return pres_id, presentation


def list_presentations():
    """
    List all presentations.
    
    Returns:
        List of presentation metadata dictionaries
    """
    index = storage.load_index()
    presentations = index.get("presentations", [])
    print(f"Found {len(presentations)} presentation(s):")
    for p in presentations:
        print(f"  - {p.get('name')} (ID: {p.get('id')}, Updated: {p.get('updated_at', 'unknown')})")
    return presentations


def delete_presentation(presentation_id):
    """
    Delete a presentation.
    
    Args:
        presentation_id: Presentation UUID
        
    Returns:
        bool: Success status
    """
    try:
        # Get name before deleting for output
        try:
            presentation = storage.load_presentation(presentation_id)
            name = presentation.get('name', 'Unknown')
        except:
            name = 'Unknown'
        
        storage.delete_presentation_file(presentation_id)
        print(f"Deleted presentation '{name}' (ID: {presentation_id})")
        return True
    except FileNotFoundError:
        print(f"Presentation {presentation_id} not found - nothing to delete")
        return False


def copy_presentation(presentation_id, new_name):
    """
    Create a copy of a presentation.
    
    Args:
        presentation_id: Source presentation UUID
        new_name: Name for the copy
        
    Returns:
        str: New presentation UUID
    """
    # Read the source presentation (no lock needed since we're only reading)
    try:
        original = storage.load_presentation(presentation_id)
    except FileNotFoundError:
        raise PresentationNotFoundError(_format_presentation_not_found_error(presentation_id))
    
    # Create new presentation with copied slides
    new_id = str(uuid.uuid4())
    now = storage.datetime.utcnow().isoformat() + 'Z'
    
    # Deep copy slides with new IDs
    new_slides = []
    for slide in original.get('slides', []):
        new_slide = {
            "id": str(uuid.uuid4()),
            "order": slide.get('order', 0),
            "title": slide.get('title'),
            "subtitle": slide.get('subtitle'),
            "content": slide.get('content', '')
        }
        new_slides.append(new_slide)
    
    new_presentation = {
        "id": new_id,
        "name": new_name,
        "created_at": now,
        "updated_at": now,
        "slides": new_slides
    }
    # Note: We do not copy markdown_file, pptx_file, html_file paths
    # The copy will derive new file paths on first generation
    
    storage.save_presentation(new_presentation)
    print(f"Copied presentation '{original.get('name')}' to '{new_name}' (New ID: {new_id})")
    _auto_generate_outputs(new_id)
    return new_id


# Slide Operations

def add_slide(presentation_id, title=None, subtitle=None, content="", position=None):
    """
    Add a new slide to the presentation.
    
    Args:
        presentation_id: Presentation UUID
        title: H1 header text (optional)
        subtitle: H2 header text (optional)
        content: Markdown content (optional)
        position: Insert position (0-based). REQUIRED for correct ordering in parallel calls.
                  If omitted, slide is appended but may be out of order.
        
    Returns:
        bool: Success status
    """
    position_warning = False
    
    with presentation_lock(presentation_id):
        try:
            presentation = storage.load_presentation(presentation_id)
        except FileNotFoundError:
            raise PresentationNotFoundError(_format_presentation_not_found_error(presentation_id))
        
        slides = presentation.get('slides', [])
        current_count = len(slides)
        
        # Determine the order value for this slide
        if position is None:
            # No position specified - append at end but warn
            intended_order = current_count
            position_warning = True
        else:
            # Use the specified position as the intended order
            intended_order = position
        
        # Create new slide with the intended order value
        new_slide = {
            "id": str(uuid.uuid4()),
            "order": intended_order,
            "title": title,
            "subtitle": subtitle,
            "content": content
        }
        
        # Always append to array - the 'order' property determines actual position
        # This handles parallel calls gracefully: each slide stores its intended order,
        # and rendering/export will sort by order value
        slides.append(new_slide)
        
        presentation['slides'] = slides
        storage.save_presentation(presentation)
        
        title_str = f"'{title}'" if title else "(no title)"
        if position_warning:
            print(f"WARNING: Added slide {title_str} without position - assigned order={intended_order}. "
                  f"Slides may be out of order. Use reorder_slides to fix if needed. (now {len(slides)} slide(s))")
        else:
            print(f"Added slide {title_str} with order={intended_order} to presentation (now {len(slides)} slide(s))")
    
    _auto_generate_outputs(presentation_id)
    return True


def update_slide(presentation_id, slide_index, title=_NOT_PROVIDED, subtitle=_NOT_PROVIDED, content=_NOT_PROVIDED):
    """
    Update slide content. Only provided fields are updated.
    
    Args:
        presentation_id: Presentation UUID
        slide_index: Zero-based slide index (based on order, not array position)
        title: New title (None = clear field, _NOT_PROVIDED = keep existing)
        subtitle: New subtitle (None = clear field, _NOT_PROVIDED = keep existing)
        content: New content (None = clear field, _NOT_PROVIDED = keep existing)
        
    Returns:
        bool: Success status
    """
    changes = []
    
    with presentation_lock(presentation_id):
        try:
            presentation = storage.load_presentation(presentation_id)
        except FileNotFoundError:
            raise PresentationNotFoundError(f"Presentation {presentation_id} not found")
        
        slides = presentation.get('slides', [])
        
        # Sort slides by order (don't re-normalize - preserves parallel insert order values)
        slides = _sort_slides_by_order(slides)
        presentation['slides'] = slides
        
        if slide_index < 0 or slide_index >= len(slides):
            raise InvalidSlideIndexError(f"Slide index {slide_index} out of range")
        
        slide = slides[slide_index]
        old_title = slide.get('title')
        old_subtitle = slide.get('subtitle')
        old_content = slide.get('content', '')
        
        # Update title if provided
        if title is not _NOT_PROVIDED:
            if title != old_title:
                slide['title'] = title
                old_str = old_title if old_title is not None else '(none)'
                new_str = title if title is not None else '(cleared)'
                changes.append(f"title: '{old_str}' -> '{new_str}'")
        
        # Update subtitle if provided
        if subtitle is not _NOT_PROVIDED:
            if subtitle != old_subtitle:
                slide['subtitle'] = subtitle
                old_str = old_subtitle if old_subtitle is not None else '(none)'
                new_str = subtitle if subtitle is not None else '(cleared)'
                changes.append(f"subtitle: '{old_str}' -> '{new_str}'")
        
        # Update content if provided
        if content is not _NOT_PROVIDED:
            if content != old_content:
                slide['content'] = content
                changes.append("content updated")
        
        if changes:
            storage.save_presentation(presentation)
            print(f"Updated slide {slide_index}: {', '.join(changes)}")
        else:
            print(f"Slide {slide_index} unchanged (no updates provided)")
    
    if changes:
        _auto_generate_outputs(presentation_id)
    return True


def delete_slide(presentation_id, slide_index):
    """
    Delete a slide from the presentation.
    
    Args:
        presentation_id: Presentation UUID
        slide_index: Zero-based slide index (based on order, not array position)
        
    Returns:
        bool: Success status
    """
    with presentation_lock(presentation_id):
        try:
            presentation = storage.load_presentation(presentation_id)
        except FileNotFoundError:
            raise PresentationNotFoundError(_format_presentation_not_found_error(presentation_id))
        
        slides = presentation.get('slides', [])
        
        # Normalize slides first to ensure array position matches order
        slides = _normalize_slides(slides)
        
        if slide_index < 0 or slide_index >= len(slides):
            raise InvalidSlideIndexError(f"Slide index {slide_index} out of range")
        
        # Get slide info before deleting for output
        deleted_slide = slides[slide_index]
        deleted_title = deleted_slide.get('title', '(no title)')
        
        # Create a new list without the deleted slide
        new_slides = [slide for i, slide in enumerate(slides) if i != slide_index]
        
        # Re-normalize order values
        for i, slide in enumerate(new_slides):
            slide['order'] = i
        
        presentation['slides'] = new_slides
        storage.save_presentation(presentation)
        print(f"Deleted slide {slide_index} ('{deleted_title}') from presentation (now {len(new_slides)} slide(s))")
    
    _auto_generate_outputs(presentation_id)
    return True


def get_slide(presentation_id, slide_index):
    """
    Get slide content and metadata.
    
    Args:
        presentation_id: Presentation UUID
        slide_index: Zero-based slide index (based on order, not array position)
        
    Returns:
        dict: Slide structure with title, subtitle, content, raw_markdown
    """
    try:
        presentation = storage.load_presentation(presentation_id)
    except FileNotFoundError:
        raise PresentationNotFoundError(_format_presentation_not_found_error(presentation_id))
    
    slides = presentation.get('slides', [])
    
    # Sort slides by order (read-only, don't modify order values)
    slides = _sort_slides_by_order(slides)
    
    if slide_index < 0 or slide_index >= len(slides):
        raise InvalidSlideIndexError(f"Slide index {slide_index} out of range")
    
    slide = slides[slide_index]
    # Get raw values to ensure we're reading correctly
    raw_title = slide.get('title')
    raw_subtitle = slide.get('subtitle')
    raw_content = slide.get('content', '')
    
    raw_markdown = markdown_ops.assemble_slide_markdown(
        raw_title,
        raw_subtitle,
        raw_content
    )
    
    result = {
        "index": slide_index,
        "id": slide.get('id'),
        "title": raw_title,
        "subtitle": raw_subtitle,
        "content": raw_content,
        "raw_markdown": raw_markdown
    }
    
    # print(f"\n=== Slide {slide_index} ===")
    # print(f"ID: {result['id']}")
    # print(f"Title: {raw_title if raw_title is not None else '(no title)'}")
    # print(f"Subtitle: {raw_subtitle if raw_subtitle is not None else '(no subtitle)'}")
    # print(f"Content:")
    # content = result['content']
    # if content:
    #     print(content)
    # else:
    #     print("  (no content)")
    # print(f"\nRaw Markdown:")
    # print(raw_markdown)
    # print("=" * 50)
    
    return result


def get_all_slides(presentation_id):
    """
    Get all slides in the presentation.
    
    Args:
        presentation_id: Presentation UUID
        
    Returns:
        list: List of slide dictionaries sorted by order
    """
    try:
        presentation = storage.load_presentation(presentation_id)
    except FileNotFoundError:
        raise PresentationNotFoundError(_format_presentation_not_found_error(presentation_id))
    
    slides = presentation.get('slides', [])
    
    # Sort slides by order (read-only, don't modify order values)
    slides = _sort_slides_by_order(slides)
    
    result = []
    for i, slide in enumerate(slides):
        raw_markdown = markdown_ops.assemble_slide_markdown(
            slide.get('title'),
            slide.get('subtitle'),
            slide.get('content', '')
        )
        result.append({
            "index": i,
            "id": slide.get('id'),
            "title": slide.get('title'),
            "subtitle": slide.get('subtitle'),
            "content": slide.get('content', ''),
            "raw_markdown": raw_markdown
        })
    
    # print(f"\nRetrieved {len(result)} slide(s):\n")
    # for slide_data in result:
    #     print(f"=== Slide {slide_data['index']} ===")
    #     print(f"ID: {slide_data['id']}")
    #     title = slide_data['title']
    #     print(f"Title: {title if title is not None else '(no title)'}")
    #     subtitle = slide_data['subtitle']
    #     print(f"Subtitle: {subtitle if subtitle is not None else '(no subtitle)'}")
    #     print(f"Content:")
    #     content = slide_data['content']
    #     if content:
    #         print(content)
    #     else:
    #         print("  (no content)")
    #     print(f"\nRaw Markdown:")
    #     print(slide_data['raw_markdown'])
    #     print("=" * 50)
    #     print()
    
    return result


def reorder_slides(presentation_id, new_order):
    """
    Reorder slides in the presentation.
    
    Use this to fix slide ordering after parallel add_slide calls resulted in
    slides being out of order.
    
    Args:
        presentation_id: Presentation UUID
        new_order: List of slide indices in desired order (e.g., [2, 0, 1] moves slide 2 to first)
        
    Returns:
        bool: Success status
    """
    with presentation_lock(presentation_id):
        try:
            presentation = storage.load_presentation(presentation_id)
        except FileNotFoundError:
            raise PresentationNotFoundError(_format_presentation_not_found_error(presentation_id))
        
        slides = presentation.get('slides', [])
        
        # Normalize slides first to ensure consistent state
        slides = _normalize_slides(slides)
        
        # Validate indices
        if len(new_order) != len(slides):
            raise ValueError(f"new_order length {len(new_order)} doesn't match slide count {len(slides)}")
        
        if set(new_order) != set(range(len(slides))):
            raise ValueError("new_order must contain all slide indices exactly once")
        
        # Reorder slides
        reordered = [slides[i] for i in new_order]
        
        # Update order values
        for i, slide in enumerate(reordered):
            slide['order'] = i
        
        presentation['slides'] = reordered
        storage.save_presentation(presentation)
        print(f"Reordered {len(reordered)} slide(s) - new order: {new_order}")
    
    _auto_generate_outputs(presentation_id)
    return True


def duplicate_slide(presentation_id, slide_index):
    """
    Create a copy of a slide.
    
    Args:
        presentation_id: Presentation UUID
        slide_index: Zero-based slide index to duplicate (based on order, not array position)
        
    Returns:
        bool: Success status
    """
    with presentation_lock(presentation_id):
        try:
            presentation = storage.load_presentation(presentation_id)
        except FileNotFoundError:
            raise PresentationNotFoundError(_format_presentation_not_found_error(presentation_id))
        
        slides = presentation.get('slides', [])
        
        # Sort slides by order first
        slides = _sort_slides_by_order(slides)
        
        if slide_index < 0 or slide_index >= len(slides):
            raise InvalidSlideIndexError(f"Slide index {slide_index} out of range")
        
        original_slide = slides[slide_index]
        new_slide = {
            "id": str(uuid.uuid4()),
            "order": slide_index + 1,
            "title": original_slide.get('title'),
            "subtitle": original_slide.get('subtitle'),
            "content": original_slide.get('content', '')
        }
        
        # Reorder slides after insertion point
        for slide in slides[slide_index + 1:]:
            slide['order'] = slide.get('order', 0) + 1
        
        slides.insert(slide_index + 1, new_slide)
        
        # Re-normalize order values
        for i, slide in enumerate(slides):
            slide['order'] = i
        
        presentation['slides'] = slides
        storage.save_presentation(presentation)
        original_title = original_slide.get('title', '(no title)')
        print(f"Duplicated slide {slide_index} ('{original_title}') - new slide at position {slide_index + 1} (now {len(slides)} slide(s))")
    
    _auto_generate_outputs(presentation_id)
    return True


# Content Helpers

def append_to_slide(presentation_id, slide_index, markdown_content):
    """
    Append content to an existing slide.
    
    Args:
        presentation_id: Presentation UUID
        slide_index: Zero-based slide index (based on order, not array position)
        markdown_content: Content to append
        
    Returns:
        bool: Success status
    """
    with presentation_lock(presentation_id):
        try:
            presentation = storage.load_presentation(presentation_id)
        except FileNotFoundError:
            raise PresentationNotFoundError(f"Presentation {presentation_id} not found")
        
        slides = presentation.get('slides', [])
        
        # Sort slides by order (don't re-normalize - preserves parallel insert order values)
        slides = _sort_slides_by_order(slides)
        presentation['slides'] = slides
        
        if slide_index < 0 or slide_index >= len(slides):
            raise InvalidSlideIndexError(f"Slide index {slide_index} out of range")
        
        slide = slides[slide_index]
        current_content = slide.get('content', '')
        new_content = current_content + "\n\n" + markdown_content if current_content else markdown_content
        slide['content'] = new_content
        
        storage.save_presentation(presentation)
        title_str = slide.get('title', '(no title)')
        print(f"Appended content to slide {slide_index} ('{title_str}')")
    
    _auto_generate_outputs(presentation_id)
    return True


def prepend_to_slide(presentation_id, slide_index, markdown_content):
    """
    Prepend content to an existing slide.
    
    Args:
        presentation_id: Presentation UUID
        slide_index: Zero-based slide index (based on order, not array position)
        markdown_content: Content to prepend
        
    Returns:
        bool: Success status
    """
    with presentation_lock(presentation_id):
        try:
            presentation = storage.load_presentation(presentation_id)
        except FileNotFoundError:
            raise PresentationNotFoundError(_format_presentation_not_found_error(presentation_id))
        
        slides = presentation.get('slides', [])
        
        # Sort slides by order (don't re-normalize - preserves parallel insert order values)
        slides = _sort_slides_by_order(slides)
        presentation['slides'] = slides
        
        if slide_index < 0 or slide_index >= len(slides):
            raise InvalidSlideIndexError(f"Slide index {slide_index} out of range")
        
        slide = slides[slide_index]
        current_content = slide.get('content', '')
        new_content = markdown_content + "\n\n" + current_content if current_content else markdown_content
        slide['content'] = new_content
        
        storage.save_presentation(presentation)
        title_str = slide.get('title', '(no title)')
        print(f"Prepended content to slide {slide_index} ('{title_str}')")
    
    _auto_generate_outputs(presentation_id)
    return True


# Export & Generation

def generate_markdown(presentation_id, output_file=None):
    """
    Generate markdown file from presentation.
    
    Regenerates all output files (md, pptx, html) to keep them in sync.
    
    Args:
        presentation_id: Presentation UUID
        output_file: Path for output .md file (optional, uses stored path or derives from name)
        
    Returns:
        str: Path to the generated markdown file
    """
    try:
        presentation = storage.load_presentation(presentation_id)
    except FileNotFoundError:
        raise PresentationNotFoundError(f"Presentation {presentation_id} not found")
    
    if output_file:
        # User specified a path - store it
        output_file = os.path.abspath(output_file)
        if presentation.get('markdown_file') != output_file:
            presentation['markdown_file'] = output_file
            storage.save_presentation(presentation)
    
    # Regenerate all outputs (md, pptx, html)
    _auto_generate_outputs(presentation_id)
    
    # Return the markdown path
    presentation = storage.load_presentation(presentation_id)
    return presentation.get('markdown_file')


def generate_pptx(presentation_id, output_file=None, theme_file=None):
    """
    Generate PowerPoint file from presentation.
    
    Regenerates all output files (md, pptx, html) to keep them in sync.
    
    Args:
        presentation_id: Presentation UUID
        output_file: Path for output .pptx file (optional, uses stored path or derives from name)
        theme_file: Path to theme JSON file (optional)
        
    Returns:
        str: Path to the generated pptx file
    """
    try:
        presentation = storage.load_presentation(presentation_id)
    except FileNotFoundError:
        raise PresentationNotFoundError(_format_presentation_not_found_error(presentation_id))
    
    if output_file:
        # User specified a path - store it
        output_file = os.path.abspath(output_file)
        if presentation.get('pptx_file') != output_file:
            presentation['pptx_file'] = output_file
            storage.save_presentation(presentation)
    
    # Regenerate all outputs (md, pptx, html)
    _auto_generate_outputs(presentation_id, theme_file)
    
    # Return the pptx path
    presentation = storage.load_presentation(presentation_id)
    return presentation.get('pptx_file')


def import_from_markdown(markdown_file, presentation_name=None):
    """
    Import a markdown file as a presentation, updating content if presentation exists.
    
    If a presentation with the same name exists, its slides are REPLACED with
    the content from the markdown file. The imported file path is stored as
    the presentation's markdown_file for future regenerations.
    
    Args:
        markdown_file: Path to markdown file to import
        presentation_name: Name for new presentation (None = use filename)
        
    Returns:
        str: Presentation UUID
    """
    if not os.path.exists(markdown_file):
        raise FileNotFoundError(f"Markdown file {markdown_file} not found")
    
    abs_markdown_file = os.path.abspath(markdown_file)
    
    with open(abs_markdown_file, 'r', encoding='utf-8') as f:
        markdown_text = f.read()
    
    if presentation_name is None:
        presentation_name = os.path.splitext(os.path.basename(markdown_file))[0]
    
    # Parse markdown into slides
    slides = markdown_ops.parse_markdown_to_slides(markdown_text)
    
    # Check if presentation with this name already exists
    existing_id = storage.find_presentation_by_name(presentation_name)
    
    if existing_id:
        # Update existing presentation with new slides from the markdown
        with presentation_lock(existing_id):
            presentation = storage.load_presentation(existing_id)
            old_slide_count = len(presentation.get('slides', []))
            presentation['slides'] = slides
            presentation_id = existing_id
            
            # Store the imported file paths
            presentation['markdown_file'] = abs_markdown_file
            base_path = os.path.splitext(abs_markdown_file)[0]
            presentation['pptx_file'] = f"{base_path}.pptx"
            presentation['html_file'] = f"{base_path}.html"
            
            storage.save_presentation(presentation)
            print(f"Updated existing presentation '{presentation_name}' (ID: {presentation_id}): {old_slide_count} -> {len(slides)} slide(s)")
    else:
        # Create new presentation
        presentation = storage.create_new_presentation(presentation_name, markdown_text)
        presentation_id = presentation['id']
        
        # Update file paths (lock for the new presentation)
        with presentation_lock(presentation_id):
            presentation = storage.load_presentation(presentation_id)
            presentation['markdown_file'] = abs_markdown_file
            base_path = os.path.splitext(abs_markdown_file)[0]
            presentation['pptx_file'] = f"{base_path}.pptx"
            presentation['html_file'] = f"{base_path}.html"
            storage.save_presentation(presentation)
        
        print(f"Created presentation '{presentation_name}' (ID: {presentation_id}) with {len(slides)} slide(s)")
    
    base_path = os.path.splitext(abs_markdown_file)[0]
    print(f"Imported markdown file '{markdown_file}' as presentation '{presentation_name}'")
    print(f"  Linked files: {base_path}.md, .pptx, .html")
    
    _auto_generate_outputs(presentation_id)
    return presentation_id


# Utility Functions

def get_slide_count(presentation_id):
    """
    Get the number of slides in the presentation.
    
    Args:
        presentation_id: Presentation UUID
        
    Returns:
        int: Number of slides
    """
    try:
        presentation = storage.load_presentation(presentation_id)
    except FileNotFoundError:
        raise PresentationNotFoundError(_format_presentation_not_found_error(presentation_id))
    
    count = len(presentation.get('slides', []))
    print(f"Presentation has {count} slide(s)")
    return count


def search_slides(presentation_id, search_text):
    """
    Search for slides containing the given text.
    
    Args:
        presentation_id: Presentation UUID
        search_text: Text to search for
        
    Returns:
        list: List of slide indices (by order) containing the text
    """
    try:
        presentation = storage.load_presentation(presentation_id)
    except FileNotFoundError:
        raise PresentationNotFoundError(_format_presentation_not_found_error(presentation_id))
    
    search_lower = search_text.lower()
    matching_indices = []
    
    slides = presentation.get('slides', [])
    
    # Sort slides by order (read-only, don't modify order values)
    slides = _sort_slides_by_order(slides)
    
    for i, slide in enumerate(slides):
        # Search in title, subtitle, and content
        searchable_text = ' '.join([
            slide.get('title', ''),
            slide.get('subtitle', ''),
            slide.get('content', '')
        ]).lower()
        
        if search_lower in searchable_text:
            matching_indices.append(i)
    
    if matching_indices:
        print(f"Found '{search_text}' in {len(matching_indices)} slide(s): {matching_indices}")
    else:
        print(f"No slides found containing '{search_text}'")
    return matching_indices


def generate_html(presentation_id, output_file=None, theme_file=None):
    """
    Generate standalone HTML file from presentation with base64-encoded images.
    
    Regenerates all output files (md, pptx, html) to keep them in sync.
    
    Args:
        presentation_id: Presentation UUID
        output_file: Path for output .html file (optional, uses stored path or derives from name)
        theme_file: Path to theme JSON file (optional)
        
    Returns:
        str: Path to the generated html file
    """
    try:
        presentation = storage.load_presentation(presentation_id)
    except FileNotFoundError:
        raise PresentationNotFoundError(_format_presentation_not_found_error(presentation_id))
    
    if output_file:
        # User specified a path - store it
        output_file = os.path.abspath(output_file)
        if presentation.get('html_file') != output_file:
            presentation['html_file'] = output_file
            storage.save_presentation(presentation)
    
    # Regenerate all outputs (md, pptx, html)
    _auto_generate_outputs(presentation_id, theme_file)
    
    # Return the html path
    presentation = storage.load_presentation(presentation_id)
    return presentation.get('html_file')
