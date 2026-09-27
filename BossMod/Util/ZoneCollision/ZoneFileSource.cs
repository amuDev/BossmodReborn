namespace BossMod;

// minimal file access edge for the offline zone loader, so the plugin (Dalamud data manager) and the offline harness can share the loader
public interface IZoneFileSource
{
    bool Exists(string path);
    byte[]? Read(string path); // whole file, null when missing
}

public sealed class LuminaZoneFileSource(Lumina.GameData data) : IZoneFileSource
{
    public bool Exists(string path) => data.FileExists(path);
    public byte[]? Read(string path) => data.GetFile<Lumina.Data.FileResource>(path)?.Data; // null for a path that is not in the indices
}
