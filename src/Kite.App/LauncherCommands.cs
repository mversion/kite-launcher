namespace Kite.App;

/// <summary>App-level actions reachable from the launcher's right-click menu and shortcuts.</summary>
public sealed class LauncherCommands
{
    public required Action RebuildCatalog { get; init; }
    public required Action ReloadSkin { get; init; }
    public required Action OpenOptions { get; init; }
    public required Action Exit { get; init; }
    public required Func<IEnumerable<string>> ListSkins { get; init; }
    public required Action<string> SelectSkin { get; init; }
}
