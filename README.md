# Animation Sprite Preview

Editor-only Unity package that makes sprite keyframes readable in the **Animation** window.

Unity draws sprite keyframe previews as tall as the expanded sprite row, which is hardcoded to 36 px, and zooming only stretches the time axis. With pixel art the frames become unreadable. This package adds a **Sprite Size** slider to the Animation window that sets that row height, redraws the previews scaled to fill it with point filtering, and can center each sprite on its keyframe.


## Install

Package Manager → **+** → **Add package from git URL…**

```
https://github.com/MartinMaestri/unity-animation-sprite-preview.git
```

Or add it to `Packages/manifest.json`:

```json
"com.martinmaestri.animation-sprite-preview": "https://github.com/MartinMaestri/unity-animation-sprite-preview.git#1.1.0"
```

Requires Unity 2021.3 or newer. Tested on Unity 6000.3.

## Usage

1. Open the Animation window on a clip with a sprite property.
2. Expand the sprite row with the arrow next to the property (Unity's "tall" row mode, the one that shows previews).
3. Drag the **Sprite Size** slider in the bottom-left toolbar, next to the **Dopesheet / Curves** toggles. It goes from 36 px (Unity's default) to 200 px.
4. Tick **Center** to center each sprite on its keyframe instead of starting it there (Unity's default, left-anchored). It's also in the slider's right-click menu.

Both settings are saved per machine (`EditorPrefs`) and apply to every open Animation window. The bar hides itself in Curves mode. When the left pane is too narrow it drops the slider label first, then the **Center** toggle, then hides; widen the pane to bring them back.

## How it works

It relies on Unity editor internals through reflection, so a future Unity version could break it. If that happens it logs a warning or simply stops drawing, it never touches your assets.

- The row height is `AnimationWindowHierarchyGUI.k_DopeSheetRowHeightTall`, a `static readonly` field, overwritten on every domain reload.
- Unity draws previews with `GUI.Label`, which never upscales a texture, so taller rows alone stop helping past the preview's native size. An IMGUI overlay added to each Animation window reads the previews the dopesheet just queued (`DopeSheetEditor.m_PointRenderer`) and redraws them scaled to fill each key, over an opaque background that hides Unity's smaller copy. Selected keys get a blue outline and the playhead is redrawn on top.
- The property list on the left caches each row's position (`AnimationWindowHierarchyNode.topPixel`); the cache is cleared whenever the size changes so it re-lays out right away.

## Known limitations

- The rectangle you drag to select keys is drawn under the enlarged previews.
- With **Center** on, clicking and dragging still hit keys where Unity places them (starting at the keyframe), so the left half of a centered sprite doesn't select it.
- The redrawn playhead is plain white (red while recording) and may not match Unity's exact color.

## License

MIT, see [LICENSE](LICENSE).
