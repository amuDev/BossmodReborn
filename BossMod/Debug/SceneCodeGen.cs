namespace BossMod;

// component snippets for the scene rules: state-driven arena swaps, key pickup, door; pure text, repo conventions (braces on every branch, f suffixes)
public static class SceneCodeGen
{
    public sealed class ArenaSource
    {
        public string Field = "arena";
        public List<WPos[]> Outers = [];
        public List<WPos[]> Holes = [];
        public bool Flat;
        public float MeanY, MinY, MaxY;
        public bool HitboxIn, HitboxOut, ProjectionZero;
    }

    // the shared skeleton of the key and warp components: an actor found on creation, a state hook, AI steering to it and a hint
    private sealed class TrackedObject
    {
        public string ClassName = "";
        public string Field = "";
        public string[] ExtraFields = [];   // after the actor field
        public string[] OnCreated = [];     // inside the OID guard
        public string StateCondition = "";  // OnActorEventStateChange guard
        public string[] OnState = [];
        public string[] AiPreamble = [];    // before the interact guard
        public string AiCondition = "";
        public string Target = "";          // the actor expression to interact with
        public string HintCondition = "";
        public string HintCall = "";
        public bool DrawOutline;
    }

    public static ArenaSource FromPolygons(string field, List<SavedPolygon> polys, bool flat, float meanY, bool hitboxIn, bool hitboxOut, bool projectionZero)
    {
        var a = new ArenaSource { Field = field, Flat = flat, MeanY = meanY, HitboxIn = hitboxIn, HitboxOut = hitboxOut, ProjectionZero = projectionZero, MinY = float.MaxValue, MaxY = float.MinValue };
        foreach (var p in polys)
        {
            a.Outers.Add(Unflatten(p.Outer));
            foreach (var h in p.Holes)
            {
                a.Holes.Add(Unflatten(h));
            }
            a.MinY = MathF.Min(a.MinY, p.MinY);
            a.MaxY = MathF.Max(a.MaxY, p.MaxY);
        }
        if (polys.Count == 0)
        {
            a.MinY = a.MaxY = meanY;
        }
        return a;
    }

    private static WPos[] Unflatten(float[] xz)
    {
        var r = new WPos[xz.Length / 2];
        for (var i = 0; i < r.Length; ++i)
        {
            r[i] = new(xz[2 * i], xz[2 * i + 1]);
        }
        return r;
    }

