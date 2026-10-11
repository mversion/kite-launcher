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
            if (selected.PluginId is not null)
            {
                if (!_plugins.Launch(selected, query, mode) && isOwnerRow)
                    ShellLauncher.Launch(owner.Target, query.ArgumentText, mode);
            }
            else
            {
                ShellLauncher.Launch(selected.Target, isOwnerRow ? query.ArgumentText : null, mode);
            }
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
        Learn(query.Text, selected);
    }

    private void Learn(string typed, CatItem item)
    {
        // Transient plugin results (calculator answers, web fallbacks) are not worth remembering.
        if (!_isCatalogKey(item.Key) && !Path.IsPathRooted(item.Target)) return;
        _history.Record(typed, item.Key, item.Name);
        _historyChanged();
    }
}
