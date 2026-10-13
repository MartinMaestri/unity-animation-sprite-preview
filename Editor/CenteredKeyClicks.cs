using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace MartinMaestri.AnimationSpritePreview
{
    // With Center on, sprites are drawn half a key to the left of where DopeSheetEditor hit-tests them
    // (GetKeyframeRect starts at the keyframe), so clicking the left half of a sprite would miss it and
    // clicking just right of it would pick a key that is no longer drawn there. The window's OnGUI, as
    // its HostView calls it (HostView.m_OnGUI, rebuilt by Unity whenever the window is docked, focused
    // as a tab or reloaded, hence re-wrapped every editor update), is wrapped so that before the
    // dopesheet sees a mouse event the mouse is moved (through GUI.matrix) from where a key is drawn to
    // the same spot inside the rect Unity tests. Drags keep the offset of the mouse down that started them.
    internal sealed class CenteredKeyClicks
    {
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly FieldInfo ParentField = typeof(EditorWindow).GetField("m_Parent", AnyInstance);
        private static readonly MethodInfo WrapperMethod = typeof(CenteredKeyClicks).GetMethod(nameof(OnGUI), AnyInstance);
        private static readonly Dictionary<AnimationWindow, CenteredKeyClicks> ByWindow = new Dictionary<AnimationWindow, CenteredKeyClicks>();
        private static readonly List<AnimationWindow> Closed = new List<AnimationWindow>();

        private readonly AnimationWindow window;
        // Window space, in draw order, as of the last repaint: where each key's sprite is drawn and
        // where Unity hit-tests that same key.
        private readonly List<Rect> drawnKeys = new List<Rect>();
        private readonly List<Rect> hitKeys = new List<Rect>();
        private Rect dopeSheetArea;
        private Action innerOnGUI;
        private Delegate wrapper;
        private float dragOffset;
        private bool dragging;

        private CenteredKeyClicks(AnimationWindow window)
        {
            this.window = window;
        }

        public static CenteredKeyClicks For(AnimationWindow window)
        {
            if (!ByWindow.TryGetValue(window, out CenteredKeyClicks clicks))
            {
                clicks = new CenteredKeyClicks(window);
                ByWindow.Add(window, clicks);
            }
            return clicks;
        }

        public static void KeepHooked()
        {
            foreach (KeyValuePair<AnimationWindow, CenteredKeyClicks> entry in ByWindow)
            {
                if (entry.Key == null)
                    Closed.Add(entry.Key);
                else
                    entry.Value.Hook();
            }

            foreach (AnimationWindow window in Closed)
                ByWindow.Remove(window);
            Closed.Clear();
        }

        public void BeginFrame(Rect dopeSheetAreaInWindow)
        {
            dopeSheetArea = dopeSheetAreaInWindow;
            drawnKeys.Clear();
            hitKeys.Clear();
        }

        public void AddKey(Rect drawn, Rect hit)
        {
            drawnKeys.Add(drawn);
            hitKeys.Add(hit);
        }

        private void Hook()
        {
            object parent = ParentField?.GetValue(window);
            if (parent == null || !ReferenceEquals(GetMember(parent, "actualView"), window))
                return;

            FieldInfo onGUIField = FindField(parent.GetType(), "m_OnGUI");
            if (!(onGUIField?.GetValue(parent) is Delegate current) || ReferenceEquals(current, wrapper))
                return;

            innerOnGUI = (Action)Delegate.CreateDelegate(typeof(Action), current.Target, current.Method);
            if (wrapper == null || wrapper.GetType() != onGUIField.FieldType)
                wrapper = Delegate.CreateDelegate(onGUIField.FieldType, this, WrapperMethod);
            onGUIField.SetValue(parent, wrapper);
        }

        private void OnGUI()
        {
            Event evt = Event.current;
            float offset = evt != null ? MouseOffset(evt) : 0f;
            if (offset == 0f)
            {
                innerOnGUI();
                return;
            }

            // Setting Event.mousePosition would not stick: IMGUI re-derives it, on every GUIClip push,
            // from the mouse position it captured when the GUI pass began, so the dopesheet's own clip
            // would undo it. The GUI matrix is honored through every clip (it is how scaled GUI keeps
            // clicks on target), and only mouse events get here, so nothing is drawn shifted.
            Matrix4x4 matrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Translate(new Vector3(-offset, 0f, 0f)) * matrix;
            try
            {
                innerOnGUI();
            }
            finally
            {
                GUI.matrix = matrix;
            }
        }

        private float MouseOffset(Event evt)
        {
            switch (evt.type)
            {
                case EventType.MouseDown:
                    dragOffset = AnimationSpritePreviewSize.Centered ? OffsetAt(evt.mousePosition) : 0f;
                    dragging = dragOffset != 0f;
                    return dragOffset;
                case EventType.MouseDrag:
                    return dragging ? dragOffset : 0f;
                case EventType.MouseUp:
                    float offset = dragging ? dragOffset : 0f;
                    dragging = false;
                    return offset;
                case EventType.ContextClick:
                    return AnimationSpritePreviewSize.Centered ? OffsetAt(evt.mousePosition) : 0f;
                default:
                    return 0f;
            }
        }

        private float OffsetAt(Vector2 mouse)
        {
            if (!dopeSheetArea.Contains(mouse))
                return 0f;

            // Topmost drawn sprite under the mouse wins, as it is the one the user sees.
            for (int i = drawnKeys.Count - 1; i >= 0; i--)
            {
                if (drawnKeys[i].Contains(mouse))
                    return hitKeys[i].x - drawnKeys[i].x;
            }

            // Over no sprite: if Unity still has a key there (the right half of where a centered
            // sprite used to start), step past it so the click lands on empty dopesheet, as it looks.
            float x = mouse.x;
            for (int pass = 0; pass <= hitKeys.Count; pass++)
            {
                int hit = HitKeyAt(new Vector2(x, mouse.y));
                if (hit < 0)
                    break;
                x = hitKeys[hit].xMax + 0.5f;
            }
            return x - mouse.x;
        }

        private int HitKeyAt(Vector2 point)
        {
            for (int i = 0; i < hitKeys.Count; i++)
            {
                if (hitKeys[i].Contains(point))
                    return i;
            }
            return -1;
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (; type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, AnyInstance | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }
            return null;
        }

        private static object GetMember(object target, string name)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                PropertyInfo property = type.GetProperty(name, AnyInstance | BindingFlags.DeclaredOnly);
                if (property != null)
                    return property.GetValue(target);
            }
            return null;
        }
    }
}
