# Changelog

## [1.1.0] - 2026-10-13

- The slider is now labeled **Sprite Size**.
- **Center** toggle next to the slider: centers each sprite preview on its keyframe instead of starting it there (Unity's left anchor). Saved per machine like the size; also available from the slider's right-click menu.
- When the left pane is too narrow, the bar drops the slider label first, then the toggle, then hides.

## [1.0.0] - 2026-10-13

- Slider in the Animation window (bottom-left toolbar, next to Dopesheet/Curves) that sets the height of expanded sprite rows, from 36 px (Unity's default) to 200 px. The value is saved per machine in `EditorPrefs`.
- Sprite keyframe previews are redrawn scaled to fill the row with point filtering, so pixel art stays crisp past the preview texture's native size.
- The property list on the left re-lays out immediately when the size changes.
