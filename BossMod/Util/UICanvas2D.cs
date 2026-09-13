using Dalamud.Bindings.ImGui;

namespace BossMod;

// top-down XZ canvas over an ImGui draw list: uniform zoom about the cursor, panning, drag rectangles and world-unit drawing helpers
// screen = centre + (p.X - Center.X, p.Z - Center.Z) * Zoom, so north (-Z) is up without any axis flip
public sealed class UICanvas2D
{
    public WPos Center;
    public float Zoom = 4f; // pixels per yalm
    public float MinZoom = 0.05f;
    public float MaxZoom = 400f;
    public bool ShowGrid = true;
    public float MinorGrid = 5f;
    public float MajorGrid = 25f;
    public bool RightDragPans = true;
    public bool SpaceLeftPans = true;
    public bool SpaceHeld; // set by the owner each frame (raw key state - the game eats keyboard input before ImGui sees it)

    public bool Hovered { get; private set; }
    public bool Active { get; private set; }
    public bool Panning { get; private set; }
    public Vector2 ScreenTL, ScreenBR, ScreenSize, ScreenCenter, MouseScreen;
    public WPos MouseWorld;
    public WPos ViewMin, ViewMax;

    public bool DragActive, DragJustEnded;
    public WPos DragStart, DragEnd;
    private ImGuiMouseButton _dragButton;
    private bool _dragEnabled, _dragPressed;
    private Vector2 _dragStartScreen;
    private ImDrawListPtr _dl;

    public bool Begin(string id, Vector2 size)
    {
        if (size.X < 8f || size.Y < 8f)
        {
            Hovered = false;
            return false;
        }
        ImGui.InvisibleButton(id, size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        ScreenTL = ImGui.GetItemRectMin();
        ScreenBR = ImGui.GetItemRectMax();
        ScreenSize = ScreenBR - ScreenTL;
        ScreenCenter = (ScreenTL + ScreenBR) * 0.5f;
        Hovered = ImGui.IsItemHovered();
        Active = ImGui.IsItemActive();
        var io = ImGui.GetIO();
        MouseScreen = io.MousePos;
        MouseWorld = FromScreen(MouseScreen);

        // zoom about the cursor
        if (Hovered && io.MouseWheel != 0f && !io.KeyShift)
        {
            var k = MathF.Pow(1.1f, io.MouseWheel);
            var newZoom = Math.Clamp(Zoom * k, MinZoom, MaxZoom);
            var before = MouseWorld;
            Zoom = newZoom;
            var after = FromScreen(MouseScreen);
            Center = new(Center.X + before.X - after.X, Center.Z + before.Z - after.Z);
            MouseWorld = FromScreen(MouseScreen);
        }

        // pan
        var panDrag = Active && (ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 0f)
            || (RightDragPans && ImGui.IsMouseDragging(ImGuiMouseButton.Right, 4f))
            || (SpaceLeftPans && SpaceHeld && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f)));
        if (panDrag)
        {
            Panning = true;
            Center = new(Center.X - io.MouseDelta.X / Zoom, Center.Z - io.MouseDelta.Y / Zoom);
            MouseWorld = FromScreen(MouseScreen);
        }
        else if (Panning && !ImGui.IsMouseDown(ImGuiMouseButton.Middle) && !ImGui.IsMouseDown(ImGuiMouseButton.Right) && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            Panning = false;
        }

        // drag rectangle
        DragJustEnded = false;
        if (_dragEnabled)
        {
            if (!_dragPressed && Hovered && !Panning && ImGui.IsMouseClicked(_dragButton) && !(SpaceLeftPans && SpaceHeld))
            {
                _dragPressed = true;
                _dragStartScreen = MouseScreen;
                DragStart = MouseWorld;
                DragActive = false;
            }
            if (_dragPressed)
            {
                DragEnd = MouseWorld;
                if (!DragActive && (MouseScreen - _dragStartScreen).Length() > 3f)
                {
                    DragActive = true;
                }
                if (!ImGui.IsMouseDown(_dragButton))
                {
                    _dragPressed = false;
                    DragJustEnded = DragActive;
                    DragActive = false;
                }
            }
        }
        else
        {
            _dragPressed = false;
            DragActive = false;
        }
        _dragEnabled = false; // must be re-enabled every frame by the owner

