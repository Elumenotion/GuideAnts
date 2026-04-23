"""
Minimal test: Create a PPTX with just one hyperlink to isolate the issue.
"""
from pptx import Presentation
from pptx.util import Inches, Pt

def test_minimal():
    """Create minimal PPTX with one hyperlink."""
    prs = Presentation()
    prs.slide_width = Inches(13.33)
    prs.slide_height = Inches(7.5)
    
    # Add one slide with one hyperlink
    slide = prs.slides.add_slide(prs.slide_layouts[6])  # Blank layout
    
    # Add a text box
    txBox = slide.shapes.add_textbox(Inches(1), Inches(1), Inches(8), Inches(1))
    tf = txBox.text_frame
    tf.word_wrap = True
    
    p = tf.paragraphs[0]
    
    # Add plain text first
    run1 = p.add_run()
    run1.text = "Click here: "
    run1.font.size = Pt(24)
    
    # Add hyperlink text
    run2 = p.add_run()
    run2.text = "Aquarium Co-op Setup Guide"
    run2.font.size = Pt(24)
    
    # Set hyperlink
    print(f"Setting hyperlink...")
    run2.hyperlink.address = "https://www.aquariumcoop.com/blogs/aquarium/how-to-set-up-a-fish-tank"
    print(f"Hyperlink set!")
    
    # Save
    output_path = "test_minimal_hyperlink.pptx"
    prs.save(output_path)
    print(f"\nSaved to: {output_path}")
    print("Please try opening this file in PowerPoint.")

if __name__ == "__main__":
    test_minimal()



