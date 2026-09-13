namespace BossMod;

// triangle selection with delta-based undo/redo; the Selected set instance is shared with the auto-map session
public sealed class TriangleSelection(HashSet<int> selected)
{
    private readonly record struct Delta(int[] Added, int[] Removed, string Label);
    private const int UndoDepth = 64;

    public readonly HashSet<int> Selected = selected;
    public int Version;
    private readonly List<Delta> _undo = [];
    private readonly List<Delta> _redo = [];
    private List<int>? _strokeAdded;
    private List<int>? _strokeRemoved;
    private string _strokeLabel = "";
    private readonly List<int> _scratchAdd = [];
    private readonly List<int> _scratchRemove = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;

    public bool Apply(ReadOnlySpan<int> add, ReadOnlySpan<int> remove, string label)
    {
        _scratchAdd.Clear();
        _scratchRemove.Clear();
        for (var i = 0; i < remove.Length; ++i)
        {
            if (Selected.Remove(remove[i]))
            {
                _scratchRemove.Add(remove[i]);
            }
        }
        for (var i = 0; i < add.Length; ++i)
        {
            if (Selected.Add(add[i]))
            {
                _scratchAdd.Add(add[i]);
            }
        }
        if (_scratchAdd.Count == 0 && _scratchRemove.Count == 0)
        {
            return false;
        }
        ++Version;
        if (_strokeAdded != null && _strokeRemoved != null)
        {
            _strokeAdded.AddRange(_scratchAdd);
            _strokeRemoved.AddRange(_scratchRemove);
        }
        else
        {
            Push(new([.. _scratchAdd], [.. _scratchRemove], label));
        }
        return true;
    }

    public void Toggle(int tri) => Apply(Selected.Contains(tri) ? [] : [tri], Selected.Contains(tri) ? [tri] : [], "toggle");
    public void Add(int tri) => Apply([tri], [], "add");
    public void Remove(int tri) => Apply([], [tri], "remove");

    public void Replace(IEnumerable<int> next, string label)
    {
        HashSet<int> target = [.. next];
        List<int> remove = [];
        foreach (var t in Selected)
        {
            if (!target.Contains(t))
            {
                remove.Add(t);
            }
        }
        List<int> add = [];
        foreach (var t in target)
        {
            if (!Selected.Contains(t))
            {
                add.Add(t);
            }
        }
        Apply([.. add], [.. remove], label);
    }

    public void Clear() => Apply([], [.. Selected], "clear");

    // current state is already applied externally (e.g. auto-map wrote into Selected); record the change as one undo entry against the previous snapshot
    public void RecordExternal(HashSet<int> previous, string label)
    {
        List<int> add = [];
        List<int> remove = [];
        foreach (var t in Selected)
        {
            if (!previous.Contains(t))
            {
                add.Add(t);
            }
        }
        foreach (var t in previous)
        {
            if (!Selected.Contains(t))
            {
                remove.Add(t);
            }
        }
        if (add.Count == 0 && remove.Count == 0)
        {
            return;
        }
        ++Version;
        Push(new([.. add], [.. remove], label));
    }

    private void Push(Delta d)
    {
        _undo.Add(d);
        _redo.Clear();
        if (_undo.Count > UndoDepth)
        {
            _undo.RemoveAt(0);
        }
    }

    public void BeginStroke(string label)
    {
        _strokeAdded = [];
        _strokeRemoved = [];
        _strokeLabel = label;
    }

    public void EndStroke()
    {
        if (_strokeAdded == null || _strokeRemoved == null)
        {
            return;
        }
        if (_strokeAdded.Count > 0 || _strokeRemoved.Count > 0)
        {
            Push(new([.. _strokeAdded], [.. _strokeRemoved], _strokeLabel));
        }
        _strokeAdded = null;
        _strokeRemoved = null;
    }

    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }
        var d = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        foreach (var t in d.Added)
        {
            Selected.Remove(t);
        }
        foreach (var t in d.Removed)
        {
            Selected.Add(t);
        }
        _redo.Add(d);
        ++Version;
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }
        var d = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        foreach (var t in d.Removed)
        {
            Selected.Remove(t);
        }
        foreach (var t in d.Added)
        {
            Selected.Add(t);
        }
        _undo.Add(d);
        ++Version;
    }

    public void Reset()
    {
        Selected.Clear();
        ClearHistory();
    }

    // keeps the current selection, drops undo/redo (the current state becomes the baseline)
    public void ClearHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _strokeAdded = null;
        _strokeRemoved = null;
        ++Version;
    }
}