        ViewMin = FromScreen(ScreenTL);
        ViewMax = FromScreen(ScreenBR);
        _dl = ImGui.GetWindowDrawList();
        _dl.PushClipRect(ScreenTL, ScreenBR, true);
        _dl.AddRectFilled(ScreenTL, ScreenBR, 0xff181818);
        if (ShowGrid)
        {
            DrawGrid();
        }
        return true;
    }

    public void End()
    {
        if (DragActive || _dragPressed && DragActive)
        {
            var a = ToScreen(DragStart);
            var b = ToScreen(DragEnd);
            _dl.AddRectFilled(Vector2.Min(a, b), Vector2.Max(a, b), WithAlpha(Colors.Vulnerable, 0x30));
            _dl.AddRect(Vector2.Min(a, b), Vector2.Max(a, b), Colors.Vulnerable);
        }
        var fs = ImGui.GetFontSize();
        _dl.AddText(new(ScreenCenter.X - fs * 0.3f, ScreenTL.Y + 2f), Colors.CardinalN, "N");
        _dl.AddText(new(ScreenCenter.X - fs * 0.3f, ScreenBR.Y - fs - 2f), Colors.CardinalS, "S");
        _dl.AddText(new(ScreenBR.X - fs - 2f, ScreenCenter.Y - fs * 0.5f), Colors.CardinalE, "E");
        _dl.AddText(new(ScreenTL.X + 4f, ScreenCenter.Y - fs * 0.5f), Colors.CardinalW, "W");
        _dl.PopClipRect();
    }

    // call between Begin and End on every frame the tool wants a drag rectangle
    public void EnableDragRect(ImGuiMouseButton button)
    {
        _dragEnabled = true;
        _dragButton = button;
    }

    public Vector2 ToScreen(WPos p) => new(ScreenCenter.X + (p.X - Center.X) * Zoom, ScreenCenter.Y + (p.Z - Center.Z) * Zoom);
    public Vector2 ToScreen(in Vector3 p) => new(ScreenCenter.X + (p.X - Center.X) * Zoom, ScreenCenter.Y + (p.Z - Center.Z) * Zoom);
    public WPos FromScreen(Vector2 s) => new(Center.X + (s.X - ScreenCenter.X) / Zoom, Center.Z + (s.Y - ScreenCenter.Y) / Zoom);

    public bool IsClicked(ImGuiMouseButton b) => Hovered && !Panning && ImGui.IsMouseClicked(b);

    public bool IsVisible(float minX, float minZ, float maxX, float maxZ) => maxX >= ViewMin.X && minX <= ViewMax.X && maxZ >= ViewMin.Z && minZ <= ViewMax.Z;
    public bool IsVisible(in Vector3 min, in Vector3 max) => IsVisible(min.X, min.Z, max.X, max.Z);

    public void FitBounds(WPos min, WPos max, float paddingPx = 24f)
    {
        var w = MathF.Max(max.X - min.X, 1f);
        var h = MathF.Max(max.Z - min.Z, 1f);
        var size = ScreenSize.X > 8f ? ScreenSize : new Vector2(800f, 600f);
        Zoom = Math.Clamp(MathF.Min((size.X - 2f * paddingPx) / w, (size.Y - 2f * paddingPx) / h), MinZoom, MaxZoom);
        Center = new((min.X + max.X) * 0.5f, (min.Z + max.Z) * 0.5f);
    }

    public void FitBounds(in Vector3 min, in Vector3 max, float paddingPx = 24f) => FitBounds(new WPos(min.X, min.Z), new WPos(max.X, max.Z), paddingPx);

    public static uint WithAlpha(uint abgr, byte a) => (abgr & 0x00FFFFFFu) | ((uint)a << 24);

    private void DrawGrid()
    {
        var minorPx = MinorGrid * Zoom;
        var drawMinor = minorPx >= 8f;
        var step = drawMinor ? MinorGrid : MajorGrid;
        if (MajorGrid * Zoom < 6f)
        {
            step = MajorGrid * MathF.Ceiling(6f / (MajorGrid * Zoom));
        }
        var minorColor = WithAlpha(Colors.Border, 0x28);
        var majorColor = WithAlpha(Colors.Border, 0x60);
        var x0 = MathF.Floor(ViewMin.X / step) * step;
        for (var x = x0; x <= ViewMax.X; x += step)
        {
            var major = MathF.Abs(x / MajorGrid - MathF.Round(x / MajorGrid)) < 1e-3f;
            var sx = ScreenCenter.X + (x - Center.X) * Zoom;
            _dl.AddLine(new(sx, ScreenTL.Y), new(sx, ScreenBR.Y), major ? majorColor : minorColor, 1f);
            if (major)
            {
                _dl.AddText(new(sx + 2f, ScreenTL.Y + 2f), majorColor, $"{x:0}");
            }
        }
        var z0 = MathF.Floor(ViewMin.Z / step) * step;
        for (var z = z0; z <= ViewMax.Z; z += step)
        {
            var major = MathF.Abs(z / MajorGrid - MathF.Round(z / MajorGrid)) < 1e-3f;
            var sz = ScreenCenter.Y + (z - Center.Z) * Zoom;
            _dl.AddLine(new(ScreenTL.X, sz), new(ScreenBR.X, sz), major ? majorColor : minorColor, 1f);
            if (major)
            {
                _dl.AddText(new(ScreenTL.X + 14f, sz + 1f), majorColor, $"{z:0}");
            }
        }
    }

    // --- world-unit drawing helpers (thickness in pixels) ---

    public void Line(WPos a, WPos b, uint color, float thickness = 1f) => _dl.AddLine(ToScreen(a), ToScreen(b), color, thickness);
    public void Line(in Vector3 a, in Vector3 b, uint color, float thickness = 1f) => _dl.AddLine(ToScreen(a), ToScreen(b), color, thickness);

    public void Triangle(in Vector3 a, in Vector3 b, in Vector3 c, uint color, float thickness = 1f) => _dl.AddTriangle(ToScreen(a), ToScreen(b), ToScreen(c), color, thickness);
    public void TriangleFilled(in Vector3 a, in Vector3 b, in Vector3 c, uint color) => _dl.AddTriangleFilled(ToScreen(a), ToScreen(b), ToScreen(c), color);

    public void Poly(ReadOnlySpan<WPos> pts, uint color, float thickness = 1f, bool closed = true)
    {
        var n = pts.Length;
        for (var i = 0; i + 1 < n; ++i)
        {
            _dl.AddLine(ToScreen(pts[i]), ToScreen(pts[i + 1]), color, thickness);
        }
        if (closed && n > 2)
        {
            _dl.AddLine(ToScreen(pts[n - 1]), ToScreen(pts[0]), color, thickness);
        }
    }

    public void Poly(ReadOnlySpan<Vector3> pts, uint color, float thickness = 1f, bool closed = true)
    {
        var n = pts.Length;
        for (var i = 0; i + 1 < n; ++i)
        {
            _dl.AddLine(ToScreen(pts[i]), ToScreen(pts[i + 1]), color, thickness);
        }
        if (closed && n > 2)
        {
            _dl.AddLine(ToScreen(pts[n - 1]), ToScreen(pts[0]), color, thickness);
        }
    }

    public void Circle(WPos c, float radius, uint color, float thickness = 1f) => _dl.AddCircle(ToScreen(c), radius * Zoom, color, 0, thickness);
    public void CircleFilled(WPos c, float radius, uint color) => _dl.AddCircleFilled(ToScreen(c), radius * Zoom, color);
    public void Rect(WPos min, WPos max, uint color, float thickness = 1f) => _dl.AddRect(ToScreen(min), ToScreen(max), color, 0f, ImDrawFlags.None, thickness);
    public void Rect(in Vector3 min, in Vector3 max, uint color, float thickness = 1f) => _dl.AddRect(ToScreen(min), ToScreen(max), color, 0f, ImDrawFlags.None, thickness);
    public void RectFilled(in Vector3 min, in Vector3 max, uint color) => _dl.AddRectFilled(ToScreen(min), ToScreen(max), color);
    public void Text(WPos p, string text, uint color, Vector2 pixelOffset = default) => _dl.AddText(ToScreen(p) + pixelOffset, color, text);
    public void ScreenCircle(Vector2 s, float radiusPx, uint color, float thickness = 1f) => _dl.AddCircle(s, radiusPx, color, 0, thickness);
    public void ScreenCircleFilled(Vector2 s, float radiusPx, uint color) => _dl.AddCircleFilled(s, radiusPx, color);

    public void Arrow(WPos from, WPos to, uint color, float thickness = 2f, float headPx = 8f)
    {
        var a = ToScreen(from);
        var b = ToScreen(to);
        _dl.AddLine(a, b, color, thickness);
        var d = b - a;
        var len = d.Length();
        if (len < 1e-3f)
        {
            return;
        }
        d /= len;
        var n = new Vector2(-d.Y, d.X);
        _dl.AddTriangleFilled(b, b - d * headPx + n * headPx * 0.5f, b - d * headPx - n * headPx * 0.5f, color);
    }

    public void Crosshair(WPos p, float sizePx, uint color, float thickness = 1f)
    {
        var s = ToScreen(p);
        _dl.AddLine(new(s.X - sizePx, s.Y), new(s.X + sizePx, s.Y), color, thickness);
        _dl.AddLine(new(s.X, s.Y - sizePx), new(s.X, s.Y + sizePx), color, thickness);
    }
}
