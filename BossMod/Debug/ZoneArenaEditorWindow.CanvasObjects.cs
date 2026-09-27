using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace BossMod;

// canvas side of the objects: dashed ghosts for colliders the scene switched off, trigger volumes, links, hit testing and the context popup
public sealed partial class ZoneArenaEditorWindow
{
    private const float ObjectHitRadiusPx = 9f;

    private bool _showAreas = true;

    private void DashedLine(in Vector3 a, in Vector3 b, uint color, float thickness = 1f, float dashPx = 6f)
    {
        var sa = _canvas.ToScreen(a);
        var sb = _canvas.ToScreen(b);
        var len = (sb - sa).Length();
        if (len < dashPx * 2f)
        {
            _canvas.Line(a, b, color, thickness);
            return;
        }
        var n = (int)(len / dashPx);
        for (var i = 0; i < n; i += 2)
        {
            var t0 = i / (float)n;
            var t1 = MathF.Min((i + 1) / (float)n, 1f);
            _canvas.Line(Vector3.Lerp(a, b, t0), Vector3.Lerp(a, b, t1), color, thickness);
        }
    }

    private void DashedRect(in Bounds3 b, uint color, float thickness = 1f)
    {
        var y = b.Min.Y;
        var c0 = new Vector3(b.Min.X, y, b.Min.Z);
        var c1 = new Vector3(b.Max.X, y, b.Min.Z);
        var c2 = new Vector3(b.Max.X, y, b.Max.Z);
        var c3 = new Vector3(b.Min.X, y, b.Max.Z);
        DashedLine(c0, c1, color, thickness);
        DashedLine(c1, c2, color, thickness);
        DashedLine(c2, c3, color, thickness);
        DashedLine(c3, c0, color, thickness);
    }

    // the map ranges as a coloured overview with place names at low zoom, so a dungeon reads as rooms before the meshes do
    private void DrawAreaOverview()
    {
        if (!_showAreas || _canvas.Zoom >= 3f || _session == null)
        {
            return;
        }
        var model = _session.Model;
        var dl = ImGui.GetWindowDrawList();
        for (var i = 0; i < model.Areas.Count; ++i)
        {
            var a = model.Areas[i];
            if (!_canvas.IsVisible(a.WorldBounds.Min, a.WorldBounds.Max))
            {
                continue;
            }
            // one hue per area, boss arenas (map 0) warmer
            var fill = IndexColor(i, a.Map == 0 ? 0.7f : 0.45f, 0.9f, 0.10f);
            _canvas.RectFilled(a.WorldBounds.Min, a.WorldBounds.Max, fill);
            _canvas.Rect(a.WorldBounds.Min, a.WorldBounds.Max, UICanvas2D.WithAlpha(fill, 0x8C), 1f);
            var c = a.WorldBounds.Center;
            var label = a.Name.Length > 0 ? a.Name : $"area {i}";
            var size = ImGui.CalcTextSize(label);
            var sp = _canvas.ToScreen(c) - size * 0.5f;
            dl.AddText(sp, UICanvas2D.WithAlpha(fill, 0xE6), label);
        }
    }

    private static Vector3 HsvToRgb(float h, float s, float v)
    {
        float r = 0f, g = 0f, b = 0f;
        ImGui.ColorConvertHSVtoRGB(h, s, v, ref r, ref g, ref b);
        return new(r, g, b);
    }

    // floor pieces: outline and label per piece in its group's colour; pieces gone at the scrub time draw dashed
    private void DrawFloorPieces()
    {
        if (!_showFloorPieces || _session == null || _session.Model.FloorPieces.Count == 0)
        {
            return;
        }
        var model = _session.Model;
        var present = _timeline?.PiecesPresentAt(_timelineT);
        var fills = PieceFills();
        foreach (var p in model.FloorPieces)
        {
            if (!_canvas.IsVisible(p.Bounds.Min, p.Bounds.Max))
            {
                continue;
            }
            var col = fills[p.Index] | 0xFF000000u;
            var gone = ZoneSceneTimelineFile.PieceGone(present, p);
            if (gone)
            {
                DashedRect(p.Bounds, UICanvas2D.WithAlpha(col, 0xA0), 1.5f);
            }
            else
            {
                _canvas.Rect(p.Bounds.Min, p.Bounds.Max, UICanvas2D.WithAlpha(col, 0xD0), 1.5f);
            }
            if (_canvas.Zoom >= 3f)
            {
                var g = model.FloorGroups[p.Group];
                _canvas.Text(new WPos(p.Bounds.Min.X, p.Bounds.Min.Z), $"{g.Name} {(p.Kind == ZoneFloorPieceKind.Platform ? "platform" : "section")} {g.Pieces.IndexOf(p.Index)}{(gone ? " (gone)" : "")}", UICanvas2D.WithAlpha(col, 0xE0), new(3f, -14f));
            }
        }
    }

