"""
Markdown operations module.
Handles parsing markdown into slide structures and assembling slides into markdown.
"""

import re


def parse_slide_markdown(slide_md):
    """
    Parse a slide's markdown to extract title, subtitle, and content.
    
    Only the first H1 becomes title and first H2 becomes subtitle.
    Any subsequent H1/H2 headers remain in content to prevent data loss.
    
    Args:
        slide_md: Markdown string for a single slide
        
    Returns:
        dict with keys: title, subtitle, content
    """
    lines = slide_md.strip().split('\n')
    title = None
    subtitle = None
    content_lines = []
    title_found = False
    subtitle_found = False
    
    for line in lines:
        stripped = line.strip()
        # Only take the FIRST H1 as title
        if stripped.startswith('# ') and not title_found:
            title = stripped[2:].strip()
            title_found = True
        # Only take the FIRST H2 as subtitle
        elif stripped.startswith('## ') and not subtitle_found:
            subtitle = stripped[3:].strip()
            subtitle_found = True
        else:
            content_lines.append(line)
    
    content = '\n'.join(content_lines).strip()
    return {
        "title": title,
        "subtitle": subtitle,
        "content": content
    }


def assemble_slide_markdown(title, subtitle, content):
    """
    Assemble markdown string from title, subtitle, and content components.
    
    Prevents duplicate H1/H2 headers by stripping any leading headers from
    content that match the provided title/subtitle.
    
    Args:
        title: H1 header text (optional)
        subtitle: H2 header text (optional)
        content: Markdown content (optional)
        
    Returns:
        Complete markdown string for the slide
    """
    parts = []
    if title:
        parts.append(f"# {title}")
    if subtitle:
        parts.append(f"## {subtitle}")
    
    if content:
        # Strip duplicate headers from content that match title/subtitle
        content = content.strip()
        lines = content.split('\n')
        cleaned_lines = []
        headers_stripped = False
        
        for line in lines:
            stripped = line.strip()
            # Skip duplicate H1 that matches title (only at the beginning before non-header content)
            if not headers_stripped and title and stripped == f"# {title}":
                continue
            # Skip duplicate H2 that matches subtitle (only at the beginning before non-header content)
            if not headers_stripped and subtitle and stripped == f"## {subtitle}":
                continue
            # Skip empty lines at the very start (before any content)
            if not headers_stripped and not cleaned_lines and not stripped:
                continue
            # Once we hit non-header content, stop stripping headers
            if stripped and not stripped.startswith('# ') and not stripped.startswith('## '):
                headers_stripped = True
            cleaned_lines.append(line)
        
        cleaned_content = '\n'.join(cleaned_lines).strip()
        if cleaned_content:
            parts.append(cleaned_content)
    
    return "\n\n".join(parts)


def parse_markdown_to_slides(markdown_text):
    """
    Parse full markdown text into slide structures.
    Slides are separated by '---' (horizontal rule).
    
    Args:
        markdown_text: Full markdown content
        
    Returns:
        List of slide dictionaries with id, order, title, subtitle, content
    """
    import uuid
    
    # Split by slide separators
    slide_texts = re.split(r'\n---+\n', markdown_text)
    
    slides = []
    for i, slide_text in enumerate(slide_texts):
        slide_text = slide_text.strip()
        if not slide_text:
            continue
        
        parsed = parse_slide_markdown(slide_text)
        slides.append({
            "id": str(uuid.uuid4()),
            "order": i,
            "title": parsed["title"],
            "subtitle": parsed["subtitle"],
            "content": parsed["content"]
        })
    
    return slides


def assemble_slides_to_markdown(slides):
    """
    Assemble slides into full markdown text with separators.
    
    Args:
        slides: List of slide dictionaries
        
    Returns:
        Complete markdown string
    """
    # Sort by order
    sorted_slides = sorted(slides, key=lambda s: s.get('order', 0))
    
    slide_markdowns = []
    for slide in sorted_slides:
        md = assemble_slide_markdown(
            slide.get('title'),
            slide.get('subtitle'),
            slide.get('content', '')
        )
        slide_markdowns.append(md)
    
    return '\n\n---\n\n'.join(slide_markdowns)

