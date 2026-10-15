# Chat Agent Bridge app icon

Selected concept C: a blue chat bubble with a white terminal chevron and cyan underscore.
The outer white presentation tile was removed and the background made transparent so the symbol remains large at taskbar sizes.

`chat-agent-bridge-icon.png` is the image-generation master. Keep its alpha channel.
Run `desktop/ExportAppIcon.ps1` on Windows to export the PNG sizes and multi-resolution ICO consumed by the desktop app.
The title bar uses PNG; the executable and native window use ICO. The package manifest already references the exported Store/tile assets.

Generated with the built-in image_gen tool, using the previously selected concept C as the edit target.
Mattermost Deck informed the blue/cyan/white palette and restrained utility-icon mood, rather than the specific composition.

## Final editing prompt

Latest framing revision: preserve the existing blue bubble, white chevron and cyan underscore. Enlarge the visible silhouette to 97% of canvas width, with only 1.5% transparent margins at the sides, vertically centered at its natural aspect ratio. No added tile, background, shadow or decoration, no clipped tail and no stretching. Generated with the built-in image_gen tool; all Windows derivatives were exported again from this revised master.

## Initial background-removal prompt

Edit the provided chosen application icon C. Preserve its exact design identity: solid blue rounded rectangular chat bubble with lower-left tail, thick white terminal chevron and cyan underscore, same geometry and relative positions. Remove ALL the outer white rounded-square tile AND the pale gray exterior completely, leaving genuine alpha transparency outside the blue chat bubble. Reframe and enlarge the mark to occupy 90% of square canvas width, centered optically, only 5% transparent inset left and right; natural top and bottom inset matching its original aspect ratio. Do not add a new enclosing tile, white frame, border, backdrop, shadow, glow or decoration. Flatten color fills to clean uniform cobalt blue, white and cyan, eliminating subtle shading/textures. This is a tightly fitted production app icon, not a presentation mockup. Keep the white chevron inside the blue shape opaque white. Only remove the exterior/background and reduce margins; do not redesign the symbol.
