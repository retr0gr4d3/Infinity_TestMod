using UnityEngine;

namespace Infinity_TestMod.Util
{
    public static class ResizableWindow
    {
        private enum HandleKind
        {
            None,
            TopLeft,
            Top,
            TopRight,
            Right,
            BottomRight,
            Bottom,
            BottomLeft,
            Left
        }

        private const float DefaultMinWidth = 150f;
        private const float DefaultMinHeight = 100f;
        private const float DefaultGripSize = 6f;
        private const float VisualGripSize = 16f;
        private const float GripThickness = 2f;
        private const float CornerSize = 12f; // Diagonal hit zone at corners
        private const float TitleBarHeight = 20f; // Reserve top area for window drag

        private static int _resizingId = -1;
        private static int _activeControlId = 0;
        private static HandleKind _handle = HandleKind.None;
        private static Vector2 _dragStart = Vector2.zero;
        private static Rect _rectAtStart = new();
        private static readonly System.Collections.Generic.HashSet<int> _userResized = new();

        /// <summary>True once the user has dragged a resize handle on this window. Auto-sizing draw
        /// code should skip overwriting height/width after this returns true.</summary>
        public static bool WasManuallyResized(int windowId) => _userResized.Contains(windowId);

        /// <summary>Returns a title-bar drag rect with corners excluded so resize hit-zones win.</summary>
        public static Rect TitleBarDragRect(float windowWidth, float titleHeight = 30f)
        {
            float inset = CornerSize;
            return new Rect(inset, 0, Mathf.Max(0f, windowWidth - inset * 2f), titleHeight);
        }

        public static Rect HandleResize(int windowId, Rect windowRect)
            => HandleResize(windowId, windowRect, DefaultMinWidth, DefaultMinHeight, DefaultGripSize);

        public static Rect HandleResize(int windowId, Rect windowRect, float minWidth, float minHeight, float gripSize)
        {
            Event evt = Event.current;
            if (evt == null) return windowRect;

            Vector2 mouse = evt.mousePosition;
            minWidth = Mathf.Max(1f, minWidth);
            minHeight = Mathf.Max(1f, minHeight);
            gripSize = Mathf.Max(1f, gripSize);

            HandleKind hit = GetHandleAt(mouse, windowRect, gripSize);
            UpdateCursor(windowId, hit, evt);

            if (evt.type == EventType.MouseDown && hit != HandleKind.None && _resizingId == -1)
            {
                _resizingId = windowId;
                _activeControlId = GUIUtility.GetControlID(FocusType.Passive);
                _handle = hit;
                _dragStart = mouse;
                _rectAtStart = windowRect;
                GUIUtility.hotControl = _activeControlId;
                evt.Use();
                return windowRect;
            }

            if (evt.type == EventType.MouseDrag && _resizingId == windowId)
            {
                Rect r = ResizeFromMouse(_rectAtStart, mouse, _dragStart, _handle, minWidth, minHeight);
                _userResized.Add(windowId);
                evt.Use();
                return r;
            }
            if (evt.type == EventType.MouseUp && _resizingId == windowId)
            {
                int activeControlId = _activeControlId;
                _resizingId = -1;
                _activeControlId = 0;
                _handle = HandleKind.None;
                if (GUIUtility.hotControl == activeControlId)
                {
                    GUIUtility.hotControl = 0;
                }
                evt.Use();
            }

            return windowRect;
        }

        private static HandleKind GetHandleAt(Vector2 mouse, Rect windowRect, float gripSize)
        {
            // Bail if mouse is outside the window entirely
            if (mouse.x < windowRect.x - gripSize || mouse.x > windowRect.xMax + gripSize ||
                mouse.y < windowRect.y - gripSize || mouse.y > windowRect.yMax + gripSize)
                return HandleKind.None;

            bool onLeft = mouse.x >= windowRect.x - gripSize && mouse.x <= windowRect.x + gripSize;
            bool onRight = mouse.x >= windowRect.xMax - gripSize && mouse.x <= windowRect.xMax + gripSize;
            bool onTop = mouse.y >= windowRect.y - gripSize && mouse.y <= windowRect.y + gripSize;
            bool onBottom = mouse.y >= windowRect.yMax - gripSize && mouse.y <= windowRect.yMax + gripSize;

            // Larger corner zones (Windows-style): within CornerSize of a corner
            bool nearLeft = mouse.x <= windowRect.x + CornerSize;
            bool nearRight = mouse.x >= windowRect.xMax - CornerSize;
            bool nearTop = mouse.y <= windowRect.y + CornerSize;
            bool nearBottom = mouse.y >= windowRect.yMax - CornerSize;

            // Corners take priority
            if (onBottom && nearLeft) return HandleKind.BottomLeft;
            if (onBottom && nearRight) return HandleKind.BottomRight;
            if (onTop && nearLeft) return HandleKind.TopLeft;
            if (onTop && nearRight) return HandleKind.TopRight;
            if (onLeft && nearBottom) return HandleKind.BottomLeft;
            if (onRight && nearBottom) return HandleKind.BottomRight;
            if (onLeft && nearTop) return HandleKind.TopLeft;
            if (onRight && nearTop) return HandleKind.TopRight;

            // Edges
            if (onBottom) return HandleKind.Bottom;
            if (onRight) return HandleKind.Right;
            if (onLeft) return HandleKind.Left;
            // Skip top edge — it conflicts with GUI.DragWindow title bar

            return HandleKind.None;
        }