    // door / event / map ranges as dashed boxes (the script's trigger volumes, never collision)
    private void DrawTriggers()
    {
        if (!_showTriggers || _canvas.Zoom < 2f)
        {
            return;
        }
        var scene = _scene!;
        var model = _session!.Model;
        for (var i = 0; i < model.Triggers.Count; ++i)
        {
            var m = scene.Markers[model.Triggers[i]];
            if (m.Corners == null || !scene.IsLayerEnabled(m.LayerIndex) || !_canvas.IsVisible(m.WorldBounds.Min, m.WorldBounds.Max))
            {
                continue;
            }
            var r = new ObjectRef(ObjectKind.Trigger, i);
            var hot = r == _hoverObject || r == _selectedObject;
            var color = hot ? Colors.PlayerInteresting : m.Type switch
            {
                (int)LgbInstanceType.DoorRange => UICanvas2D.WithAlpha(Colors.Other5, 0xA0),
                (int)LgbInstanceType.EventRange => UICanvas2D.WithAlpha(Colors.Other6, 0x80),
                _ => UICanvas2D.WithAlpha(Colors.Other7, 0x50),
            };
            BoxTop(m.Corners, color, hot ? 2f : 1f, true);
            if (_canvas.Zoom >= 6f || hot)
            {
                var area = m.Type == (int)LgbInstanceType.MapRange ? AreaByMarker().GetValueOrDefault(m.Index)?.Name : null;
                _canvas.Text(new WPos(m.WorldBounds.Min.X, m.WorldBounds.Min.Z), area != null ? $"{m.TypeName} '{area}'" : $"{m.TypeName}{(m.Name.Length > 0 ? $" '{m.Name}'" : "")}", color, new(4f, 2f));
            }
        }
    }

    // exit -> destination pop arrows and event object -> bound instance lines
    private void DrawExitLinks()
    {
        if (!_showLinks)
        {
            return;
        }
        var scene = _scene!;
        var model = _session!.Model;
        foreach (var e in model.Exits)
        {
            if (e.DestPopNode < 0)
            {
                continue;
            }
            var to = scene.Nodes[e.DestPopNode].Position;
            _canvas.Arrow(new WPos(e.Position.X, e.Position.Z), new WPos(to.X, to.Z), UICanvas2D.WithAlpha(Colors.Other3, 0xC0), 1.5f);
            if (e.ReturnPopNode >= 0)
            {
                var back = scene.Nodes[e.ReturnPopNode].Position;
                _canvas.Arrow(new WPos(to.X, to.Z), new WPos(back.X, back.Z), UICanvas2D.WithAlpha(Colors.Other3, 0x60), 1f);
            }
        }
        // warp -> landing from the model: dashed = layout-order guess, solid = seen in a replay
        foreach (var l in model.Links)
        {
            var eo = model.EventObjects[l.EObj];
            var to = l.Landing;
            var col = UICanvas2D.WithAlpha(Colors.Other2, l.Confidence >= 0.5f ? (byte)0xE0 : (byte)0x90);
            if (l.Confidence >= 0.5f)
            {
                _canvas.Arrow(new WPos(eo.ActorPosition.X, eo.ActorPosition.Z), new WPos(to.X, to.Z), col, 2f, 10f);
            }
            else
            {
                DashedLine(eo.ActorPosition, to, col, 1.5f, 8f);
                _canvas.ScreenCircle(_canvas.ToScreen(to), 6f, col, 1.5f);
            }
            if (_canvas.Zoom >= 2f)
            {
                _canvas.Text(new WPos(to.X, to.Z), l.Confidence >= 0.5f ? $"{eo.Name} lands" : $"{eo.Name} lands? (guess)", col, new(10f, 6f));
            }
        }
        // shortcut / hand-made teleport rules
        foreach (var r in _rules)
        {
            if (r.Kind is not (ZoneRuleKind.WarpTeleport or ZoneRuleKind.ShortcutTeleport) || r.EffectNodeId.Length == 0)
            {
                continue;
            }
            var trigger = model.FindEventObject(r.TriggerNodeId);
            if (r.Kind == ZoneRuleKind.WarpTeleport && trigger >= 0 && model.Links.Exists(l => l.EObj == trigger))
            {
                continue; // drawn from the model's link
            }
            var to = NodePosition(r.EffectNodeId);
            var from = NodePosition(r.TriggerNodeId);
            if (to is not { } tp)
            {
                continue;
            }
            if (from is { } fp)
            {
                _canvas.Arrow(new WPos(fp.X, fp.Z), new WPos(tp.X, tp.Z), UICanvas2D.WithAlpha(Colors.Other2, r.Enabled ? (byte)0xE0 : (byte)0x60), 2f, 10f);
            }
            else
            {
                _canvas.ScreenCircle(_canvas.ToScreen(tp), 9f, UICanvas2D.WithAlpha(Colors.Other2, 0xC0), 2f);
            }
            if (_canvas.Zoom >= 2f)
            {
                _canvas.Text(new WPos(tp.X, tp.Z), r.Kind == ZoneRuleKind.ShortcutTeleport ? "shortcut lands" : "warp lands", UICanvas2D.WithAlpha(Colors.Other2, 0xE0), new(10f, 6f));
            }
        }
        if (_canvas.Zoom < 4f)
        {
            return;
        }
        var link = UICanvas2D.WithAlpha(Colors.Other4, 0x60);
        foreach (var eo in model.EventObjects)
        {
            if (eo.BoundNode < 0 || !scene.IsLayerEnabled(scene.Nodes[eo.NodeIndex].LayerIndex) || !_canvas.IsVisible(eo.Position.X - 1f, eo.Position.Z - 1f, eo.Position.X + 1f, eo.Position.Z + 1f))
            {
                continue;
            }
            var b = scene.Nodes[eo.BoundNode].Position;
            if ((new Vector2(b.X, b.Z) - new Vector2(eo.Position.X, eo.Position.Z)).Length() > 0.5f)
            {
                _canvas.Line(eo.Position, b, link, 1f);
            }
        }
    }

