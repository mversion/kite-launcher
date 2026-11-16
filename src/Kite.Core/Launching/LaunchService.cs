using Kite.Core.Plugins;
using Kite.Core.Search;
using Kite.Plugins;

namespace Kite.Core.Launching;

/// <summary>Routes a launch to the owning plugin or the shell, and teaches the history.</summary>
public sealed class LaunchService
{
    private readonly PluginHost _plugins;
    private readonly History _history;
    private readonly Func<string, bool> _isCatalogKey;
    private readonly Action _historyChanged;

    public LaunchService(PluginHost plugins, History history, Func<string, bool> isCatalogKey, Action historyChanged)
    {
        _plugins = plugins;
        _history = history;
        _isCatalogKey = isCatalogKey;
        _historyChanged = historyChanged;
    }

    /// <param name="selected">The highlighted result row.</param>
    /// <param name="query">Current query (owner chip + arguments, or plain text).</param>
    /// <param name="ownerTypedText">What the user typed before pressing Tab on the owner, used for learning.</param>
    public void Launch(CatItem selected, Query query, LaunchMode mode, string? ownerTypedText = null)
    {
        if (query.Owner is { } owner)
        {
            var isOwnerRow = string.Equals(selected.Key, owner.Key, StringComparison.OrdinalIgnoreCase);
            // A plugin that took over a plain owner (e.g. "Everything.lnk ⇥ invoice") launches it its own way.
            var handled = isOwnerRow && _plugins.OwnerHandler(owner) is { } handler && handler.Launch(selected, query, mode);
            if (!handled && selected.PluginId is not null)
                handled = _plugins.Launch(selected, query, mode);
            if (!handled)
                ShellLauncher.Launch(isOwnerRow ? owner.Target : selected.Target, isOwnerRow ? query.ArgumentText : null, mode);
            // What was picked from the owner's results ("e ⇥ password reset", "ente ⇥ microsoft") comes first next time.
            if (!isOwnerRow && (selected.PluginId is not null || Path.IsPathRooted(selected.Target)))
                _history.RecordPick(owner.Key, query.ArgumentText, selected);
            Learn(ownerTypedText ?? "", owner);
            return;
        }

        if (mode == LaunchMode.OpenContainingFolder)
        {
            ShellLauncher.Reveal(selected.Target);
            return;
        }

        if (selected.PluginId is null || !_plugins.Launch(selected, query, mode))
            ShellLauncher.Launch(selected.Target, null, mode);
        // A plugin result picked from several (e.g. one of four Microsoft codes) comes first next time
        // the same thing is typed; catalog items are learned the Launchy way below.
        if (selected.PluginId is not null && !_isCatalogKey(selected.Key))
        {
            _history.RecordPick(NoOwner, query.Text, selected);
            _historyChanged();
        }
        Learn(query.Text, selected);
    }

    /// <summary>Owner key used for picks made without a Tab owner.</summary>
    public const string NoOwner = "";

    private void Learn(string typed, CatItem item)
    {
        // Transient plugin results (calculator answers, web fallbacks) are not worth remembering.
        if (!_isCatalogKey(item.Key) && !Path.IsPathRooted(item.Target)) return;
        _history.Record(typed, item.Key, item.Name);
        _historyChanged();
    }
}