        private static Rect ResizeFromMouse(Rect startRect, Vector2 mouse, Vector2 dragStart, HandleKind handle, float minWidth, float minHeight)
        {
            Vector2 delta = mouse - dragStart;
            Rect r = startRect;

            switch (handle)
            {
                case HandleKind.BottomRight:
                    r.width = Mathf.Max(minWidth, startRect.width + delta.x);
                    r.height = Mathf.Max(minHeight, startRect.height + delta.y);
                    break;

                case HandleKind.BottomLeft:
                    r.x = Mathf.Min(startRect.x + delta.x, startRect.xMax - minWidth);
                    r.width = Mathf.Max(minWidth, startRect.width - delta.x);
                    r.height = Mathf.Max(minHeight, startRect.height + delta.y);
                    break;

                case HandleKind.TopRight:
                    r.height = Mathf.Max(minHeight, startRect.height - delta.y);
                    r.y = startRect.yMax - r.height;
                    r.width = Mathf.Max(minWidth, startRect.width + delta.x);
                    break;

                case HandleKind.TopLeft:
                    r.height = Mathf.Max(minHeight, startRect.height - delta.y);
                    r.y = startRect.yMax - r.height;
                    r.x = Mathf.Min(startRect.x + delta.x, startRect.xMax - minWidth);
                    r.width = Mathf.Max(minWidth, startRect.width - delta.x);
                    break;

                case HandleKind.Top:
                    r.height = Mathf.Max(minHeight, startRect.height - delta.y);
                    r.y = startRect.yMax - r.height;
                    break;

                case HandleKind.Right:
                    r.width = Mathf.Max(minWidth, startRect.width + delta.x);
                    break;

                case HandleKind.Bottom:
                    r.height = Mathf.Max(minHeight, startRect.height + delta.y);
                    break;

                case HandleKind.Left:
                    r.x = Mathf.Min(startRect.x + delta.x, startRect.xMax - minWidth);
                    r.width = Mathf.Max(minWidth, startRect.width - delta.x);
                    break;
            }

            return r;
        }

        public static void DrawResizeHandles(Rect windowRect, Color color)
        {
        }

        private static void UpdateCursor(int windowId, HandleKind hit, Event evt)
        {
            if (evt == null) return;

            bool isDraggingThisWindow = _resizingId == windowId && _handle != HandleKind.None;
            if (isDraggingThisWindow)
            {
                ApplyCursor(_handle);
                return;
            }

            if (hit != HandleKind.None && (evt.type == EventType.MouseMove || evt.type == EventType.Repaint || evt.type == EventType.MouseDrag))
            {
                ApplyCursor(hit);
                return;
            }

            if (evt.type == EventType.Repaint || evt.type == EventType.MouseMove)
            {
                NativeCursor.SetArrow();
            }
        }

        private static void ApplyCursor(HandleKind handle)
        {
            switch (handle)
            {
                case HandleKind.TopLeft:
                case HandleKind.BottomRight:
                    NativeCursor.SetNWSE();
                    break;

                case HandleKind.TopRight:
                case HandleKind.BottomLeft:
                    NativeCursor.SetNESW();
                    break;

                case HandleKind.Top:
                case HandleKind.Bottom:
                    NativeCursor.SetVertical();
                    break;

                case HandleKind.Left:
                case HandleKind.Right:
                    NativeCursor.SetHorizontal();
                    break;

                default:
                    NativeCursor.SetArrow();
                    break;
            }
        }
    }
}