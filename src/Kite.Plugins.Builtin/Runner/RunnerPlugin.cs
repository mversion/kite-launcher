using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kite.Plugins;
using Kite.Plugins.Builtin.Everything;

namespace Kite.Plugins.Builtin.Runner;

public sealed class RunnerCommand
{
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    /// <summary>Argument template; each $$ is replaced by the next Tab-separated input.</summary>
    public string Args { get; set; } = "";
}

/// <summary>Custom commands, Launchy Runner-compatible: "cmd ⇥ dir" runs cmd.exe /K dir.</summary>
public sealed class RunnerPlugin : IKitePlugin
{
    public const string PluginId = "runner";
    /// <summary>Settings flag: the one-time "e for Everything" setup has run (so a deleted "e" stays deleted).</summary>
    public const string EverythingCheckedKey = "everythingCommandChecked";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private IPluginContext _ctx = null!;
    private List<RunnerCommand> _commands = [];

    public string Id => PluginId;
    public string Name => "Runner";
    public string Description => "Launch custom commands with $$ argument placeholders";

    public IReadOnlyList<RunnerCommand> Commands => _commands;

    public void Init(IPluginContext context)
    {
        _ctx = context;
        var node = context.Settings["commands"];
        _commands = node?.Deserialize<List<RunnerCommand>>(JsonOptions) ?? DefaultCommands();
        var changed = node is null;
        if (context.Settings[EverythingCheckedKey]?.GetValue<bool>() != true)
        {
            changed |= AddEverythingCommand(_commands, EverythingClient.LocateExecutable());
            context.Settings[EverythingCheckedKey] = true;
            changed = true;
        }
        if (changed)
        {
            context.Settings["commands"] = JsonSerializer.SerializeToNode(_commands, JsonOptions);
            context.SaveSettings();
        }
    }

    /// <summary>
    /// When Everything is installed, adds the Launchy-style "e" command for it (Everything's own plugin then
    /// shows "e ⇥ query" results inline), unless there's already an "e" or a command that runs Everything.
    /// </summary>
    public static bool AddEverythingCommand(List<RunnerCommand> commands, string? everythingExe)
    {
        if (string.IsNullOrEmpty(everythingExe)) return false;
        if (commands.Any(c => c.Name.Equals("e", StringComparison.OrdinalIgnoreCase) ||
                              Path.GetFileName(c.File.Trim('"')).Equals("Everything.exe", StringComparison.OrdinalIgnoreCase)))
            return false;
        commands.Add(new RunnerCommand { Name = "e", File = everythingExe, Args = "-search \"$$\"" });
        return true;
    }

    public static List<RunnerCommand> DefaultCommands() =>
    [
        new() { Name = "cmd", File = Path.Combine(Environment.SystemDirectory, "cmd.exe"), Args = "/K $$" },
    ];

    public IEnumerable<CatItem> GetCatalog() =>
        _commands.Where(c => c.Name.Length > 0 && c.File.Length > 0).Select(c => new CatItem
        {
            Name = c.Name,
            Target = c.Name,
            Data = c.File,
            Description = $"{c.File} {c.Args}".Trim(),
            IconPath = Environment.ExpandEnvironmentVariables(c.File),
        });

    public bool Launch(CatItem item, Query query, LaunchMode mode)
    {
        var cmd = _commands.FirstOrDefault(c => c.Name == item.Target);
        if (cmd is null) return false;
        var file = Environment.ExpandEnvironmentVariables(cmd.File);
        if (mode == LaunchMode.OpenContainingFolder)
            return false;
        _ctx.RunProgram(file, FillArguments(cmd.Args, query.Arguments), mode == LaunchMode.RunAsAdmin);
        return true;
    }

    /// <summary>
    /// Replaces $$ markers with the inputs in order. A single $$ receives all inputs joined by
    /// spaces; with no $$ the inputs are appended; unused markers are removed.
    /// </summary>
    public static string FillArguments(string template, IReadOnlyList<string> inputs)
    {
        var markers = CountMarkers(template);
        if (markers == 0)
            return inputs.Count == 0 ? template : $"{template} {string.Join(' ', inputs)}".Trim();
        if (markers == 1)
            return template.Replace("$$", string.Join(' ', inputs)).Trim();

        var sb = new StringBuilder();
        var next = 0;
        for (var i = 0; i < template.Length; i++)
        {
            if (i + 1 < template.Length && template[i] == '$' && template[i + 1] == '$')
            {
                if (next < inputs.Count) sb.Append(inputs[next++]);
                i++;
            }
            else sb.Append(template[i]);
        }
        return sb.ToString().Trim();
    }

    private static int CountMarkers(string s)
    {
        int count = 0, i = 0;
        while ((i = s.IndexOf("$$", i, StringComparison.Ordinal)) >= 0) { count++; i += 2; }
        return count;
    }

    public static JsonObject CreateSettings(IEnumerable<RunnerCommand> commands) =>
        new() { ["commands"] = JsonSerializer.SerializeToNode(commands.ToList(), JsonOptions) };
}
