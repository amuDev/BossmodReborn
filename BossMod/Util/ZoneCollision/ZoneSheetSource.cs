namespace BossMod;

public readonly record struct ZoneEObjRow(uint Data, byte PopType, string SgbPath, bool DirectorControl, bool EyeCollision, bool Target);

// game data sheet rows the scene model needs (EObj, EObjName, PlaceName, BNpcName); the plugin and the harness both supply Lumina, tests can supply nothing
public interface IZoneSheetSource
{
    ZoneEObjRow? EObj(uint baseId);
    string EObjName(uint baseId);
    string PlaceName(uint id);
    string BNpcName(uint nameId);
}

public sealed class NullZoneSheetSource : IZoneSheetSource
{
    public static readonly NullZoneSheetSource Instance = new();
    public ZoneEObjRow? EObj(uint baseId) => null;
    public string EObjName(uint baseId) => "";
    public string PlaceName(uint id) => "";
    public string BNpcName(uint nameId) => "";
}

public sealed class LuminaZoneSheetSource : IZoneSheetSource
{
    private readonly Dictionary<uint, ZoneEObjRow?> _eobj = [];
    private readonly Dictionary<uint, string> _eobjName = [], _place = [], _bnpc = [];

    public ZoneEObjRow? EObj(uint baseId)
    {
        if (_eobj.TryGetValue(baseId, out var cached))
        {
            return cached;
        }
        ZoneEObjRow? row = null;
        var r = Service.LuminaRow<Lumina.Excel.Sheets.EObj>(baseId);
        if (r != null)
        {
            var e = r.Value;
            var sgb = "";
            try
            {
                sgb = e.SgbPath.ValueNullable?.SgbPath.ToString() ?? "";
            }
            catch (Exception ex)
            {
                Service.Log($"[ZoneSheetSource] EObj {baseId}: sgb path unresolved: {ex.Message}");
            }
            row = new(e.Data.RowId, e.PopType, sgb, e.DirectorControl, e.EyeCollision, e.Target);
        }
        _eobj[baseId] = row;
        return row;
    }

    public string EObjName(uint baseId)
    {
        if (!_eobjName.TryGetValue(baseId, out var n))
        {
            _eobjName[baseId] = n = Service.LuminaRow<Lumina.Excel.Sheets.EObjName>(baseId)?.Singular.ToString() ?? "";
        }
        return n;
    }

    public string PlaceName(uint id)
    {
        if (!_place.TryGetValue(id, out var n))
        {
            _place[id] = n = id != 0 ? Service.LuminaRow<Lumina.Excel.Sheets.PlaceName>(id)?.Name.ToString() ?? "" : "";
        }
        return n;
    }

    public string BNpcName(uint nameId)
    {
        if (!_bnpc.TryGetValue(nameId, out var n))
        {
            _bnpc[nameId] = n = nameId != 0 ? Service.LuminaRow<Lumina.Excel.Sheets.BNpcName>(nameId)?.Singular.ToString() ?? "" : "";
        }
        return n;
    }
}