    // the hovered / selected object: its controlled colliders outlined, its bound node marked
    private void DrawObjectHighlight()
    {
        DrawObjectHighlight(_hoverObject, 0xFF);
        if (_selectedObject != _hoverObject)
        {
            DrawObjectHighlight(_selectedObject, 0xA0);
        }
    }

    private void DrawObjectHighlight(ObjectRef r, byte alpha)
    {
        if (!r.Valid)
        {
            return;
        }
        var scene = _scene!;
        var model = _session!.Model;
        var color = UICanvas2D.WithAlpha(Colors.Vulnerable, alpha);
        switch (r.Kind)
        {
            case ObjectKind.EObj:
                {
                    var eo = model.EventObjects[r.Index];
                    foreach (var m in eo.ControlledMeshes)
                    {
                        _canvas.Rect(scene.Meshes[m].WorldBounds.Min, scene.Meshes[m].WorldBounds.Max, color, 2f);
                    }
                    foreach (var b in eo.ControlledBoxes)
                    {
                        BoxTop(scene.Boxes[b].Corners, color, 2.5f);
                    }
                    if (eo.BoundNode >= 0)
                    {
                        var bn = scene.Nodes[eo.BoundNode].Position;
                        _canvas.Line(eo.Position, bn, color, 1.5f);
                        _canvas.ScreenCircle(_canvas.ToScreen(bn), 5f, color, 1.5f);
                    }
                    if (eo.LinkedNode >= 0)
                    {
                        DashedLine(eo.Position, scene.Nodes[eo.LinkedNode].Position, color, 1.5f);
                    }
                    _canvas.ScreenCircle(_canvas.ToScreen(eo.Position), 8f, color, 2f);
                }
                break;
            case ObjectKind.Pop:
                {
                    var p = model.PopPoints[r.Index];
                    _canvas.ScreenCircle(_canvas.ToScreen(p.Position), 8f, color, 2f);
                    foreach (var rel in p.RelativePositions)
                    {
                        var q = p.Position + rel;
                        _canvas.ScreenCircleFilled(_canvas.ToScreen(q), 2f, color);
                    }
                }
                break;
            case ObjectKind.Exit:
                {
                    var e = model.Exits[r.Index];
                    _canvas.ScreenCircle(_canvas.ToScreen(e.Position), 8f, color, 2f);
                    if (e.DestPopNode >= 0)
                    {
                        _canvas.ScreenCircle(_canvas.ToScreen(scene.Nodes[e.DestPopNode].Position), 8f, color, 2f);
                    }
                }
                break;
            case ObjectKind.Trigger:
                break;
            case ObjectKind.Enemy:
                if (Enemy(r) is { } en)
                {
                    _canvas.Circle(new WPos(en.X, en.Z), 1f, color, 1.5f);
                    if (en.Death >= 0)
                    {
                        _canvas.Line(new WPos(en.X, en.Z), new WPos(en.DX, en.DZ), color, 1f);
                        _canvas.ScreenCircle(_canvas.ToScreen(new Vector3(en.DX, en.Y, en.DZ)), 4f, color, 1.5f);
                    }
                }
                break;
        }
    }

