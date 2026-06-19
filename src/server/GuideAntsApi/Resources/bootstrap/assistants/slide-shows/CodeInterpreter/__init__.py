"""
Presentation API Module
Provides a simple API for creating and managing presentations stored as structured data,
with on-demand generation of markdown and PowerPoint files.
"""

from .api import (
    # Presentation Management
    create_presentation,
    list_presentations,
    delete_presentation,
    copy_presentation,
    
    # Slide Operations
    add_slide,
    update_slide,
    delete_slide,
    get_slide,
    get_all_slides,
    reorder_slides,
    duplicate_slide,
    
    # Content Helpers
    append_to_slide,
    prepend_to_slide,
    
    # Export & Generation
    generate_markdown,
    generate_pptx,
    generate_html,  # Generates standalone HTML presentation
    import_from_markdown,
    
    # Utility Functions
    get_slide_count,
    search_slides,
)

__all__ = [
    'create_presentation',
    'list_presentations',
    'delete_presentation',
    'copy_presentation',
    'add_slide',
    'update_slide',
    'delete_slide',
    'get_slide',
    'get_all_slides',
    'reorder_slides',
    'duplicate_slide',
    'append_to_slide',
    'prepend_to_slide',
    'generate_markdown',
    'generate_pptx',
    'generate_html',
    'import_from_markdown',
    'get_slide_count',
    'search_slides',
]