    public static string Ident(string name, uint oid)
    {
        var sb = new StringBuilder();
        var upper = true;
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) && c < 128)
            {
                sb.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            else
            {
                upper = true;
            }
        }
        if (sb.Length == 0 || char.IsDigit(sb[0]))
        {
            return $"EObj{oid:X}";
        }
        return sb.ToString();
    }

    public static string Ident(string name, string fallback, uint oid) => Ident(name.Length > 0 ? name : fallback, oid);

    public static string OidLine(uint oid, string name) => $"{Ident(name, oid)} = 0x{oid:X}, // R0.5, EventObj type";

    private static string OidLine(uint oid, string name, string fallback) => OidLine(oid, name.Length > 0 ? name : fallback);

    private static uint ParseOid(string s) => ZoneBinary.TryParseHex(s, out var v) && v <= uint.MaxValue ? (uint)v : 0;

    // one snippet line; the snippets use CRLF like the module files
    private static void L(StringBuilder sb, string s) => sb.Append(s).Append("\r\n");

    // rounded first so a value that rounds to zero prints as 0 rather than -0
    private static string F(float v, int decimals) => CollisionArenaCodeGen.F(v, decimals);

    public static string Emit(SavedRule r, string provenance, ArenaSource? pre, ArenaSource? post, int decimals, ZoneSceneModel model)
    {
        var effectOid = ParseOid(r.EffectOid);
        var triggerOid = ParseOid(r.TriggerOid);
        var effectName = model.EventObjectsByBaseId.TryGetValue(effectOid, out var list) && list.Count > 0 ? model.EventObjects[list[0]].Name : "";
        var triggerName = model.EventObjectsByBaseId.TryGetValue(triggerOid, out var tl) && tl.Count > 0 ? model.EventObjects[tl[0]].Name : "";
        var sb = new StringBuilder();
        sb.Append(provenance);
        L(sb, $"// rule {r.Kind}: {r.TriggerText} -> {r.EffectText}");
        foreach (var e in r.Evidence)
        {
            L(sb, $"// evidence: {e}");
        }
        switch (r.Kind)
        {
            case ZoneRuleKind.SealOnPull:
            case ZoneRuleKind.SealRelease:
                return sb.Append(StateArenaSwap(r, effectOid, effectName, pre, post, decimals)).ToString();
            case ZoneRuleKind.KeyOpensDoor:
                return sb.Append(Door(r, effectOid, effectName, triggerOid, triggerName, post, decimals)).ToString();
            case ZoneRuleKind.SetClearSpawns:
                return sb.Append(KeyPickup(r, effectOid, effectName)).ToString();
            case ZoneRuleKind.WarpOpens:
            case ZoneRuleKind.WarpTeleport:
            case ZoneRuleKind.ShortcutTeleport:
                return sb.Append(Warp(r, effectOid != 0 ? effectOid : triggerOid, effectOid != 0 ? effectName : triggerName, model)).ToString();
            case ZoneRuleKind.FloorChange:
                return sb.Append(FloorChange(r, model)).ToString();
            case ZoneRuleKind.WaveSpawn:
            case ZoneRuleKind.BossAdds:
            case ZoneRuleKind.InstanceStart:
            case ZoneRuleKind.ExitGate:
                return sb.Append(Note(r, model)).ToString();
            default:
                L(sb, $"// no generator for rule kind '{r.Kind}'");
                return sb.ToString();
        }
    }

    private static string Hook(SavedRule r) => r.Channel == ZoneStateChannel.ObjectState ? "public override void OnActorEState(Actor actor, ushort state)" : "public override void OnActorEventStateChange(Actor actor, byte value)";
    private static string StateVar(SavedRule r) => r.Channel == ZoneStateChannel.ObjectState ? "state" : "value";

    // the arena fields live inside the component (a top-level class in the module file), indented one level
    private static void AppendArena(StringBuilder sb, ArenaSource a, int decimals)
    {
        var inner = new StringBuilder();
        CollisionArenaCodeGen.AppendVertexFields(inner, a.Outers, a.Holes, decimals, a.Field);
        inner.Append(CollisionArenaCodeGen.ArenaDeclaration($"{a.Field}Arena", a.Outers.Count, a.Holes.Count, a.Flat, a.MeanY, a.MinY, a.MaxY, a.ProjectionZero, a.HitboxIn, a.HitboxOut, a.Field));
        foreach (var line in inner.ToString().Split("\r\n"))
        {
            if (line.Length > 0)
            {
                L(sb, "    " + line);
            }
        }
    }

    private static void AppendArenaSwap(StringBuilder sb, string indent, string arena)
    {
        L(sb, $"{indent}Arena.Bounds = {arena};");
        L(sb, $"{indent}Arena.Center = {arena}.Center;");
    }

    // seal controller: EventState 0 at the pull = room only, 7 at the kill = room + corridor
    private static string StateArenaSwap(SavedRule r, uint oid, string name, ArenaSource? pre, ArenaSource? post, int decimals)
    {
        var sb = new StringBuilder();
        var ident = Ident(name, "Seal", oid);
        L(sb, $"// OID enum: {OidLine(oid, name, "Seal")}");
        if (pre == null && post == null)
        {
            L(sb, "// pick 'arena before' (sealed room) and/or 'arena after' (room + corridor) in the rule editor to emit the bounds");
        }
        L(sb, "sealed class ArenaSwap(BossModule module) : BossComponent(module)");
        L(sb, "{");
        if (pre != null)
        {
            AppendArena(sb, pre, decimals);
        }
        if (post != null)
        {
            AppendArena(sb, post, decimals);
        }
        if (pre != null || post != null)
        {
            L(sb, "");
        }
        L(sb, $"    {Hook(r)}");
        L(sb, "    {");
        L(sb, $"        if (actor.OID != (uint)OID.{ident})");
        L(sb, "        {");
        L(sb, "            return;");
        L(sb, "        }");
        var v = StateVar(r);
        var sealedState = r.Kind == ZoneRuleKind.SealOnPull ? Math.Max(r.EffectState, 0) : 0;
        var openState = r.Kind == ZoneRuleKind.SealRelease ? Math.Max(r.EffectState, 0) : 7;
        if (pre != null)
        {
            L(sb, $"        if ({v} == {sealedState})");
            L(sb, "        {");
            AppendArenaSwap(sb, "            ", $"{pre.Field}Arena");
            L(sb, "        }");
        }
        if (post != null)
        {
            L(sb, $"        {(pre != null ? "else " : "")}if ({v} == {openState})");
            L(sb, "        {");
            AppendArenaSwap(sb, "            ", $"{post.Field}Arena");
            L(sb, "        }");
        }
        if (pre == null && post == null)
        {
            L(sb, $"        // {v} {sealedState} = sealed (collision on), {openState} = released (collision removed)");
        }
        L(sb, "    }");
        L(sb, "}");
        var first = pre ?? post;
        if (first != null)
        {
            L(sb, $"// ctor: base(ws, primary, ArenaSwap.{first.Field}Arena.Center, ArenaSwap.{first.Field}Arena) after making the field internal, or keep the module's own arena literal; activate ArenaSwap in the trivial phase (OnActorEventStateChange is replayed on activation, so a late start still lands on the right bounds)");
        }
        L(sb, "// paste into the boss module of this room; dungeon scenario logic has no separate home");
        return sb.ToString();
    }

    private static void AppendTrackedObject(StringBuilder sb, string ident, TrackedObject t)
    {
        L(sb, $"sealed class {t.ClassName}(BossModule module) : BossComponent(module)");
        L(sb, "{");
        L(sb, $"    private Actor? {t.Field};");
        foreach (var f in t.ExtraFields)
        {
            L(sb, $"    {f}");
        }
        L(sb, "");
        L(sb, "    public override void OnActorCreated(Actor actor)");
        L(sb, "    {");
        L(sb, $"        if (actor.OID == (uint)OID.{ident})");
        L(sb, "        {");
        foreach (var line in t.OnCreated)
        {
            L(sb, $"            {line}");
        }
        L(sb, "        }");
        L(sb, "    }");
        L(sb, "");
        L(sb, "    public override void OnActorEventStateChange(Actor actor, byte value)");
        L(sb, "    {");
        L(sb, $"        if ({t.StateCondition})");
        L(sb, "        {");
        foreach (var line in t.OnState)
        {
            L(sb, $"            {line}");
        }
        L(sb, "        }");
        L(sb, "    }");
        L(sb, "");
        L(sb, "    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)");
        L(sb, "    {");
        foreach (var line in t.AiPreamble)
        {
            L(sb, $"        {line}");
        }
        L(sb, $"        if ({t.AiCondition})");
        L(sb, "        {");
        L(sb, $"            hints.InteractWithTarget = {t.Target};");
        L(sb, $"            hints.GoalZones.Add(AIHints.GoalSingleTarget({t.Target}, 2f, 5f));");
        L(sb, "        }");
        L(sb, "    }");
        L(sb, "");
        L(sb, "    public override void AddHints(int slot, Actor actor, TextHints hints)");
        L(sb, "    {");
        L(sb, $"        if ({t.HintCondition})");
        L(sb, "        {");
        L(sb, $"            {t.HintCall}");
        L(sb, "        }");
        L(sb, "    }");
        if (t.DrawOutline)
        {
            L(sb, "");
            L(sb, "    public override void DrawArenaForeground(int pcSlot, Actor pc)");
            L(sb, "    {");
            L(sb, $"        if ({t.Field} != null)");
            L(sb, "        {");
            L(sb, $"            Arena.ZoneCircleOutline({t.Field}.Position, 1f, Colors.Safe);");
            L(sb, "        }");
            L(sb, "    }");
        }
        L(sb, "}");
    }

    // the key object: track it from creation, drop it when consumed, steer the AI to it
    private static string KeyPickup(SavedRule r, uint oid, string name)
    {
        var sb = new StringBuilder();
        var ident = Ident(name, "Key", oid);
        var display = name.Length > 0 ? name : "key";
        L(sb, $"// {display}: spawns when the set dies ({(r.SetMembers.Length > 0 ? r.SetMembers : r.TriggerText)}); EventState 7 = picked up");
        L(sb, $"// OID enum: {OidLine(oid, name, "Key")}");
        AppendTrackedObject(sb, ident, new()
        {
            ClassName = $"{ident}Pickup",
            Field = "_key",
            OnCreated = ["_key = actor;"],
            StateCondition = "actor == _key && value == 7",
            OnState = ["_key = null;"],
            AiCondition = "_key is { IsTargetable: true } key",
            Target = "key",
            HintCondition = "_key != null",
            HintCall = $"hints.Add(\"{Escape(r.HintText.Length > 0 ? r.HintText : $"Pick up the {display}")}\");",
            DrawOutline = true,
        });
        L(sb, "// paste into the boss module of this room; activate in the trivial phase");
        return sb.ToString();
    }

    // door: state handler on the door object; with an 'arena after' the bounds swap, otherwise a hint + interact with the door while closed
    private static string Door(SavedRule r, uint doorOid, string doorName, uint keyOid, string keyName, ArenaSource? post, int decimals)
    {
        var sb = new StringBuilder();
        var door = Ident(doorName, "Door", doorOid);
        L(sb, $"// OID enum: {OidLine(doorOid, doorName, "Door")}");
        if (keyOid != 0)
        {
            L(sb, $"// OID enum: {OidLine(keyOid, keyName, "Key")}");
        }
        var openState = r.EffectState >= 0 ? r.EffectState : 7;
        var v = StateVar(r);
        L(sb, $"sealed class {door}Open(BossModule module) : BossComponent(module)");
        L(sb, "{");
        if (post != null)
        {
            AppendArena(sb, post, decimals);
            L(sb, "");
        }
        L(sb, "    private bool _open;");
        L(sb, "");
        L(sb, $"    {Hook(r)}");
        L(sb, "    {");
        L(sb, $"        if (actor.OID == (uint)OID.{door} && {v} == {openState})");
        L(sb, "        {");
        L(sb, "            _open = true;");
        if (post != null)
        {
            AppendArenaSwap(sb, "            ", $"{post.Field}Arena");
        }
        L(sb, "        }");
        L(sb, "    }");
        L(sb, "");
        L(sb, "    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)");
        L(sb, "    {");
        L(sb, "        if (!_open)");
        L(sb, "        {");
        L(sb, $"            hints.InteractWithOID(WorldState, (uint)OID.{door});");
        L(sb, "        }");
        L(sb, "    }");
        L(sb, "");
        L(sb, "    public override void AddHints(int slot, Actor actor, TextHints hints)");
        L(sb, "    {");
        L(sb, "        if (!_open)");
        L(sb, "        {");
        L(sb, $"            hints.Add(\"{Escape(r.HintText.Length > 0 ? r.HintText : keyOid != 0 ? $"Use the {(keyName.Length > 0 ? keyName : "key")} on the {(doorName.Length > 0 ? doorName : "door")}" : $"Open the {(doorName.Length > 0 ? doorName : "door")}")}\");");
        L(sb, "        }");
        L(sb, "    }");
        L(sb, "}");
        L(sb, $"// the door's EventState {openState} arrives {r.DelaySeconds:f1}s after the key is used; paste into the boss module of the room the door leads to");
        return sb.ToString();
    }

    // warp: hidden (EventState 7) until the room is cleared, then EventState 0; the party interacts with it to move on. A teleport with no
    // source object (a scripted move on a boss death / director update) only gets the landing: the next room's module starts there
    private static string Warp(SavedRule r, uint oid, string name, ZoneSceneModel model)
    {
        var sb = new StringBuilder();
        ZonePopPoint? pop = null;
        if (r.Kind != ZoneRuleKind.WarpOpens && ZoneBinary.TryParseHex(r.EffectNodeId, out var popId))
        {
            for (var i = 0; i < model.PopPoints.Count; ++i)
            {
                if (model.PopPoints[i].PathId == popId)
                {
                    pop = model.PopPoints[i];
                    break;
                }
            }
        }
        if (pop != null)
        {
            L(sb, $"// destination: pop point key 0x{pop.InstanceKey:X} ({pop.PopType}) at ({F(pop.Position.X, 1)}, {F(pop.Position.Z, 1)}), {r.DelaySeconds:f1}s after {(oid != 0 ? "the interaction" : r.TriggerText)}");
            if (oid == 0)
            {
                L(sb, $"private static readonly WPos teleportTarget = new({F(pop.Position.X, 3)}, {F(pop.Position.Z, 3)});");
                if (pop.RelativePositions.Length > 0)
                {
                    sb.Append("private static readonly WPos[] teleportSlots = [");
                    for (var i = 0; i < pop.RelativePositions.Length; ++i)
                    {
                        sb.Append(i > 0 ? (i % 5 == 0 ? ",\r\n    " : ", ") : "").Append($"new({F(pop.Position.X + pop.RelativePositions[i].X, 3)}, {F(pop.Position.Z + pop.RelativePositions[i].Z, 3)})");
                    }
                    L(sb, "];");
                }
            }
        }
        else if (oid == 0)
        {
            L(sb, "// pick the landing pop point via right-click on the canvas");
        }
        if (oid == 0)
        {
            L(sb, "// the module for the next room starts at the teleport target: no code needed unless the arena centre depends on it");
            return sb.ToString();
        }
        var ident = Ident(name, "Warp", oid);
        L(sb, $"// OID enum: {OidLine(oid, name, "Warp")}");
        AppendTrackedObject(sb, ident, new()
        {
            ClassName = $"{ident}Warp",
            Field = "_warp",
            ExtraFields = ["private bool _open;"],
            OnCreated = ["_warp = actor;", "_open = actor.EventState != 7;"],
            StateCondition = "actor == _warp",
            OnState = ["_open = value != 7;"],
            AiPreamble =
            [
                "var boss = Module.Enemies((uint)OID.Boss);",
                "var inCombat = false;",
                "for (var i = 0; i < boss.Count; ++i)",
                "{",
                "    if (boss[i].InCombat)",
                "    {",
                "        inCombat = true;",
                "        break;",
                "    }",
                "}",
            ],
            AiCondition = "_open && _warp != null && !inCombat",
            Target = "_warp",
            HintCondition = "_open && _warp != null",
            HintCall = $"hints.Add(\"{Escape(r.HintText.Length > 0 ? r.HintText : $"Take the {(name.Length > 0 ? name : "warp")}")}\", false);",
        });
        L(sb, $"// {r.TriggerText}; the warp's EventState 0 arrives {(r.Kind == ZoneRuleKind.WarpOpens ? $"{r.DelaySeconds:f1}s after the last death" : "when the room is cleared")}; adjust the Enemies(OID.Boss) guard to the module's boss enum");
        return sb.ToString();
    }

    // waves, start and exit carry no code of their own: a comment block with the facts for the module author
    private static string Note(SavedRule r, ZoneSceneModel model)
    {
        var sb = new StringBuilder();
        L(sb, $"// {r.Kind}: {r.TriggerText}");
        L(sb, $"//   -> {r.EffectText}{(r.DelaySeconds != 0f ? $" ({r.DelaySeconds:f1}s later)" : "")}");
        var eoIndex = model.FindEventObject(r.EffectNodeId);
        if (eoIndex >= 0)
        {
            var eo = model.EventObjects[eoIndex];
            L(sb, $"// OID enum: {OidLine(eo.BaseId, eo.Name)}");
            L(sb, $"// at ({F(eo.ActorPosition.X, 1)}, {F(eo.ActorPosition.Z, 1)})");
        }
        L(sb, r.Kind switch
        {
            ZoneRuleKind.WaveSpawn => "// a room wave: no module code; Components.Adds-style tracking of the spawned OIDs if the fight needs priorities",
            ZoneRuleKind.BossAdds => "// adds during the boss fight: track with Components.Adds / AddsMulti in the boss module",
            ZoneRuleKind.InstanceStart => "// the party spawns here; the first room's module (if any) starts from this point",
            _ => "// the exit gate appears after the last boss; nothing to do in a module",
        });
        return sb.ToString();
    }

    // a floor-change rule emits its piece's whole group: the shape list, the default arena and the component skeleton by family
    private static string FloorChange(SavedRule r, ZoneSceneModel model)
    {
        ZoneFloorPiece? piece = null;
        if (ZoneBinary.TryParseHex(r.EffectNodeId, out var pieceId))
        {
            for (var i = 0; i < model.FloorPieces.Count; ++i)
            {
                if (model.FloorPieces[i].PathId == pieceId)
                {
                    piece = model.FloorPieces[i];
                    break;
                }
            }
        }
        var group = piece != null ? model.FloorGroups[piece.Group] : model.FloorGroups.Find(g => r.TriggerText.Contains(g.Name, StringComparison.OrdinalIgnoreCase)) ?? (model.FloorGroups.Count > 0 ? model.FloorGroups[0] : null);
        if (group == null)
        {
            return "// no floor pieces in this layout: the arena change is cast / phase driven, see the evidence above\r\n";
        }
        var sb = new StringBuilder();
        if (r.Channel == ZoneStateChannel.MapEffect)
        {
            L(sb, $"// map effect index 0x{r.TriggerState:X2} state 0x{r.EffectState:X8}: fill the index below from this line");
        }
        sb.Append(FloorGroupSnippet(model, group, 3));
        return sb.ToString();
    }

    // the class-name stem of a group: its area name as an identifier ("Floor" for unnamed groups), at most 24 characters
    private static string GroupIdent(ZoneFloorGroup g)
    {
        var ident = Ident(g.Name, "Floor", 0);
        if (ident == "EObj0")
        {
            ident = "Floor";
        }
        if (ident.Length > 24)
        {
            ident = ident[..24];
        }
        return ident;
    }

    // remove the piece when the removed state arrives, put it back on 0x00020001; the arena follows either change
    private static void AppendPresenceToggle(StringBuilder sb, string indent, string index, uint removedState)
    {
        L(sb, $"{indent}if (state == 0x{removedState:X8}u)");
        L(sb, $"{indent}{{");
        L(sb, $"{indent}    if (_present.Remove(pieces[{index}]))");
        L(sb, $"{indent}    {{");
        L(sb, $"{indent}        Rebuild();");
        L(sb, $"{indent}    }}");
        L(sb, $"{indent}}}");
        L(sb, $"{indent}else if (state == 0x00020001u && !_present.Contains(pieces[{index}]))");
        L(sb, $"{indent}{{");
        L(sb, $"{indent}    _present.Add(pieces[{index}]);");
        L(sb, $"{indent}    Rebuild();");
        L(sb, $"{indent}}}");
    }

    // shapes per piece (Square when the piece is square, else Rectangle) with the layout positions, the union as the default arena, and the
    // hook skeleton: platforms react to OnActorEAnim on their object (break constants), sections to OnMapEffect (index from a replay)
    public static string FloorGroupSnippet(ZoneSceneModel model, ZoneFloorGroup g, int decimals)
    {
        var sb = new StringBuilder();
        var ident = GroupIdent(g);
        var sameName = 0;
        for (var i = 0; i < model.FloorGroups.Count; ++i)
        {
            if (GroupIdent(model.FloorGroups[i]) == ident)
            {
                ++sameName;
            }
        }
        if (sameName > 1)
        {
            ident += g.Index; // two groups named after the same area
        }
        L(sb, $"// floor pieces of '{g.Name}' from the layout ({g.Family}: {g.Pieces.Count} piece(s)); positions are the layout's, sizes the collision / map range extents");
        L(sb, $"sealed class {ident}Floor(BossModule module) : BossComponent(module)");
        L(sb, "{");
        L(sb, "    // one shape per piece, in layout order; a piece is removed from Present when it breaks");
        var positions = new StringBuilder();
        var shapes = new StringBuilder();
        for (var i = 0; i < g.Pieces.Count; ++i)
        {
            var p = model.FloorPieces[g.Pieces[i]];
            var hw = (p.Bounds.Max.X - p.Bounds.Min.X) * 0.5f;
            var hh = (p.Bounds.Max.Z - p.Bounds.Min.Z) * 0.5f;
            positions.Append(i > 0 ? ", " : "").Append($"new({F(p.Bounds.Center.X, decimals)}, {F(p.Bounds.Center.Z, decimals)})");
            shapes.Append(i > 0 ? ", " : "").Append(MathF.Abs(hw - hh) < 0.05f ? $"new Square(positions[{i}], {F(hw, decimals)})" : $"new Rectangle(positions[{i}], {F(hw, decimals)}, {F(hh, decimals)})");
        }
        L(sb, $"    private static readonly WPos[] positions = [{positions}];");
        L(sb, $"    private static readonly Shape[] pieces = [{shapes}];");
        L(sb, "    public static readonly ArenaBoundsCustom DefaultArena = new([.. pieces]); // plus the room floor the pieces sit in, if any: add its polygon to the union");
        L(sb, "    private readonly List<Shape> _present = [.. pieces];");
        L(sb, "");
        L(sb, "    private void Rebuild()");
        L(sb, "    {");
        L(sb, "        var arena = new ArenaBoundsCustom([.. _present]);");
        AppendArenaSwap(sb, "        ", "arena");
        L(sb, "    }");
        L(sb, "");
        if (g.Family == ZoneFloorFamily.EAnim)
        {
            var oids = new List<uint>();
            for (var i = 0; i < g.Pieces.Count; ++i)
            {
                var p = model.FloorPieces[g.Pieces[i]];
                if (p.EObj >= 0 && !oids.Contains(model.EventObjects[p.EObj].BaseId))
                {
                    oids.Add(model.EventObjects[p.EObj].BaseId);
                }
            }
            var oidList = new StringBuilder();
            var oidGuard = new StringBuilder();
            for (var i = 0; i < oids.Count; ++i)
            {
                oidList.Append(i > 0 ? ", " : "").Append($"0x{oids[i]:X}");
                oidGuard.Append(i > 0 ? " or " : "").Append($"0x{oids[i]:X}u");
            }
            L(sb, $"    // the platform objects: {(oids.Count > 0 ? oidList.ToString() : "none resolved")}; 0x00100020 = about to break, 0x00040008 = broken, 0x00020001 = back");
            L(sb, "    public override void OnActorEAnim(Actor actor, uint state)");
            L(sb, "    {");
            if (oids.Count == 0)
            {
                L(sb, "        // TODO: no event object resolved for the platforms; fill the OIDs");
                L(sb, "        if (actor.OID is not 0u)");
            }
            else
            {
                L(sb, $"        if (actor.OID is not ({oidGuard}))");
            }
            L(sb, "        {");
            L(sb, "            return;");
            L(sb, "        }");
            L(sb, "        for (var i = 0; i < positions.Length; ++i)");
            L(sb, "        {");
            L(sb, "            if (!actor.Position.AlmostEqual(positions[i], 5f))");
            L(sb, "            {");
            L(sb, "                continue;");
            L(sb, "            }");
            AppendPresenceToggle(sb, "            ", "i", 0x00040008u);
            L(sb, "        }");
            L(sb, "    }");
        }
        else
        {
            L(sb, "    // sections change through map effects: the index per section comes from a replay (`events` MAPFX lines during the fight);");
            L(sb, "    // 0x00020001 = the effect appears, 0x00080004 = removed (game-wide convention)");
            L(sb, "    public override void OnMapEffect(byte index, uint state)");
            L(sb, "    {");
            L(sb, "        var piece = index switch");
            L(sb, "        {");
            for (var i = 0; i < g.Pieces.Count; ++i)
            {
                L(sb, $"            0x{i:X2} => {i}, // TODO index of section {i} at ({F(model.FloorPieces[g.Pieces[i]].Position.X, 1)}, {F(model.FloorPieces[g.Pieces[i]].Position.Z, 1)})");
            }
            L(sb, "            _ => -1,");
            L(sb, "        };");
            L(sb, "        if (piece < 0)");
            L(sb, "        {");
            L(sb, "            return;");
            L(sb, "        }");
            AppendPresenceToggle(sb, "        ", "piece", 0x00080004u);
            L(sb, "    }");
        }
        L(sb, "}");
        L(sb, $"// ctor: base(ws, primary, {ident}Floor.DefaultArena.Center, {ident}Floor.DefaultArena); activate {ident}Floor in the trivial phase");
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