    // screen-space hit test over the drawn objects (each kind only while its layer is shown); markers win over volumes, the nearest of a kind wins
    private void UpdateHoverObject()
    {
        _hoverObject = ObjectRef.None;
        if (_scene == null || _session == null || !_canvas.Hovered || _canvas.Panning)
        {
            return;
        }
        var scene = _scene;
        var model = _session.Model;
        var mouse = _canvas.MouseScreen;
        var best = ObjectHitRadiusPx * ObjectHitRadiusPx;
        var zoomOk = _showMarkers && _canvas.Zoom >= 4f;
        for (var i = 0; i < model.EventObjects.Count && zoomOk; ++i)
        {
            var eo = model.EventObjects[i];
            if (!scene.IsLayerEnabled(scene.Nodes[eo.NodeIndex].LayerIndex))
            {
                continue;
            }
            var d = (_canvas.ToScreen(eo.Position) - mouse).LengthSquared();
            if (d < best)
            {
                best = d;
                _hoverObject = new(ObjectKind.EObj, i);
            }
        }
        if (_hoverObject.Valid)
        {
            return;
        }
        for (var i = 0; i < model.Exits.Count && _showMarkers; ++i)
        {
            var d = (_canvas.ToScreen(model.Exits[i].Position) - mouse).LengthSquared();
            if (d < best)
            {
                best = d;
                _hoverObject = new(ObjectKind.Exit, i);
            }
        }
        for (var i = 0; i < model.PopPoints.Count && zoomOk; ++i)
        {
            var p = model.PopPoints[i];
            if (!scene.IsLayerEnabled(scene.Nodes[p.NodeIndex].LayerIndex))
            {
                continue;
            }
            var d = (_canvas.ToScreen(p.Position) - mouse).LengthSquared();
            if (d < best)
            {
                best = d;
                _hoverObject = new(ObjectKind.Pop, i);
            }
        }
        if (_hoverObject.Valid || !_showTriggers || _canvas.Zoom < 2f)
        {
            return;
        }
        // trigger volumes: the smallest one containing the cursor, only when no triangle is under it (they cover whole rooms)
        if (_hoverTri >= 0)
        {
            return;
        }
        var mw = _canvas.MouseWorld;
        var bestArea = float.MaxValue;
        for (var i = 0; i < model.Triggers.Count; ++i)
        {
            var m = scene.Markers[model.Triggers[i]];
            if (m.Type == (int)LgbInstanceType.MapRange || !scene.IsLayerEnabled(m.LayerIndex) || !m.WorldBounds.ContainsXZ(mw.X, mw.Z))
            {
                continue;
            }
            var area = (m.WorldBounds.Max.X - m.WorldBounds.Min.X) * (m.WorldBounds.Max.Z - m.WorldBounds.Min.Z);
            if (area < bestArea)
            {
                bestArea = area;
                _hoverObject = new(ObjectKind.Trigger, i);
            }
        }
    }

    private void HandleObjectClicks(bool spaceHeld)
    {
        if (!_canvas.Hovered || spaceHeld)
        {
            return;
        }
        if (_hoverObject.Valid && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !AltHeld && _tool == Tool.Pick)
        {
            _selectedObject = _hoverObject;
        }
        // right click (not a right-drag pan) opens the object menu
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Right) && !_canvas.Panning && ImGui.GetMouseDragDelta(ImGuiMouseButton.Right).LengthSquared() < 16f && _hoverObject.Valid)
        {
            _popupObject = _hoverObject;
            _selectedObject = _hoverObject;
            ImGui.OpenPopup(ObjectPopupId);
        }
    }

    private void DrawObjectPopup()
    {
        using var popup = ImRaii.Popup(ObjectPopupId);
        if (popup)
        {
            DrawObjectPopupBody();
        }
    }
}
