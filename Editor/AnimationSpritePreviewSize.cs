using System.Collections;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinMaestri.AnimationSpritePreview
{
    // The Animation window draws sprite keyframe thumbnails as tall as the expanded dopesheet row,
    // which Unity hardcodes to 36 px (AnimationWindowHierarchyGUI.k_DopeSheetRowHeightTall), so pixel
    // art is unreadable and zooming only stretches the time axis. The field is static readonly rather
    // than const, so it is overwritten here by reflection on every domain reload.
    //
    // Taller rows alone stop helping past ~64 px: the dopesheet draws each thumbnail with
    // GUI.Label + GUIStyle.none, which never upscales the AssetPreview texture. So an IMGUI overlay is
    // added to every Animation window that, after the dopesheet repaints, reads the thumbnails it just
    // queued (DopeSheetEditor.m_PointRenderer buffers) and redraws them scaled to fill the key rect.
    // The row height is set from a slider the overlay places in the bottom-left toolbar, next to the
    // Dopesheet/Curves toggles, beside a toggle that centers each sprite on its keyframe instead of
    // starting it there (Unity's left anchor); CenteredKeyClicks keeps clicks on what is drawn then.
    [InitializeOnLoad]
    internal static class AnimationSpritePreviewSize
    {
        private const string PrefKey = "MartinMaestri.AnimationSpritePreview.RowHeight";
        private const string CenteredPrefKey = "MartinMaestri.AnimationSpritePreview.Centered";
        private const float DefaultHeight = 96f;
        private const float MinHeight = 36f;
        private const float MaxHeight = 200f;
        private const string OverlayName = "MartinMaestri.AnimationSpritePreview.Overlay";
        private const string BarName = "MartinMaestri.AnimationSpritePreview.Bar";
        private const string SliderName = "MartinMaestri.AnimationSpritePreview.Slider";
        private const string CenterToggleName = "MartinMaestri.AnimationSpritePreview.CenterToggle";
        private const string CenterMenuItem = "Center sprites on keyframes";
        private const float BarMaxWidth = 240f;
        // Room needed, as the pane narrows: label + slider + toggle, then slider + toggle, then the
        // bare slider (the toggle stays reachable from the slider's context menu).
        private const float FullBarMinWidth = 180f;
        private const float NoLabelBarMinWidth = 116f;
        private const float SliderOnlyMinWidth = 50f;
        // Two AnimEditor.kToggleButtonWidth buttons (Dopesheet, Curves) on the right of that toolbar.
        private const float ToolbarTogglesWidth = 2f * 80f;
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly Color SelectedOutlineColor = new Color32(87, 133, 217, 255);
        private static readonly Color PlayheadColor = new Color(1f, 1f, 1f, 0.9f);
        private static readonly Color RecordingPlayheadColor = new Color(1f, 0.25f, 0.25f, 0.9f);

        private static double nextWindowScan;
        private static bool centered;
        private static FieldInfo elementPosition;
        private static FieldInfo elementColor;
        private static FieldInfo elementTexture;

        internal static bool Centered => centered;

        static AnimationSpritePreviewSize()
        {
            centered = EditorPrefs.GetBool(CenteredPrefKey, false);
            Apply(EditorPrefs.GetFloat(PrefKey, DefaultHeight));
            EditorApplication.update += AttachOverlays;
            EditorApplication.update += CenteredKeyClicks.KeepHooked;
        }

        private static void Apply(float height)
        {
            FieldInfo field = typeof(Editor).Assembly
                .GetType("UnityEditorInternal.AnimationWindowHierarchyGUI")
                ?.GetField("k_DopeSheetRowHeightTall", BindingFlags.Public | BindingFlags.Static);
            if (field == null)
            {
                Debug.LogWarning("AnimationWindowHierarchyGUI.k_DopeSheetRowHeightTall not found (Unity version changed?)");
                return;
            }

            field.SetValue(null, height);
            EditorPrefs.SetFloat(PrefKey, height);

            foreach (AnimationWindow window in Resources.FindObjectsOfTypeAll<AnimationWindow>())
            {
                window.rootVisualElement.Q<Slider>(SliderName)?.SetValueWithoutNotify(height);
                InvalidateHierarchyRowPositions(window);
            }
            InternalEditorUtility.RepaintAllViews();
        }

        private static void SetCentered(bool value)
        {
            centered = value;
            EditorPrefs.SetBool(CenteredPrefKey, value);

            foreach (AnimationWindow window in Resources.FindObjectsOfTypeAll<AnimationWindow>())
                window.rootVisualElement.Q<Toggle>(CenterToggleName)?.SetValueWithoutNotify(value);
            InternalEditorUtility.RepaintAllViews();
        }

        // AnimationWindowHierarchyGUI.GetRowRect caches each row's top in AnimationWindowHierarchyNode.topPixel
        // and only drops it when the rows are rebuilt, so the property list on the left would keep the
        // old row positions while the dopesheet already uses the new height.
        private static void InvalidateHierarchyRowPositions(AnimationWindow window)
        {
            object hierarchyData = Get(Get(Get(window, "animEditor"), "state"), "hierarchyData");
            if (!(hierarchyData?.GetType().GetMethod("GetRows", AnyInstance, null, System.Type.EmptyTypes, null)?.Invoke(hierarchyData, null) is IList rows))
                return;

            foreach (object row in rows)
                row?.GetType().GetField("topPixel", AnyInstance)?.SetValue(row, null);
        }

        private static void AttachOverlays()
        {
            if (EditorApplication.timeSinceStartup < nextWindowScan)
                return;
            nextWindowScan = EditorApplication.timeSinceStartup + 1.0;

            foreach (AnimationWindow window in Resources.FindObjectsOfTypeAll<AnimationWindow>())
            {
                VisualElement root = window.rootVisualElement;
                if (root.Q(OverlayName) != null)
                    continue;

                var bar = new VisualElement { name = BarName };
                bar.style.position = Position.Absolute;
                bar.style.bottom = 1;
                bar.style.left = 4;
                bar.style.flexDirection = FlexDirection.Row;
                bar.style.display = DisplayStyle.None;

                var slider = new Slider("Sprite Size", MinHeight, MaxHeight)
                {
                    name = SliderName,
                    tooltip = "Sprite Size: height of the sprite keyframe previews in the dopesheet",
                    value = EditorPrefs.GetFloat(PrefKey, DefaultHeight),
                };
                slider.labelElement.style.minWidth = 0;
                slider.style.flexGrow = 1;
                slider.style.flexShrink = 1;
                slider.RegisterValueChangedCallback(evt => Apply(Mathf.Round(evt.newValue)));
                // Reachable from here too when the pane is too narrow to show the toggle.
                slider.AddManipulator(new ContextualMenuManipulator(evt => evt.menu.AppendAction(
                    CenterMenuItem,
                    _ => SetCentered(!centered),
                    _ => centered ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal)));

                var centerToggle = new Toggle
                {
                    name = CenterToggleName,
                    text = "Center",
                    tooltip = "Center each sprite on its keyframe instead of starting it there",
                    value = centered,
                };
                centerToggle.style.flexShrink = 0;
                centerToggle.style.marginLeft = 6;
                centerToggle.RegisterValueChangedCallback(evt => SetCentered(evt.newValue));

                bar.Add(slider);
                bar.Add(centerToggle);

                CenteredKeyClicks clicks = CenteredKeyClicks.For(window);
                var overlay = new IMGUIContainer(() => DrawOverlay(window, clicks, bar, slider, centerToggle))
                {
                    name = OverlayName,
                    pickingMode = PickingMode.Ignore,
                };
                overlay.StretchToParentSize();
                root.Add(overlay);
                root.Add(bar);
            }
        }

        private static void DrawOverlay(AnimationWindow window, CenteredKeyClicks clicks, VisualElement bar, Slider slider, Toggle centerToggle)
        {
            if (Event.current.type != EventType.Repaint || window == null)
                return;

            // Shown only while the dopesheet is, sized to the room the hierarchy splitter leaves beside
            // the Dopesheet/Curves toggles (re-read every repaint).
            bool dopeSheetShown = PlaceControls(window, bar, slider, centerToggle);
            clicks.BeginFrame(Rect.zero);
            if (!dopeSheetShown)
                return;

            object animEditor = Get(window, "animEditor");
            object state = Get(animEditor, "state");
            object dopeSheet = Get(animEditor, "dopeSheetEditor");
            object pointRenderer = Get(dopeSheet, "m_PointRenderer");
            if (pointRenderer == null || !(Get(dopeSheet, "drawRect") is Rect area) || !(Get(Get(state, "hierarchyState"), "scrollPos") is Vector2 scroll))
                return;

            // Same clip DopeSheetEditor.OnGUI pushes, so buffered key rects land where Unity drew them.
            GUI.BeginClip(area, -scroll, Vector2.zero, false);
            clicks.BeginFrame(area);
            Vector2 toWindow = area.position - scroll;

            object unselected = Get(pointRenderer, "m_UnselectedKeysDrawBuffer");
            object selected = Get(pointRenderer, "m_SelectedKeysDrawBuffer");
            object dragDrop = Get(pointRenderer, "m_DragDropKeysDrawBuffer");

            // All backgrounds first, then all sprites: drawing them per key would let a key's opaque
            // background hide the transparent pixels of an overlapping neighbor's sprite.
            bool drewAny = DrawKeys(unselected, false, true, null, toWindow);
            drewAny |= DrawKeys(selected, true, true, null, toWindow);
            drewAny |= DrawKeys(dragDrop, false, true, null, toWindow);
            DrawKeys(unselected, false, false, clicks, toWindow);
            DrawKeys(selected, true, false, clicks, toWindow);
            DrawKeys(dragDrop, false, false, null, toWindow);

            // The opaque thumbnails cover the window's playhead, so redraw it on top.
            if (drewAny && Get(state, "currentTime") is float time)
            {
                MethodInfo timeToPixel = state.GetType().GetMethod("TimeToPixel", AnyInstance, null, new[] { typeof(float) }, null);
                if (timeToPixel != null)
                {
                    float x = (float)timeToPixel.Invoke(state, new object[] { time });
                    Color color = Get(state, "recording") is true ? RecordingPlayheadColor : PlayheadColor;
                    EditorGUI.DrawRect(new Rect(x, scroll.y, 1f, area.height), color);
                }
            }

            GUI.EndClip();
        }

        private static bool PlaceControls(AnimationWindow window, VisualElement bar, Slider slider, Toggle centerToggle)
        {
            object animEditor = Get(window, "animEditor");
            object state = Get(animEditor, "state");
            bool dopeSheetShown = state != null && Get(state, "showCurveEditor") is false && Get(state, "disabled") is false
                && Get(Get(animEditor, "dopeSheetEditor"), "drawRect") is Rect;

            // The dopesheet starts right where the hierarchy pane ends.
            float hierarchyWidth = dopeSheetShown ? ((Rect)Get(Get(animEditor, "dopeSheetEditor"), "drawRect")).x : 0f;
            float width = Mathf.Min(BarMaxWidth, hierarchyWidth - ToolbarTogglesWidth - 12f);
            bool barShown = dopeSheetShown && width >= SliderOnlyMinWidth;

            SetDisplay(bar, barShown);
            SetDisplay(slider.labelElement, width >= FullBarMinWidth);
            SetDisplay(centerToggle, width >= NoLabelBarMinWidth);
            if (barShown && !Mathf.Approximately(bar.style.width.value.value, width))
                bar.style.width = width;

            return dopeSheetShown;
        }

        private static void SetDisplay(VisualElement element, bool shown)
        {
            DisplayStyle display = shown ? DisplayStyle.Flex : DisplayStyle.None;
            if (element.style.display != display)
                element.style.display = display;
        }

        // clicks: where to record each drawn key for click remapping (null for drag-and-drop ghosts).
        private static bool DrawKeys(object buffer, bool selected, bool backgroundPass, CenteredKeyClicks clicks, Vector2 toWindow)
        {
            if (!(buffer is IList elements) || elements.Count == 0)
                return false;

            if (elementTexture == null)
            {
                System.Type elementType = elements[0].GetType();
                elementPosition = elementType.GetField("position");
                elementColor = elementType.GetField("color");
                elementTexture = elementType.GetField("texture");
            }

            Color background = EditorGUIUtility.isProSkin ? new Color(0.2f, 0.2f, 0.2f) : new Color(0.78f, 0.78f, 0.78f);
            Color oldColor = GUI.color;

            foreach (object element in elements)
            {
                if (!(elementTexture.GetValue(element) is Texture2D texture))
                    continue;

                // Unity's rect starts 1 px right of the keyframe's time (DopeSheetEditor: the pptr key
                // offset of -1 plus the preview's 2 px inset), so centering shifts it left by half its
                // width plus that pixel.
                Rect unityRect = (Rect)elementPosition.GetValue(element);
                Rect rect = unityRect;
                if (centered)
                    rect.x -= rect.width * 0.5f + 1f;

                // Hide Unity's thumbnail, which is drawn at the preview's native size in the corner of
                // its own rect, so when centered the background spans both rects.
                if (backgroundPass)
                {
                    EditorGUI.DrawRect(Rect.MinMaxRect(rect.xMin, rect.yMin, unityRect.xMax, rect.yMax), background);
                    continue;
                }

                if (texture.filterMode != FilterMode.Point)
                    texture.filterMode = FilterMode.Point;
                GUI.color = (Color)elementColor.GetValue(element);
                GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit);
                GUI.color = oldColor;

                if (selected)
                    DrawOutline(rect, SelectedOutlineColor);

                // Unity hit-tests the preview rect grown back by its 2 px inset (GetKeyframeRect).
                if (centered && clicks != null)
                    clicks.AddKey(Grow(rect, 2f, toWindow), Grow(unityRect, 2f, toWindow));
            }

            return true;
        }

        private static Rect Grow(Rect rect, float by, Vector2 offset)
        {
            return new Rect(rect.x - by + offset.x, rect.y - by + offset.y, rect.width + 2f * by, rect.height + 2f * by);
        }

        private static void DrawOutline(Rect rect, Color color)
        {
            EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMin, rect.width, 1f), color);
            EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMax - 1f, rect.width, 1f), color);
            EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMin, 1f, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - 1f, rect.yMin, 1f, rect.height), color);
        }

        private static object Get(object target, string memberName)
        {
            if (target == null)
                return null;

            for (System.Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(memberName, AnyInstance | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field.GetValue(target);

                PropertyInfo property = type.GetProperty(memberName, AnyInstance | BindingFlags.DeclaredOnly);
                if (property != null)
                    return property.GetValue(target);
            }

            return null;
        }
    }
}
