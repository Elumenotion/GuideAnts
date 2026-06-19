"""
HTML Generator for Markdown to PowerPoint Converter
Creates standalone HTML presentations with base64-encoded images that match PPTX layouts.
"""

import os
import re
import base64
import json
import sys
from PIL import Image
from urllib.request import urlopen, Request
from io import BytesIO


# Default headers for URL requests - many servers block requests without User-Agent
DEFAULT_URL_HEADERS = {
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'
}


def _create_url_request(url):
    """Create a Request object with proper headers for downloading images"""
    return Request(url, headers=DEFAULT_URL_HEADERS)


# Import from md_to_pptx_professional
try:
    from .md_to_pptx_professional import (
        ThemeLoader, Typography, MarkdownParser, LayoutEngine
    )
except ImportError:
    # Fallback for direct execution
    import importlib.util
    module_path = os.path.join(os.path.dirname(__file__), 'md_to_pptx_professional.py')
    spec = importlib.util.spec_from_file_location("md_to_pptx_professional", module_path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    ThemeLoader = module.ThemeLoader
    Typography = module.Typography
    MarkdownParser = module.MarkdownParser
    LayoutEngine = module.LayoutEngine


class HTMLGenerator:
    """Generates standalone HTML presentations matching PPTX layouts"""
    
    def __init__(self, md_file, output_file, theme_file=None):
        self.md_file = md_file
        self.output_file = output_file
        
        # Store markdown directory for resolving relative image paths
        if os.path.exists(md_file):
            self.md_dir = os.path.dirname(os.path.abspath(md_file))
        else:
            self.md_dir = os.getcwd()
        
        # Auto-find theme file if not specified
        if theme_file is None:
            if os.path.exists(md_file):
                md_theme = os.path.join(self.md_dir, 'pptx_theme.json')
                if os.path.exists(md_theme):
                    theme_file = md_theme
            
            if theme_file is None:
                theme_file = ThemeLoader.find_theme_file()
        
        self.theme_file = theme_file
        
        # Load theme
        self.theme = ThemeLoader.load_theme(theme_file)
        Typography.load_theme(theme_file)
        LayoutEngine.load_theme(theme_file)
        
        # Debug: Print theme info
        if self.theme:
            pass # print(f"HTML Generator: Loaded theme from {theme_file}")
            # print(f"  Colors: {list(self.theme.get('colors', {}).keys())}")
            # print(f"  Typography: {list(self.theme.get('typography', {}).keys())}")
            # print(f"  Layout: {list(self.theme.get('layout', {}).keys())}")
        else:
            print(f"Warning: HTML Generator: No theme loaded!")
        
        # Image cache for base64 encoding
        self.image_cache = {}
        
        self.parser = None
    
    def _optimize_image(self, img_data):
        """Resize and compress image data to reduce size"""
        try:
            img = Image.open(BytesIO(img_data))
            
            # Resize
            max_size = (1024, 1024) # reasonable limit for slides
            if img.size[0] > max_size[0] or img.size[1] > max_size[1]:
                img.thumbnail(max_size, Image.Resampling.LANCZOS)
            
            output_io = BytesIO()
            
            # Determine format
            # Convert RGBA/P to JPEG if transparency not critical, or optimize PNG
            # For this use case (slides), JPEG is preferred for size unless transparency is vital.
            # We'll stick to JPEG for non-transparent, and PNG for transparent.
            
            format = 'JPEG'
            mime_type = 'image/jpeg'
            
            if img.mode == 'RGBA':
                # Check if it actually uses transparency? 
                # For now assume yes if RGBA.
                format = 'PNG'
                mime_type = 'image/png'
            elif img.mode == 'P':
                 if 'transparency' in img.info:
                    format = 'PNG'
                    mime_type = 'image/png'
                 else:
                    img = img.convert('RGB')
            elif img.mode != 'RGB':
                 img = img.convert('RGB')
            
            if format == 'JPEG':
                img.save(output_io, format='JPEG', quality=65, optimize=True)
            else:
                # Optimize PNG (limited in PIL but optimize=True helps)
                img.save(output_io, format='PNG', optimize=True)
            
            return output_io.getvalue(), mime_type
            
        except Exception as e:
            print(f"Warning: Failed to optimize image: {e}")
            return img_data, None # Fallback to original

    def _image_to_base64(self, image_path):
        """Convert image to base64 data URI, with caching"""
        # Check cache first
        if image_path in self.image_cache:
            return self.image_cache[image_path]
        
        # Handle URLs
        is_url = image_path.startswith('http://') or image_path.startswith('https://')
        
        img_data = None
        mime_type = None
        
        if is_url:
            try:
                url_request = _create_url_request(image_path)
                with urlopen(url_request) as response:
                    content_type = response.headers.get('Content-Type', 'image/jpeg')
                    img_data = response.read()
                    
                    # Check if we got HTML instead of image
                    if img_data and (img_data[:50].lower().startswith(b'<!doctype') or img_data[:50].lower().startswith(b'<html')):
                        return None
                    
                    # Initial mime type guess
                    if 'png' in content_type.lower():
                        mime_type = 'image/png'
                    elif 'gif' in content_type.lower():
                        mime_type = 'image/gif'
                    elif 'webp' in content_type.lower():
                        mime_type = 'image/webp'
                    else:
                        mime_type = 'image/jpeg'
            except Exception as e:
                return None
        else:
            # Handle local files
            resolved_path = self._resolve_image_path(image_path)
            if not os.path.exists(resolved_path):
                print(f"Warning: Image not found: {resolved_path}")
                return None
            
            try:
                with open(resolved_path, 'rb') as f:
                    img_data = f.read()
                
                # Determine MIME type from file extension
                ext = os.path.splitext(resolved_path)[1].lower()
                mime_types = {
                    '.png': 'image/png',
                    '.jpg': 'image/jpeg',
                    '.jpeg': 'image/jpeg',
                    '.gif': 'image/gif',
                    '.webp': 'image/webp'
                }
                mime_type = mime_types.get(ext, 'image/jpeg')
            except Exception as e:
                print(f"Error reading image {resolved_path}: {e}")
                return None
        
        # Optimize image
        if img_data:
            optimized_data, optimized_mime = self._optimize_image(img_data)
            if optimized_mime:
                img_data = optimized_data
                mime_type = optimized_mime
            
            base64_data = base64.b64encode(img_data).decode('utf-8')
            data_uri = f"data:{mime_type};base64,{base64_data}"
            self.image_cache[image_path] = data_uri
            return data_uri
        
        return None
    
    def _resolve_image_path(self, image_path):
        """Resolve image path relative to markdown file directory"""
        if image_path.startswith('http://') or image_path.startswith('https://'):
            return image_path
        
        if os.path.isabs(image_path) and os.path.exists(image_path):
            return image_path
        
        resolved = os.path.join(self.md_dir, image_path)
        if os.path.exists(resolved):
            return resolved
        
        if os.path.exists(image_path):
            return image_path
        
        return image_path
    
    def _get_image_orientation(self, image_path):
        """Get image orientation: 'portrait', 'landscape', or 'square'"""
        is_url = image_path.startswith('http://') or image_path.startswith('https://')
        temp_file = None
        
        if is_url:
            try:
                url_request = _create_url_request(image_path)
                with urlopen(url_request) as response:
                    img_data = response.read()
                    
                    # Check if we got HTML instead of image
                    if img_data[:50].lower().startswith(b'<!doctype') or img_data[:50].lower().startswith(b'<html'):
                        return 'landscape'
                    
                    temp_file = BytesIO(img_data)
                    img = Image.open(temp_file)
                    img_width, img_height = img.size
                    aspect_ratio = img_width / img_height
                    
                    if 0.9 <= aspect_ratio <= 1.1:
                        return 'square'
                    elif aspect_ratio > 1.1:
                        return 'landscape'
                    else:
                        return 'portrait'
            except Exception as e:
                return 'landscape'
        
        resolved_path = self._resolve_image_path(image_path)
        if not os.path.exists(resolved_path):
            return None
        
        try:
            with Image.open(resolved_path) as img:
                img_width, img_height = img.size
                aspect_ratio = img_width / img_height
                
                if 0.9 <= aspect_ratio <= 1.1:
                    return 'square'
                elif aspect_ratio > 1.1:
                    return 'landscape'
                else:
                    return 'portrait'
        except Exception as e:
            print(f"Error reading image {resolved_path}: {e}")
            return None
    
    def _process_text_formatting(self, text):
        """Process markdown formatting (bold, italic, links) to HTML"""
        # Convert markdown links [text](url) to HTML links
        text = re.sub(r'\[([^\]]+)\]\(([^)]+)\)', r'<a href="\2" target="_blank">\1</a>', text)
        # Handle bold italic first (***text***)
        text = re.sub(r'\*\*\*(.+?)\*\*\*', r'<strong><em>\1</em></strong>', text)
        # Handle bold (**text**)
        text = re.sub(r'\*\*(.+?)\*\*', r'<strong>\1</strong>', text)
        # Handle italic (*text*)
        text = re.sub(r'\*(.+?)\*', r'<em>\1</em>', text)
        return text
    
    def _get_css(self):
        """Generate CSS styles matching theme"""
        if not self.theme:
            print("Error: No theme loaded! Cannot generate CSS.")
            return "<style>/* Error: No theme loaded */</style>"
        
        theme = self.theme
        if 'colors' not in theme or 'typography' not in theme or 'layout' not in theme:
            print(f"Error: Theme missing required sections! Has: {list(theme.keys())}")
            return "<style>/* Error: Theme missing required sections */</style>"
        
        colors = theme['colors']
        typography = theme['typography']
        layout = theme['layout']
        
        # Convert inches to pixels (96 DPI)
        slide_width_px = int(layout['slide_dimensions']['width'] * 96)
        slide_height_px = int(layout['slide_dimensions']['height'] * 96)
        margin_top_px = int(layout['margins']['top'] * 96)
        margin_bottom_px = int(layout['margins']['bottom'] * 96)
        margin_left_px = int(layout['margins']['left'] * 96)
        margin_right_px = int(layout['margins']['right'] * 96)
        
        # Color helpers
        def rgb_to_hex(rgb):
            return f"#{rgb[0]:02x}{rgb[1]:02x}{rgb[2]:02x}"
        
        primary_color = rgb_to_hex(colors['primary'])
        secondary_color = rgb_to_hex(colors['secondary'])
        accent_color = rgb_to_hex(colors['accent'])
        bg_color = rgb_to_hex(colors['background'])
        light_bg = rgb_to_hex(colors['light_bg'])
        code_bg = rgb_to_hex(colors['code_bg'])
        code_border = rgb_to_hex(colors['code_border'])
        
        return f"""
        <style>
            * {{
                margin: 0;
                padding: 0;
                box-sizing: border-box;
            }}
            
            body {{
                font-family: '{typography['fonts']['body']}', Arial, sans-serif;
                background-color: {bg_color};
                color: {primary_color};
                line-height: {typography['spacing']['line_spacing']};
            }}
            
            .presentation-container {{
                width: 100%;
                max-width: {slide_width_px}px;
                margin: 0 auto;
                background-color: {bg_color};
            }}
            
            .slide {{
                width: {slide_width_px}px;
                height: {slide_height_px}px;
                background-color: {bg_color};
                position: relative;
                margin: 0 auto;
                overflow: hidden;
                box-shadow: 0 4px 20px rgba(0, 0, 0, 0.3);
                border: 3px solid {primary_color};
                border-radius: 4px;
            }}
            
            .content-area {{
                position: absolute;
                left: {margin_left_px}px;
                top: {margin_top_px}px;
                width: {slide_width_px - margin_left_px - margin_right_px}px;
                height: {slide_height_px - margin_top_px - margin_bottom_px}px;
                box-sizing: border-box;
                overflow: hidden; /* Prevent scrollbars, content should fit */
                display: flex;
                flex-direction: column;
            }}
            
            /* Responsive content area - Disabled to preserve layout fidelity during scaling
            @media (max-width: {slide_width_px + 100}px) {{
                .content-area {{
                    left: clamp(10px, 2vw, {margin_left_px}px);
                    top: clamp(10px, 2vw, {margin_top_px}px);
                    width: calc(100% - clamp(20px, 4vw, {margin_left_px + margin_right_px}px));
                    height: calc(100% - clamp(20px, 4vw, {margin_top_px + margin_bottom_px}px));
                }}
            }}
            */
            
            /* Typography */
            .slide-title {{
                font-family: '{typography['fonts']['title']}', Arial, sans-serif;
                font-size: {int(typography['sizes']['title'] * 0.9)}pt; /* Slightly smaller for better fit */
                font-weight: bold;
                color: {primary_color};
                margin-bottom: {typography['spacing']['title_space_after']}pt;
            }}
            
            .slide-subtitle {{
                font-family: '{typography['fonts']['body']}', Arial, sans-serif;
                font-size: {int(typography['sizes']['subtitle'] * 0.9)}pt;
                color: {primary_color};
                margin-bottom: {typography['spacing']['body_space_after']}pt;
            }}
            
            .slide-body {{
                font-family: '{typography['fonts']['body']}', Arial, sans-serif;
                font-size: {int(typography['sizes']['body'] * 0.9)}pt;
                color: {primary_color};
                line-height: {typography['spacing']['line_spacing']};
                margin-bottom: {typography['spacing']['body_space_after']}pt;
            }}
            
            .slide-bullet {{
                font-family: '{typography['fonts']['body']}', Arial, sans-serif;
                font-size: {int(typography['sizes']['bullet'] * 0.9)}pt;
                color: {primary_color};
                margin-bottom: {typography['spacing']['bullet_space_after']}pt;
                margin-left: {int(layout['spacing']['bullet_indent_base'] * 96)}px;
            }}
            
            .slide-bullet.level-1 {{
                margin-left: {int(layout['spacing']['bullet_indent_base'] * 96 * 2)}px;
            }}
            
            .slide-code {{
                font-family: '{typography['fonts']['code']}', 'Courier New', monospace;
                font-size: {typography['sizes']['code']}pt;
                color: {primary_color};
                background-color: {code_bg};
                border: 1px solid {code_border};
                padding: {int(0.25 * 96)}px;
                border-radius: 4px;
                white-space: pre-wrap;
                overflow-x: auto;
            }}
            
            /* Layout utilities */
            .text-center {{
                text-align: center;
            }}
            
            .text-left {{
                text-align: left;
            }}
            
            .flex-row {{
                display: flex;
                flex-direction: row;
            }}
            
            .flex-column {{
                display: flex;
                flex-direction: column;
            }}
            
            .flex-center {{
                display: flex;
                justify-content: center;
                align-items: center;
            }}
            
            /* Title slide layouts */
            .title-slide-portrait {{
                display: flex;
                height: 100%;
                width: 100%;
            }}
            
            .title-slide-portrait .image-container {{
                height: 100%;
                width: auto;
                flex-shrink: 0;
                display: flex;
                align-items: center;
                justify-content: flex-start;
                overflow: hidden;
            }}
            
            .title-slide-portrait .image-container img {{
                height: 100%;
                width: auto;
                object-fit: contain;
                object-position: left center;
            }}
            
            .title-slide-portrait .title-container {{
                flex: 1;
                display: flex;
                align-items: center;
                justify-content: center;
                padding: 0 {margin_right_px}px;
                min-width: 0; /* Allow flex item to shrink */
            }}
            
            .title-slide-portrait.reverse {{
                flex-direction: row-reverse;
            }}
            
            /* Responsive title slide layouts - Disabled to preserve layout fidelity
            @media (max-width: {slide_width_px + 100}px) {{
                .title-slide-portrait, .title-slide-portrait.reverse {{
                    flex-direction: column;
                }}
                
                .title-slide-portrait .image-container {{
                    width: 100%;
                    height: auto;
                    flex: 0 0 auto;
                    max-height: 50%;
                }}
                
                .title-slide-portrait .image-container img {{
                    width: 100%;
                    height: auto;
                    max-height: 100%;
                    object-fit: contain;
                }}
                
                .title-slide-portrait .title-container {{
                    padding: clamp(10px, 2vw, {margin_right_px}px);
                    flex: 1;
                }}
            }}
            */
            
            .title-slide-landscape {{
                position: relative;
                width: 100%;
                height: 100%;
            }}
            
            .title-slide-landscape .image-container {{
                position: absolute;
                top: 0;
                left: 0;
                width: 100%;
                height: 100%;
                z-index: 1;
            }}
            
            .title-slide-landscape .title-container {{
                position: absolute;
                top: 0;
                left: 0;
                width: 100%;
                height: 100%;
                z-index: 2;
                display: flex;
                align-items: center;
                justify-content: center;
            }}
            
            .title-slide-square {{
                display: flex;
                flex-direction: column;
                height: 100%;
                overflow: hidden; /* Ensure content stays within slide */
            }}
            
            .title-slide-square .title-container {{
                padding: {margin_top_px}px {margin_left_px}px {int(0.4 * 96)}px {margin_right_px}px;
                flex-shrink: 0; /* Prevent title from shrinking */
            }}
            
            .title-slide-square .image-container {{
                flex: 1;
                display: flex;
                align-items: center;
                justify-content: center;
                min-height: 0; /* Allow flex item to shrink properly */
                padding-bottom: {margin_bottom_px}px;
            }}
            
            /* Image content slide */
            .image-content-layout {{
                display: flex;
                flex-direction: row;
                flex: 1;
                min-height: 0;
                gap: {int(0.5 * 96)}px;
                margin-top: {int(0.2 * 96)}px;
            }}
            
            .image-content-layout .content-column {{
                flex: 1;
                overflow-y: hidden;
            }}
            
            .image-content-layout .image-column {{
                flex: 0 0 40%;
                display: flex;
                flex-direction: column;
                gap: {int(0.2 * 96)}px;
                justify-content: flex-start;
            }}
            
            /* Responsive image-content layout - Disabled to preserve layout fidelity
            @media (max-width: {slide_width_px + 100}px) {{
                .image-content-layout {{
                    flex-direction: column;
                    gap: clamp(10px, 2vw, {int(0.5 * 96)}px);
                }}
                
                .image-content-layout .content-column,
                .image-content-layout .image-column {{
                    flex: 1 1 auto;
                    padding-top: clamp(10px, 2vw, {int(1.5 * 96)}px);
                }}
            }}
            */
            
            /* Image focused slide */
            .image-focused-layout {{
                display: flex;
                flex-direction: column;
                height: 100%;
            }}
            
            .image-focused-layout .title-container {{
                padding: {margin_top_px}px {margin_left_px}px {int(0.4 * 96)}px {margin_right_px}px;
            }}
            
            .image-focused-layout .images-container {{
                flex: 1;
                display: flex;
                align-items: center;
                justify-content: center;
                gap: {int(0.5 * 96)}px;
            }}
            
            /* Table styles */
            .table-scroll-container {{
                flex: 1;
                overflow-y: auto;
                min-height: 0;
                margin-top: {int(0.2 * 96)}px;
                padding-right: 10px;
            }}

            .slide-table {{
                width: 100%;
                border-collapse: collapse;
                margin-top: 0;
            }}
            
            .slide-table th {{
                background-color: {primary_color};
                color: white;
                font-weight: bold;
                padding: {int(0.3 * 96)}px;
                text-align: center;
            }}
            
            .slide-table td {{
                padding: {int(0.3 * 96)}px;
                border: 1px solid {code_border};
            }}
            
            .slide-table tr:nth-child(even) td {{
                background-color: {light_bg};
            }}
            
            .slide-table .cell-image {{
                text-align: center;
            }}
            
            /* Code block */
            .code-block-container {{
                margin-top: {int(0.2 * 96)}px;
                width: 100%;
                flex: 1;
                min-height: 0;
                overflow: auto;
            }}
            
            /* Bullet list */
            .bullet-list-container {{
                margin-top: {int(0.2 * 96)}px;
                flex: 1;
                min-height: 0;
                overflow-y: auto; /* Allow scrolling if list is too long */
            }}
            
            .bullet-item {{
                margin-bottom: {typography['spacing']['bullet_space_after']}pt;
            }}
            
            .numbered-item {{
                margin-bottom: {typography['spacing']['bullet_space_after']}pt;
            }}
            
            /* Footer */
            .slide-footer {{
                position: absolute;
                bottom: {int(0.4 * 96)}px;
                left: {margin_left_px}px;
                width: {slide_width_px - margin_left_px - margin_right_px}px;
                text-align: center;
                font-size: 10pt;
                color: {secondary_color};
            }}
            
            /* Image styles */
            .slide-image {{
                max-width: 100%;
                height: auto;
                object-fit: contain;
            }}
            
            .slide-image-full {{
                width: 100%;
                height: 100%;
                object-fit: cover;
            }}
            
            /* Slideshow controls */
            .slideshow-container {{
                position: relative;
                width: 100%;
                height: 100%;
                display: flex;
                align-items: center;
                justify-content: center;
                overflow: hidden;
            }}
            
            .slideshow-wrapper {{
                position: relative;
                width: 100vw;
                height: 100vh;
                display: flex;
                align-items: center;
                justify-content: center;
                background-color: {bg_color};
                overflow: hidden;
            }}
            
            .slide {{
                display: none;
                width: {slide_width_px}px;
                height: {slide_height_px}px;
                background-color: {bg_color};
                position: absolute;
                top: 50%;
                left: 50%;
                transform-origin: center center;
                transform: translate(-50%, -50%);
                box-shadow: 0 4px 20px rgba(0, 0, 0, 0.3);
                border: 3px solid {primary_color};
                border-radius: 4px;
                overflow: hidden;
            }}
            
            .slide.active {{
                display: block;
            }}
            
            .slide.fade {{
                animation: fadeIn 0.5s;
            }}
            
            @keyframes fadeIn {{
                from {{ opacity: 0; }}
                to {{ opacity: 1; }}
            }}
            
            /* Responsive scaling is handled by JS */
            /* Removed old media queries for CSS-only scaling */
            
            /* Make content responsive */
            /* Removed clamp() sizes to rely on transform: scale() */
            
            /* Responsive images */
            .slide-image {{
                max-width: 100% !important;
                height: auto !important;
                object-fit: contain;
            }}
            
            .slide-image-full {{
                width: 100% !important;
                height: 100% !important;
                object-fit: cover;
            }}
            
            /* Responsive tables */
            .slide-table {{
                font-size: {typography['sizes']['body']}pt;
                width: 100%;
                border-collapse: collapse;
            }}
            
            .slide-table th,
            .slide-table td {{
                padding: {int(0.3 * 96)}px;
                border: 1px solid {code_border};
            }}
            
            /* Responsive controls - Tablet */
            @media (max-width: 768px) {{
                .side-nav-button {{
                    width: 45px;
                    height: 70px;
                    font-size: 22px;
                    min-width: 45px; /* Ensure touch target is large enough */
                    min-height: 70px;
                }}
                
                .side-nav-button:hover {{
                    width: 50px;
                }}
                
                .side-nav-button.prev {{
                    left: 5px;
                }}
                
                .side-nav-button.next {{
                    right: 5px;
                }}
                
                .slideshow-controls {{
                    padding: 6px 12px;
                    bottom: 15px;
                    font-size: 12px;
                }}
                
                .slide-counter {{
                    font-size: 12px;
                    min-width: 50px;
                }}
                
                .slide-dots {{
                    bottom: 70px;
                    gap: 6px;
                }}
                
                .dot {{
                    width: 10px;
                    height: 10px;
                    min-width: 10px;
                    min-height: 10px;
                }}
                
                .keyboard-hint {{
                    top: 10px;
                    right: 10px;
                    font-size: 10px;
                    padding: 6px 10px;
                    max-width: calc(100vw - 20px);
                }}
            }}
            
            /* Responsive controls - Phone */
            @media (max-width: 480px) {{
                .side-nav-button {{
                    width: 40px;
                    height: 60px;
                    font-size: 20px;
                    min-width: 40px;
                    min-height: 60px;
                    opacity: 0.8;
                }}
                
                .side-nav-button:active {{
                    opacity: 1;
                    transform: translateY(-50%) scale(1.1);
                }}
                
                .side-nav-button.prev {{
                    left: 2px;
                }}
                
                .side-nav-button.next {{
                    right: 2px;
                }}
                
                .slideshow-controls {{
                    width: auto;
                    left: 50%;
                    transform: translateX(-50%);
                    padding: 5px 10px;
                    bottom: 10px;
                    font-size: 11px;
                }}
                
                .slide-counter {{
                    font-size: 11px;
                    min-width: 45px;
                }}
                
                .slide-dots {{
                    width: auto;
                    left: 50%;
                    transform: translateX(-50%);
                    bottom: 55px;
                    gap: 5px;
                    flex-wrap: wrap;
                    max-width: calc(100vw - 20px);
                    justify-content: center;
                }}
                
                .dot {{
                    width: 8px;
                    height: 8px;
                    min-width: 8px;
                    min-height: 8px;
                }}
                
                .keyboard-hint {{
                    display: none; /* Hide on small screens */
                }}
            }}
            
            /* Very small phones */
            @media (max-width: 360px) {{
                .side-nav-button {{
                    width: 35px;
                    height: 55px;
                    font-size: 18px;
                }}
                
                .slideshow-controls {{
                    padding: 4px 8px;
                    bottom: 8px;
                }}
                
                .slide-dots {{
                    bottom: 50px;
                    gap: 4px;
                }}
                
                .dot {{
                    width: 7px;
                    height: 7px;
                }}
            }}
            
            /* Landscape mobile */
            @media (max-width: 768px) and (orientation: landscape) {{
                .side-nav-button {{
                    width: 50px;
                    height: 80px;
                }}
                
                .slideshow-controls {{
                    bottom: 5px;
                }}
                
                .slide-dots {{
                    bottom: 50px;
                }}
            }}
            
            /* Side navigation buttons */
            .side-nav-button {{
                position: fixed;
                top: 50%;
                transform: translateY(-50%);
                z-index: 1000;
                background-color: rgba(0, 0, 0, 0.6);
                color: white;
                border: 2px solid {primary_color};
                width: 50px;
                height: 80px;
                cursor: pointer;
                border-radius: 0 8px 8px 0;
                display: flex;
                align-items: center;
                justify-content: center;
                font-size: 24px;
                font-weight: bold;
                transition: all 0.3s;
                user-select: none;
            }}
            
            .side-nav-button:hover {{
                background-color: rgba(0, 0, 0, 0.8);
                border-color: {accent_color};
                width: 60px;
            }}
            
            .side-nav-button:active {{
                background-color: rgba(0, 0, 0, 0.9);
            }}
            
            .side-nav-button:disabled {{
                opacity: 0.3;
                cursor: not-allowed;
            }}
            
            .side-nav-button.prev {{
                left: 0;
            }}
            
            .side-nav-button.next {{
                right: 0;
                border-radius: 8px 0 0 8px;
            }}
            
            /* Simplified bottom controls */
            .slideshow-controls {{
                position: fixed;
                bottom: 20px;
                left: 50%;
                transform: translateX(-50%);
                z-index: 1000;
                display: flex;
                gap: 10px;
                align-items: center;
                background-color: rgba(0, 0, 0, 0.7);
                padding: 8px 16px;
                border-radius: 20px;
            }}
            
            .slide-counter {{
                color: white;
                font-size: 14px;
                min-width: 60px;
                text-align: center;
            }}
            
            /* Keyboard hint */
            .keyboard-hint {{
                position: fixed;
                top: 20px;
                right: 20px;
                background-color: rgba(0, 0, 0, 0.7);
                color: white;
                padding: 10px 15px;
                border-radius: 5px;
                font-size: 12px;
                z-index: 1000;
                display: none;
            }}
            
            .keyboard-hint.show {{
                display: block;
            }}
            
            /* Slide dots indicator */
            .slide-dots {{
                position: fixed;
                bottom: 80px;
                left: 50%;
                transform: translateX(-50%);
                z-index: 1000;
                display: flex;
                gap: 8px;
                justify-content: center;
            }}
            
            .dot {{
                width: 12px;
                height: 12px;
                border-radius: 50%;
                background-color: rgba(255, 255, 255, 0.5);
                cursor: pointer;
                transition: background-color 0.3s;
            }}
            
            .dot.active {{
                background-color: {primary_color};
            }}
            
            .dot:hover {{
                background-color: {accent_color};
            }}
        </style>
        """
    
    def generate(self):
        """Generate HTML presentation from Markdown"""
        # Read Markdown
        with open(self.md_file, 'r', encoding='utf-8') as f:
            md_text = f.read()
        
        # Parse Markdown
        self.parser = MarkdownParser(md_text)
        slides_data = self.parser.parse()
        
        # Generate HTML
        html_slides = []
        for i, slide_data in enumerate(slides_data):
            slide_type = self.parser.detect_slide_type(slide_data)
            # print(f"HTML Slide {i+1}: Type={slide_type}, Title={slide_data.get('title')}")
            html_slide = self._create_slide_html(slide_data, slide_type)
            html_slides.append(html_slide)
        
        # Combine into full HTML document
        css = self._get_css()
        if not css or len(css.strip()) < 100:
            print("Warning: Generated CSS is empty or too short!")
        
        footer_config = LayoutEngine.get_footer_config()
        footer_html = ""
        if footer_config:
            if isinstance(footer_config, dict):
                footer_text = footer_config.get('text')
            else:
                footer_text = str(footer_config)
            if footer_text:
                footer_html = f'<div class="slide-footer">{footer_text}</div>'
        
        # print(f"HTML Generator: Generated {len(html_slides)} slides")
        # print(f"HTML Generator: CSS length: {len(css)} characters")
        
        # Generate slide dots for navigation
        slide_dots = ''.join([f'<span class="dot" onclick="goToSlide({i})" title="Slide {i+1}"></span>' 
                             for i in range(len(html_slides))])
        
        # Get dimensions for JS scaling
        layout = self.theme.get('layout', {})
        if 'slide_dimensions' in layout:
            slide_width_px = int(layout['slide_dimensions']['width'] * 96)
            slide_height_px = int(layout['slide_dimensions']['height'] * 96)
        else:
            # Fallback defaults (10x5.625 inches)
            slide_width_px = 960
            slide_height_px = 540
        
        full_html = f"""<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=5.0, user-scalable=yes">
    <meta name="mobile-web-app-capable" content="yes">
    <meta name="apple-mobile-web-app-capable" content="yes">
    <title>Presentation</title>
    {css}
</head>
<body>
    <div class="keyboard-hint" id="keyboardHint">
        Use ← → arrow keys or click buttons to navigate
    </div>
    
    <div class="slideshow-wrapper">
        <div class="slideshow-container">
            {''.join(html_slides)}
        </div>
    </div>
    
    <button class="side-nav-button prev" id="prevButton" onclick="changeSlide(-1)" title="Previous slide">‹</button>
    <button class="side-nav-button next" id="nextButton" onclick="changeSlide(1)" title="Next slide">›</button>
    
    <div class="slide-dots" id="slideDots">
        {slide_dots}
    </div>
    
    <div class="slideshow-controls">
        <span class="slide-counter" id="slideCounter">1 / {len(html_slides)}</span>
    </div>
    
    <script>
        let currentSlide = 0;
        const slides = document.querySelectorAll('.slide');
        const totalSlides = slides.length;
        
        function updateSlideScaling() {{
            const wrapper = document.querySelector('.slideshow-wrapper');
            const slides = document.querySelectorAll('.slide');
            if (!wrapper || !slides.length) return;
            
            const wrapperWidth = wrapper.clientWidth;
            const wrapperHeight = wrapper.clientHeight;
            
            // Padding/Safety margin
            const hPadding = 80; // Space for side buttons
            const vPadding = 100; // Space for bottom controls and top hint
            
            const availableWidth = wrapperWidth - hPadding;
            const availableHeight = wrapperHeight - vPadding;
            
            const slideBaseWidth = {slide_width_px};
            const slideBaseHeight = {slide_height_px};
            
            // Calculate scale
            const scaleX = availableWidth / slideBaseWidth;
            const scaleY = availableHeight / slideBaseHeight;
            const scale = Math.min(scaleX, scaleY);
            
            // Apply scale to all slides
            slides.forEach(slide => {{
                // Use translate to center, then scale
                slide.style.transform = `translate(-50%, -50%) scale(${{scale}})`;
            }});
        }}
        
        window.addEventListener('resize', updateSlideScaling);
        
        function showSlide(n) {{
            if (n >= totalSlides) {{
                currentSlide = 0;
            }} else if (n < 0) {{
                currentSlide = totalSlides - 1;
            }} else {{
                currentSlide = n;
            }}
            
            // Update URL hash
            const newHash = '#slide' + (currentSlide + 1);
            if (window.location.hash !== newHash) {{
                window.location.hash = newHash;
            }}
            
            // Hide all slides
            slides.forEach(slide => {{
                slide.classList.remove('active', 'fade');
            }});
            
            // Show current slide with fade animation
            if (slides[currentSlide]) {{
                slides[currentSlide].classList.add('active', 'fade');
            }}
            
            // Update dots
            const dots = document.querySelectorAll('.dot');
            dots.forEach((dot, index) => {{
                dot.classList.toggle('active', index === currentSlide);
            }});
            
            // Update counter
            const counter = document.getElementById('slideCounter');
            if (counter) counter.textContent = `${{currentSlide + 1}} / ${{totalSlides}}`;
            
            // Update button states
            const prevButton = document.getElementById('prevButton');
            const nextButton = document.getElementById('nextButton');
            if (prevButton) prevButton.disabled = currentSlide === 0 && totalSlides <= 1;
            if (nextButton) nextButton.disabled = currentSlide === totalSlides - 1 && totalSlides <= 1;
            
            // Ensure scaling is correct for the newly shown slide
            updateSlideScaling();
        }}
        
        function changeSlide(direction) {{
            showSlide(currentSlide + direction);
        }}
        
        function goToSlide(index) {{
            showSlide(index);
        }}
        
        function toggleFullscreen() {{
            if (!document.fullscreenElement) {{
                document.documentElement.requestFullscreen().catch(err => {{
                    console.log('Error attempting to enable fullscreen:', err);
                }});
            }} else {{
                document.exitFullscreen();
            }}
        }}
        
        // Keyboard navigation
        document.addEventListener('keydown', function(event) {{
            if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') {{
                changeSlide(-1);
            }} else if (event.key === 'ArrowRight' || event.key === 'ArrowDown' || event.key === ' ') {{
                event.preventDefault();
                changeSlide(1);
            }} else if (event.key === 'Home') {{
                goToSlide(0);
            }} else if (event.key === 'End') {{
                goToSlide(totalSlides - 1);
            }} else if (event.key === 'f' || event.key === 'F') {{
                toggleFullscreen();
            }}
        }});
        
        // Show keyboard hint on first load, hide after 5 seconds
        const hint = document.getElementById('keyboardHint');
        hint.classList.add('show');
        setTimeout(() => {{
            hint.classList.remove('show');
        }}, 5000);
        
        // Touch/swipe support for mobile
        let touchStartX = 0;
        let touchEndX = 0;
        
        document.addEventListener('touchstart', function(event) {{
            touchStartX = event.changedTouches[0].screenX;
        }}, false);
        
        document.addEventListener('touchend', function(event) {{
            touchEndX = event.changedTouches[0].screenX;
            handleSwipe();
        }}, false);
        
        function handleSwipe() {{
            const swipeThreshold = 50;
            const diff = touchStartX - touchEndX;
            
            if (Math.abs(diff) > swipeThreshold) {{
                if (diff > 0) {{
                    // Swipe left - next slide
                    changeSlide(1);
                }} else {{
                    // Swipe right - previous slide
                    changeSlide(-1);
                }}
            }}
        }}
        
        // Handle browser back/forward
        window.addEventListener('hashchange', function() {{
            const hash = window.location.hash;
            const match = hash.match(/#slide(\d+)/);
            if (match) {{
                const slideNum = parseInt(match[1], 10) - 1;
                if (slideNum >= 0 && slideNum < totalSlides && slideNum !== currentSlide) {{
                    showSlide(slideNum);
                }}
            }}
        }});
        
        // Initialize - show slide from hash or first slide
        const initialHash = window.location.hash;
        const initialMatch = initialHash.match(/#slide(\d+)/);
        if (initialMatch) {{
            const slideNum = parseInt(initialMatch[1], 10) - 1;
            if (slideNum >= 0 && slideNum < totalSlides) {{
                showSlide(slideNum);
            }} else {{
                showSlide(0);
            }}
        }} else {{
            showSlide(0);
        }}
        
        // Auto-hide controls after inactivity (optional)
        let controlsTimeout;
        const controls = document.querySelector('.slideshow-controls');
        const dots = document.querySelector('.slide-dots');
        const sideButtons = document.querySelectorAll('.side-nav-button');
        
        function resetControlsTimeout() {{
            clearTimeout(controlsTimeout);
            controls.style.opacity = '1';
            dots.style.opacity = '1';
            sideButtons.forEach(btn => btn.style.opacity = '1');
            controlsTimeout = setTimeout(() => {{
                controls.style.opacity = '0.3';
                dots.style.opacity = '0.3';
                sideButtons.forEach(btn => btn.style.opacity = '0.5');
            }}, 3000);
        }}
        
        document.addEventListener('mousemove', resetControlsTimeout);
        document.addEventListener('touchstart', resetControlsTimeout);
        resetControlsTimeout();
    </script>
</body>
</html>"""
        
        # Write to file
        with open(self.output_file, 'w', encoding='utf-8') as f:
            f.write(full_html)
        
        # print(f'HTML presentation saved as {self.output_file}')
    
    def _create_slide_html(self, slide_data, slide_type):
        """Create HTML for a slide based on type"""
        footer_config = LayoutEngine.get_footer_config()
        footer_html = ""
        if footer_config:
            if isinstance(footer_config, dict):
                footer_text = footer_config.get('text')
                show_on_title = footer_config.get('show_on_title_slide', True)
            else:
                footer_text = str(footer_config)
                show_on_title = True
            
            if footer_text:
                is_title_slide = slide_type in ['title_slide', 'title_slide_subtitle']
                if not is_title_slide or show_on_title:
                    footer_html = f'<div class="slide-footer">{footer_text}</div>'
        
        if slide_type == 'title_slide':
            slide_html = self._create_title_slide_html(slide_data)
        elif slide_type == 'title_slide_subtitle':
            slide_html = self._create_title_slide_subtitle_html(slide_data)
        elif slide_type == 'image_focused':
            slide_html = self._create_image_focused_slide_html(slide_data)
        elif slide_type == 'image_content':
            slide_html = self._create_image_content_slide_html(slide_data)
        elif slide_type == 'table':
            slide_html = self._create_table_slide_html(slide_data)
        elif slide_type == 'code':
            slide_html = self._create_code_slide_html(slide_data)
        elif slide_type == 'bullets':
            slide_html = self._create_bullet_slide_html(slide_data)
        elif slide_type == 'title_subtitle':
            slide_html = self._create_title_subtitle_slide_html(slide_data)
        else:
            slide_html = self._create_title_content_slide_html(slide_data)
        
        return f'<div class="slide">{slide_html}{footer_html}</div>'
    
    def _create_title_slide_html(self, slide_data):
        """Create title slide HTML"""
        if not slide_data['images']:
            return ""
        
        image_path = slide_data['images'][0]
        image_data = self._image_to_base64(image_path)
        if not image_data:
            # If base64 conversion fails, try to use the path as-is (for debugging)
            print(f"Warning: Could not convert image to base64: {image_path}, using path directly")
            image_data = image_path
        
        orientation = self._get_image_orientation(image_path)
        title = self._process_text_formatting(slide_data['title'] or '')
        
        if orientation == 'portrait':
            return f"""
            <div class="title-slide-portrait">
                <div class="image-container">
                    <img src="{image_data}" class="slide-image" alt="Portrait image">
                </div>
                <div class="title-container">
                    <h1 class="slide-title text-center">{title}</h1>
                </div>
            </div>
            """
        elif orientation == 'landscape':
            return f"""
            <div class="title-slide-landscape">
                <div class="image-container">
                    <img src="{image_data}" class="slide-image-full">
                </div>
                <div class="title-container">
                    <h1 class="slide-title text-center">{title}</h1>
                </div>
            </div>
            """
        else:  # square
            return f"""
            <div class="title-slide-square">
                <div class="title-container">
                    <h1 class="slide-title text-center">{title}</h1>
                </div>
                <div class="image-container">
                    <img src="{image_data}" class="slide-image" style="max-width: 80%; max-height: 80%;">
                </div>
            </div>
            """
    
    def _create_title_slide_subtitle_html(self, slide_data):
        """Create title slide with subtitle HTML"""
        if not slide_data['images']:
            return ""
        
        image_path = slide_data['images'][0]
        image_data = self._image_to_base64(image_path)
        if not image_data:
            # If base64 conversion fails, try to use the path as-is (for debugging)
            print(f"Warning: Could not convert image to base64: {image_path}, using path directly")
            image_data = image_path
        
        orientation = self._get_image_orientation(image_path)
        title = self._process_text_formatting(slide_data['title'] or '')
        subtitle = self._process_text_formatting(slide_data['subtitle'] or '')
        
        if orientation == 'portrait':
            return f"""
            <div class="title-slide-portrait reverse">
                <div class="image-container">
                    <img src="{image_data}" class="slide-image" alt="Portrait image">
                </div>
                <div class="title-container" style="flex-direction: column; justify-content: flex-start; padding-top: {int(0.5 * 96)}px;">
                    <h1 class="slide-title text-center">{title}</h1>
                    <h2 class="slide-subtitle text-center">{subtitle}</h2>
                </div>
            </div>
            """
        elif orientation == 'landscape':
            return f"""
            <div class="title-slide-landscape">
                <div class="image-container">
                    <img src="{image_data}" class="slide-image-full">
                </div>
                <div class="title-container" style="flex-direction: column;">
                    <h1 class="slide-title text-center">{title}</h1>
                    <h2 class="slide-subtitle text-center">{subtitle}</h2>
                </div>
            </div>
            """
        else:  # square
            return f"""
            <div class="title-slide-square">
                <div class="title-container">
                    <h1 class="slide-title text-center">{title}</h1>
                    <h2 class="slide-subtitle text-center">{subtitle}</h2>
                </div>
                <div class="image-container">
                    <img src="{image_data}" class="slide-image" style="max-width: 100%; max-height: 100%;">
                </div>
            </div>
            """
    
    def _create_image_focused_slide_html(self, slide_data):
        """Create image-focused slide HTML"""
        has_title = bool(slide_data.get('title'))
        has_subtitle = bool(slide_data.get('subtitle'))
        images = slide_data.get('images', [])
        
        title_html = ""
        if has_title:
            title = self._process_text_formatting(slide_data['title'])
            subtitle_part = ""
            if has_subtitle:
                subtitle = self._process_text_formatting(slide_data['subtitle'])
                subtitle_part = f'<h2 class="slide-subtitle text-center">{subtitle}</h2>'
            title_html = f'<div class="title-container"><h1 class="slide-title text-center">{title}</h1>{subtitle_part}</div>'
        
        images_html = []
        if images:
            num_images = len(images)
            if num_images == 1:
                image_data = self._image_to_base64(images[0])
                if image_data:
                    if has_title:
                        images_html.append(f'<img src="{image_data}" class="slide-image" style="max-width: {int(9 * 96)}px; max-height: {int(4.5 * 96)}px;">')
                    else:
                        images_html.append(f'<img src="{image_data}" class="slide-image-full">')
            else:
                for img_path in images:
                    image_data = self._image_to_base64(img_path)
                    if image_data:
                        if has_title:
                            images_html.append(f'<img src="{image_data}" class="slide-image" style="width: {int(4 * 96)}px; height: {int(3 * 96)}px;">')
                        else:
                            images_html.append(f'<img src="{image_data}" class="slide-image" style="width: {int(5.5 * 96)}px; height: {int(4 * 96)}px;">')
        
        return f"""
        <div class="image-focused-layout">
            {title_html}
            <div class="images-container">
                {''.join(images_html)}
            </div>
        </div>
        """
    
    def _create_image_content_slide_html(self, slide_data):
        """Create image-content slide HTML"""
        title_html = ""
        if slide_data.get('title'):
            title = self._process_text_formatting(slide_data['title'])
            title_html = f'<h1 class="slide-title">{title}</h1>'
        
        # Subtitle
        subtitle_html = ""
        if slide_data.get('subtitle'):
            subtitle = self._process_text_formatting(slide_data['subtitle'])
            subtitle_html = f'<h2 class="slide-subtitle">{subtitle}</h2>'
        
        # Process content
        content_html = self._process_content_html(slide_data.get('content', []))
        has_content = bool(slide_data.get('content'))
        
        # Process images
        images = slide_data.get('images', [])
        images_html = []
        if images:
            num_images = len(images)
            if not has_content:
                # No content, center images
                if num_images == 1:
                    image_data = self._image_to_base64(images[0])
                    if image_data:
                        images_html.append(f'<img src="{image_data}" class="slide-image" style="max-width: {int(9 * 96)}px; max-height: {int(4.5 * 96)}px;">')
                else:
                    for img_path in images:
                        image_data = self._image_to_base64(img_path)
                        if image_data:
                            images_html.append(f'<img src="{image_data}" class="slide-image" style="width: {int(4 * 96)}px; height: {int(3 * 96)}px;">')
            else:
                # Content exists, images on right
                for img_path in images:
                    image_data = self._image_to_base64(img_path)
                    if image_data:
                        images_html.append(f'<img src="{image_data}" class="slide-image" style="max-width: {int(4.5 * 96)}px; max-height: {int(5.5 * 96)}px;">')
        
        if not has_content:
            return f"""
            <div class="content-area">
                {title_html}
                {subtitle_html}
                <div style="display: flex; justify-content: center; align-items: center; flex: 1; min-height: 0;">
                    <div style="display: flex; gap: {int(0.5 * 96)}px; align-items: center;">
                        {''.join(images_html)}
                    </div>
                </div>
            </div>
            """
        
        return f"""
        <div class="content-area">
            {title_html}
            {subtitle_html}
            <div class="image-content-layout">
                <div class="content-column">
                    {content_html}
                </div>
                <div class="image-column">
                    {''.join(images_html)}
                </div>
            </div>
        </div>
        """
    
    def _create_table_slide_html(self, slide_data):
        """Create table slide HTML"""
        title_html = ""
        if slide_data.get('title'):
            title = self._process_text_formatting(slide_data['title'])
            title_html = f'<h1 class="slide-title">{title}</h1>'
        
        # Find table in content
        table_data = None
        for content_item in slide_data.get('content', []):
            if content_item['type'] == 'table':
                table_data = content_item
                break
        
        if not table_data:
            return f'<div class="content-area">{title_html}</div>'
        
        headers = table_data.get('headers')
        rows = table_data.get('rows', [])
        
        table_html = '<table class="slide-table"><thead><tr>'
        if headers:
            for header_cell in headers:
                header_text = header_cell.get('text', '') if isinstance(header_cell, dict) else str(header_cell)
                header_images = header_cell.get('images', []) if isinstance(header_cell, dict) else []
                cell_content = self._process_text_formatting(header_text) if header_text else ""
                if header_images:
                    for img_path in header_images:
                        image_data = self._image_to_base64(img_path)
                        if image_data:
                            cell_content += f'<div class="cell-image"><img src="{image_data}" class="slide-image" style="max-width: 100px; max-height: 60px;"></div>'
                table_html += f'<th>{cell_content}</th>'
        table_html += '</tr></thead><tbody>'
        
        for row_data in rows:
            table_html += '<tr>'
            for cell_data in row_data:
                cell_text = cell_data.get('text', '') if isinstance(cell_data, dict) else str(cell_data)
                cell_images = cell_data.get('images', []) if isinstance(cell_data, dict) else []
                cell_content = self._process_text_formatting(cell_text) if cell_text else ""
                if cell_images:
                    for img_path in cell_images:
                        image_data = self._image_to_base64(img_path)
                        if image_data:
                            cell_content += f'<div class="cell-image"><img src="{image_data}" class="slide-image" style="max-width: 100px; max-height: 60px;"></div>'
                table_html += f'<td>{cell_content}</td>'
            table_html += '</tr>'
        
        table_html += '</tbody></table>'
        
        return f"""
        <div class="content-area">
            {title_html}
            <div class="table-scroll-container">
                {table_html}
            </div>
        </div>
        """
    
    def _create_code_slide_html(self, slide_data):
        """Create code slide HTML"""
        title_html = ""
        if slide_data.get('title'):
            title = self._process_text_formatting(slide_data['title'])
            title_html = f'<h1 class="slide-title">{title}</h1>'
        
        # Find code block
        code_text = None
        for content_item in slide_data.get('content', []):
            if content_item['type'] == 'code':
                code_text = content_item['text']
                break
        
        if not code_text:
            return f'<div class="content-area">{title_html}</div>'
        
        # Escape HTML in code
        code_text = code_text.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')
        
        return f"""
        <div class="content-area">
            {title_html}
            <div class="code-block-container">
                <pre class="slide-code">{code_text}</pre>
            </div>
        </div>
        """
    
    def _create_bullet_slide_html(self, slide_data):
        """Create bullet slide HTML"""
        title_html = ""
        if slide_data.get('title'):
            title = self._process_text_formatting(slide_data['title'])
            title_html = f'<h1 class="slide-title">{title}</h1>'
        
        subtitle_html = ""
        if slide_data.get('subtitle'):
            subtitle = self._process_text_formatting(slide_data['subtitle'])
            subtitle_html = f'<h2 class="slide-subtitle">{subtitle}</h2>'
        
        content_html = self._process_content_html(slide_data.get('content', []))
        
        return f"""
        <div class="content-area">
            {title_html}
            {subtitle_html}
            <div class="bullet-list-container">
                {content_html}
            </div>
        </div>
        """
    
    def _create_title_subtitle_slide_html(self, slide_data):
        """Create title-subtitle slide HTML"""
        title = self._process_text_formatting(slide_data['title'] or '')
        subtitle = self._process_text_formatting(slide_data['subtitle'] or '')
        
        content_html = self._process_content_html(slide_data.get('content', []))
        
        return f"""
        <div class="content-area" style="display: flex; flex-direction: column; justify-content: center; align-items: center;">
            <div style="flex-shrink: 0;">
                <h1 class="slide-title text-center">{title}</h1>
                <h2 class="slide-subtitle text-center">{subtitle}</h2>
            </div>
            <div style="overflow-y: auto; width: 100%; text-align: center; flex: 1; min-height: 0;">
                {content_html}
            </div>
        </div>
        """
    
    def _create_title_content_slide_html(self, slide_data):
        """Create title-content slide HTML"""
        title_html = ""
        if slide_data.get('title'):
            title = self._process_text_formatting(slide_data['title'])
            title_html = f'<h1 class="slide-title">{title}</h1>'
        
        subtitle_html = ""
        if slide_data.get('subtitle'):
            subtitle = self._process_text_formatting(slide_data['subtitle'])
            subtitle_html = f'<h2 class="slide-subtitle">{subtitle}</h2>'
        
        content_html = self._process_content_html(slide_data.get('content', []))
        
        return f"""
        <div class="content-area">
            {title_html}
            {subtitle_html}
            <div class="text-content-scroll-wrapper" style="flex: 1; min-height: 0; overflow-y: auto;">
                {content_html}
            </div>
        </div>
        """
    
    def _process_content_html(self, content_items):
        """Process content items into HTML"""
        html_parts = []
        
        for item in content_items:
            if item['type'] == 'paragraph':
                text = self._process_text_formatting(item['text'])
                html_parts.append(f'<p class="slide-body">{text}</p>')
            
            elif item['type'] == 'bullets':
                html_parts.append('<ul style="list-style: none; padding-left: 0;">')
                for bullet_item in item['items']:
                    if isinstance(bullet_item, dict):
                        text = bullet_item['text']
                        level = bullet_item.get('level', 0)
                    else:
                        text = str(bullet_item)
                        level = 0
                    
                    text = self._process_text_formatting(text)
                    level_class = f"level-{level}" if level > 0 else ""
                    html_parts.append(f'<li class="bullet-item slide-bullet {level_class}">• {text}</li>')
                html_parts.append('</ul>')
            
            elif item['type'] == 'numbered':
                html_parts.append('<ol style="padding-left: 20px;">')
                for i, numbered_item in enumerate(item['items'], start=1):
                    if isinstance(numbered_item, dict):
                        text = numbered_item['text']
                    else:
                        text = str(numbered_item)
                    
                    text = self._process_text_formatting(text)
                    html_parts.append(f'<li class="numbered-item slide-body">{text}</li>')
                html_parts.append('</ol>')
            
            elif item['type'] == 'heading3':
                text = self._process_text_formatting(item['text'])
                html_parts.append(f'<h3 class="slide-body" style="font-weight: bold; font-size: {self.theme["typography"]["sizes"]["body"] + 2}pt; margin-bottom: 6pt;">{text}</h3>')
        
        return ''.join(html_parts)


def generate_html(md_file, output_file, theme_file=None):
    """
    Generate standalone HTML presentation from Markdown file.
    
    Args:
        md_file: Path to markdown file
        output_file: Path for output HTML file
        theme_file: Optional path to theme JSON file
    """
    generator = HTMLGenerator(md_file, output_file, theme_file)
    generator.generate()

