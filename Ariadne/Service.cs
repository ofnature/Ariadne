using Dalamud.Game;
using Dalamud.Game.ClientState.Objects;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Ariadne;

/// <summary>
/// Dalamud services, injected once at load.
///
/// <para>
/// <b>This must be a class of its own, never the plugin.</b>
/// <c>IDalamudPluginInterface.Create&lt;T&gt;()</c> does not merely populate <c>[PluginService]</c>
/// members — it <i>constructs an instance of T</i>. Passing the plugin type re-enters the plugin's
/// own constructor, which calls <c>Create</c> again, and the load recurses until the client dies.
/// It fails silently: Dalamud logs the load starting and then nothing at all, because no exception
/// ever escapes. A plain holder like this has a trivial constructor, so creating one is free and
/// the recursion cannot exist.
/// </para>
/// </summary>
internal sealed class Service
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider Hook { get; private set; } = null!;
    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IAetheryteList AetheryteList { get; private set; } = null!;

    // Same helpers vnavmesh's Service exposes — the vendored layout code depends on them.
    internal static Lumina.Excel.ExcelSheet<T>? LuminaSheet<T>() where T : struct, Lumina.Excel.IExcelRow<T>
        => DataManager.GameData.GetExcelSheet<T>(Lumina.Data.Language.English);

    internal static T? LuminaRow<T>(uint row) where T : struct, Lumina.Excel.IExcelRow<T>
        => LuminaSheet<T>()?.GetRowOrDefault(row);
}
