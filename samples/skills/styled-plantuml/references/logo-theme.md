# Logo theme — worked example (supplement to SKILL.md)

The theme's colors and skinparam line live **in SKILL.md** as a verbatim
block. The instruction to the Diagrams assistant contains the diagram
**structure** (element names, types, groupings, arrows) and the **verbatim
theme block** — nothing else. Element colors are never written in the
description body; the theme block's hierarchy does all the coloring.

## Worked example

```
Create a PlantUML component diagram, top to bottom direction, as an SVG
file named diagram_styling_pipeline.svg.

Title: "PlantUML Diagram Styling Pipeline (BF16)"

Layout: 3 packages stacked vertically; elements flow left to right inside
each package. Compact, roughly 2-3:1 overall shape.

Package "1. Create":
- Actor "User" : describes diagram in detail
- Component "Diagrams Assistant" : generates themed SVG
Arrow: User -> Diagrams Assistant

Package "2. Prep":
- Component "Split SVG" : chrome + text layers
- Component "Rasterize 2x" : opaque + alpha PNG
Arrow: Split SVG -> Rasterize 2x

Package "3. Style":
- Component "Comfy BF16 Restyle" : chrome only, no mask
- Component "FFmpeg Overlay" : text on top
- Component "Styled PNG" : video-ready
Arrows: Comfy BF16 Restyle -> FFmpeg Overlay, FFmpeg Overlay -> Styled PNG

Arrows between packages:
- Diagrams Assistant -> Split SVG : themed SVG
- Rasterize 2x -> Comfy BF16 Restyle : chrome-2x.png
- Rasterize 2x -> FFmpeg Overlay : text-2x.png

THEME (apply exactly these colors, no others):
Theme: GuideAnts logo theme. Apply exactly these colors, no others:
- All borders/outline color: #CC0000 (deep red)
- All text/font color: #990000 (dark red)
- Actors, key components, and final output elements: background #FFD700 (gold)
- Standard components/rectangles/classes: background #FFF8DC (cream)
- Packages/group containers: background #FFE4B5 (peach)
- All arrows/connectors: #FF8C00 (orange), thickness 2
- Arrow labels: #990000, small font
Visual hierarchy: gold elements stand out (actors, key nodes, the final
output); cream is the readable default for most components; peach groups
logical layers; deep-red borders and dark-red text give structure; orange
arrows carry the flow.
skinparam: ArrowColor #FF8C00, ArrowThickness 2, BorderColor #CC0000,
FontColor #990000, PackageBackgroundColor #FFE4B5, PackageBorderColor
#CC0000, RectangleBackgroundColor #FFF8DC, RectangleBorderColor #CC0000,
NoteBackgroundColor #FFD700, NoteBorderColor #CC0000, NoteFontColor #990000.

Output must be SVG, file name diagram_styling_pipeline.svg.
```

Notice: no hex code anywhere in the structure section. The "Styled PNG"
component stands out because the hierarchy gives final-output elements gold;
the description only says what it is.

## After generation

Show the SVG to the user and stop. There is no post-generation checklist.
The user's review is the only check.
