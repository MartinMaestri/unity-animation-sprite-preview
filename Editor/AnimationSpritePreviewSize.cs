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
    // Dopesheet/Curves toggles.
    [InitializeOnLoad]
    internal static class AnimationSpritePreviewSize
    {
        private const string PrefKey = "MartinMaestri.AnimationSpritePreview.RowHeight";
        private const float DefaultHeight = 96f;
        private const float MinHeight = 36f;
        private const float MaxHeight = 200f;
        private const string OverlayName = "MartinMaestri.AnimationSpritePreview.Overlay";
        private const string SliderName = "MartinMaestri.AnimationSpritePreview.Slider";
        private const float SliderMaxWidth = 170f;
        private const float SliderMinWidth = 70f;
        // Two AnimEditor.kToggleButtonWidth buttons (Dopesheet, Curves) on the right of that toolbar.
        private const float ToolbarTogglesWidth = 2f * 80f;
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly Color SelectedOutlineColor = new Color32(87, 133, 217, 255);
        private static readonly Color PlayheadColor = new Color(1f, 1f, 1f, 0.9f);
        private static readonly Color RecordingPlayheadColor = new Color(1f, 0.25f, 0.25f, 0.9f);

        private static double nextWindowScan;
        private static FieldInfo elementPosition;
        private static FieldInfo elementColor;
        private static FieldInfo elementTexture;

        static AnimationSpritePreviewSize()
        {
            Apply(EditorPrefs.GetFloat(PrefKey, DefaultHeight));
            EditorApplication.update += AttachOverlays;
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

                var slider = new Slider("Sprites", MinHeight, MaxHeight)
                {
                    name = SliderName,
                    tooltip = "Size of the sprite keyframe previews in the dopesheet",
                    value = EditorPrefs.GetFloat(PrefKey, DefaultHeight),
                };
                slider.labelElement.style.minWidth = 0;
                slider.style.position = Position.Absolute;
                slider.style.bottom = 1;
                slider.style.left = 4;
                slider.style.display = DisplayStyle.None;
                slider.RegisterValueChangedCallback(evt => Apply(Mathf.Round(evt.newValue)));

                var overlay = new IMGUIContainer(() => DrawOverlay(window, slider))
                {
                    name = OverlayName,
                    pickingMode = PickingMode.Ignore,
                };
                overlay.StretchToParentSize();
                root.Add(overlay);
                root.Add(slider);
            }
        }

        private static void DrawOverlay(AnimationWindow window, Slider slider)
        {
            if (Event.current.type != EventType.Repaint || window == null)
                return;

            // Shown only while the dopesheet is, sized to the room the hierarchy splitter leaves beside
            // the Dopesheet/Curves toggles (re-read every repaint).
            bool dopeSheetShown = PlaceSlider(window, slider);
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

            object unselected = Get(pointRenderer, "m_UnselectedKeysDrawBuffer");
            object selected = Get(pointRenderer, "m_SelectedKeysDrawBuffer");
            object dragDrop = Get(pointRenderer, "m_DragDropKeysDrawBuffer");

            // All backgrounds first, then all sprites: drawing them per key would let a key's opaque
            // background hide the transparent pixels of an overlapping neighbor's sprite.
            bool drewAny = DrawKeys(unselected, false, true);
            drewAny |= DrawKeys(selected, true, true);
            drewAny |= DrawKeys(dragDrop, false, true);
            DrawKeys(unselected, false, false);
            DrawKeys(selected, true, false);
            DrawKeys(dragDrop, false, false);

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

        private static bool PlaceSlider(AnimationWindow window, Slider slider)
        {
            object animEditor = Get(window, "animEditor");
            object state = Get(animEditor, "state");
            bool dopeSheetShown = state != null && Get(state, "showCurveEditor") is false && Get(state, "disabled") is false
                && Get(Get(animEditor, "dopeSheetEditor"), "drawRect") is Rect;

            // The dopesheet starts right where the hierarchy pane ends.
            float hierarchyWidth = dopeSheetShown ? ((Rect)Get(Get(animEditor, "dopeSheetEditor"), "drawRect")).x : 0f;
            float width = Mathf.Min(SliderMaxWidth, hierarchyWidth - ToolbarTogglesWidth - 12f);
            bool sliderShown = dopeSheetShown && width >= SliderMinWidth;

            DisplayStyle display = sliderShown ? DisplayStyle.Flex : DisplayStyle.None;
            if (slider.style.display != display)
                slider.style.display = display;
            if (sliderShown && !Mathf.Approximately(slider.style.width.value.value, width))
                slider.style.width = width;

            return dopeSheetShown;
        }

        private static bool DrawKeys(object buffer, bool selected, bool backgroundPass)
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

                Rect rect = (Rect)elementPosition.GetValue(element);

                // Hide Unity's thumbnail, which is drawn at the preview's native size in the corner.
                if (backgroundPass)
                {
                    EditorGUI.DrawRect(rect, background);
                    continue;
                }

                if (texture.filterMode != FilterMode.Point)
                    texture.filterMode = FilterMode.Point;
                GUI.color = (Color)elementColor.GetValue(element);
                GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit);
                GUI.color = oldColor;

                if (selected)
                    DrawOutline(rect, SelectedOutlineColor);
            }

            return true;
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
