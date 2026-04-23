"""
PPTX Diagnostic Tool
Extracts and analyzes PPTX files to find corruption issues.
"""
import zipfile
import os
import sys
import xml.etree.ElementTree as ET
from xml.parsers.expat import ExpatError

def diagnose_pptx(pptx_path):
    """Analyze a PPTX file for corruption issues."""
    print(f"\n{'='*60}")
    print(f"Diagnosing: {pptx_path}")
    print('='*60)
    
    if not os.path.exists(pptx_path):
        print(f"ERROR: File not found: {pptx_path}")
        return
    
    # Create extraction directory
    extract_dir = pptx_path.replace('.pptx', '_extracted')
    
    try:
        # Extract the PPTX (it's a ZIP file)
        with zipfile.ZipFile(pptx_path, 'r') as zip_ref:
            zip_ref.extractall(extract_dir)
        print(f"\n✓ Extracted to: {extract_dir}")
    except zipfile.BadZipFile as e:
        print(f"ERROR: Invalid ZIP/PPTX file: {e}")
        return
    except Exception as e:
        print(f"ERROR extracting: {e}")
        return
    
    # List of files to check
    issues = []
    
    # Check all XML files
    print("\n--- Checking XML files ---")
    for root, dirs, files in os.walk(extract_dir):
        for filename in files:
            if filename.endswith('.xml') or filename.endswith('.rels'):
                filepath = os.path.join(root, filename)
                rel_path = os.path.relpath(filepath, extract_dir)
                
                try:
                    tree = ET.parse(filepath)
                    # print(f"  ✓ {rel_path}")
                except ET.ParseError as e:
                    print(f"  ✗ XML ERROR in {rel_path}: {e}")
                    issues.append(('XML Parse Error', rel_path, str(e)))
                except Exception as e:
                    print(f"  ✗ ERROR reading {rel_path}: {e}")
                    issues.append(('Read Error', rel_path, str(e)))
    
    # Check slide relationships specifically
    print("\n--- Checking slide relationships ---")
    slides_rels_dir = os.path.join(extract_dir, 'ppt', 'slides', '_rels')
    if os.path.exists(slides_rels_dir):
        for filename in os.listdir(slides_rels_dir):
            if filename.endswith('.rels'):
                filepath = os.path.join(slides_rels_dir, filename)
                check_relationships(filepath, issues)
    
    # Check for hyperlink issues in slides
    print("\n--- Checking hyperlinks in slides ---")
    slides_dir = os.path.join(extract_dir, 'ppt', 'slides')
    if os.path.exists(slides_dir):
        for filename in os.listdir(slides_dir):
            if filename.endswith('.xml') and filename.startswith('slide'):
                filepath = os.path.join(slides_dir, filename)
                check_slide_hyperlinks(filepath, slides_rels_dir, issues)
    
    # Summary
    print("\n" + "="*60)
    if issues:
        print(f"FOUND {len(issues)} ISSUE(S):")
        for issue_type, location, details in issues:
            print(f"\n  [{issue_type}]")
            print(f"  Location: {location}")
            print(f"  Details: {details}")
    else:
        print("No obvious issues found in XML structure.")
        print("The corruption might be in binary content or subtle XML issues.")
    
    print(f"\nExtracted files are in: {extract_dir}")
    print("You can manually inspect the XML files there.")
    print("="*60)
    
    return issues

def check_relationships(rels_file, issues):
    """Check a .rels file for issues."""
    try:
        tree = ET.parse(rels_file)
        root = tree.getroot()
        
        # Namespace for relationships
        ns = {'r': 'http://schemas.openxmlformats.org/package/2006/relationships'}
        
        rel_path = os.path.basename(rels_file)
        relationships = root.findall('.//{http://schemas.openxmlformats.org/package/2006/relationships}Relationship')
        
        seen_ids = set()
        for rel in relationships:
            rid = rel.get('Id')
            target = rel.get('Target')
            rel_type = rel.get('Type')
            
            # Check for duplicate IDs
            if rid in seen_ids:
                print(f"  ✗ DUPLICATE rId in {rel_path}: {rid}")
                issues.append(('Duplicate rId', rel_path, f"Duplicate ID: {rid}"))
            seen_ids.add(rid)
            
            # Check for hyperlink relationships
            if 'hyperlink' in (rel_type or '').lower():
                print(f"  Hyperlink: {rid} -> {target}")
                # Check if target URL looks valid
                if target and not target.startswith(('http://', 'https://', 'mailto:', '#')):
                    print(f"    ⚠ Unusual hyperlink target: {target}")
                    
    except Exception as e:
        print(f"  ✗ Error checking {rels_file}: {e}")
        issues.append(('Rels Check Error', rels_file, str(e)))

def check_slide_hyperlinks(slide_file, rels_dir, issues):
    """Check a slide XML for hyperlink issues."""
    try:
        with open(slide_file, 'r', encoding='utf-8') as f:
            content = f.read()
        
        slide_name = os.path.basename(slide_file)
        
        # Look for hlinkClick elements
        import re
        hlink_pattern = r'<a:hlinkClick[^>]*r:id="([^"]*)"[^>]*/>'
        matches = re.findall(hlink_pattern, content)
        
        if matches:
            print(f"\n  {slide_name}:")
            
            # Load corresponding rels file
            rels_file = os.path.join(rels_dir, slide_name + '.rels')
            rels_targets = {}
            if os.path.exists(rels_file):
                try:
                    tree = ET.parse(rels_file)
                    root = tree.getroot()
                    for rel in root.findall('.//{http://schemas.openxmlformats.org/package/2006/relationships}Relationship'):
                        rels_targets[rel.get('Id')] = rel.get('Target')
                except:
                    pass
            
            for rid in matches:
                target = rels_targets.get(rid, 'NOT FOUND!')
                status = '✓' if rid in rels_targets else '✗ MISSING'
                print(f"    {status} hlinkClick r:id=\"{rid}\" -> {target}")
                
                if rid not in rels_targets:
                    issues.append(('Missing Hyperlink Relationship', slide_name, f"r:id={rid} not found in rels"))
        
        # Also check for malformed XML patterns around hyperlinks
        # Look for any unusual characters in hyperlink-related content
        if 'hlinkClick' in content:
            # Check for unescaped ampersands in URLs (common issue)
            url_pattern = r'Target="([^"]*&[^"]*)"'
            url_matches = re.findall(url_pattern, content)
            for url in url_matches:
                if '&amp;' not in url and '&' in url:
                    print(f"    ⚠ Unescaped '&' in URL: {url[:50]}...")
                    issues.append(('Unescaped Ampersand', slide_name, f"URL contains unescaped &: {url[:100]}"))
                    
    except Exception as e:
        print(f"  ✗ Error checking {slide_file}: {e}")
        issues.append(('Slide Check Error', slide_file, str(e)))


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print("Usage: python diagnose_pptx.py <path_to_pptx_file>")
        print("\nThis tool extracts and analyzes PPTX files to find corruption issues.")
        sys.exit(1)
    
    diagnose_pptx(sys.argv[1])



