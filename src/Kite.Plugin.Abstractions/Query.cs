namespace Kite.Plugins;

/// <summary>
/// The user's input. Launchy splits input with Tab; Kite shows each completed
/// segment as a chip. <see cref="Owner"/> is the item chosen for the first segment
/// (e.g. the "E-Mail" Weby site) and <see cref="Arguments"/> are the segments after it.
/// </summary>
public sealed class Query
{
    public Query(string text, CatItem? owner = null, IReadOnlyList<string>? arguments = null)
    {
        Text = text;
        Owner = owner;
        Arguments = arguments ?? Array.Empty<string>();
    }

    /// <summary>Text currently being typed (the last segment).</summary>
    public string Text { get; }

    /// <summary>Item selected for the first segment, if the user pressed Tab.</summary>
    public CatItem? Owner { get; }

    /// <summary>Completed segments after the owner, followed by <see cref="Text"/> when non-empty.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>All arguments joined by a space; convenient for single-argument plugins.</summary>
    public string ArgumentText => string.Join(' ', Arguments);

    public bool HasOwner => Owner is not null;
}

public enum LaunchMode
{
    /// <summary>Enter.</summary>
    Default,
    /// <summary>Shift+Enter: plugin-specific alternate action.</summary>
    Alternate,
    /// <summary>Ctrl+Shift+Enter.</summary>
    RunAsAdmin,
    /// <summary>Ctrl+Enter.</summary>
    OpenContainingFolder,
}
