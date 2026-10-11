using Kite.Plugins.Builtin.Calcy;
using Kite.Plugins.Builtin.Everything;
using Kite.Plugins.Builtin.Runner;
using Kite.Plugins.Builtin.Weby;

namespace Kite.Plugins.Builtin;

public static class BuiltinPlugins
{
    public static IEnumerable<IKitePlugin> CreateAll() =>
    [
        new CalcyPlugin(),
        new WebyPlugin(),
        new RunnerPlugin(),
        new EverythingPlugin(),
    ];
}
