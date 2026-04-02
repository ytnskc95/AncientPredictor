using System.Reflection;
using System.Text;
using System.Text.Json;
using Godot;
using HarmonyLib;
using AncientPredictor.UI;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace AncientPredictor;

[ModInitializer("Initialize")]
public static class AncientPredictorMod
{
    private static string _version = "1.0.0";
    private static string ModId => "AncientPredictor";

    public static Logger Logger { get; } = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        LoadVersionFromFile();

        try
        {
            // Apply Harmony patches to hook into the map screen lifecycle
            Harmony harmony = new(ModId);
            harmony.PatchAll();

            // Connect to ProcessFrame so we can inject the overlay once NRun is available
            var tree = (SceneTree)Engine.GetMainLoop();
            tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(TryInjectOverlay));

            Logger.Info($"[AncientPredictor] v{_version} initialized successfully.");
        }
        catch (Exception ex)
        {
            Logger.Error($"[AncientPredictor] Failed to initialize: {ex}");
        }
    }

    // ------------------------------------------------------------------
    // Version loading (from AncientPredictor.json)
    // ------------------------------------------------------------------
    private static void LoadVersionFromFile()
    {
        try
        {
            var location = Assembly.GetExecutingAssembly().Location;
            var directory = Path.GetDirectoryName(location) ?? AppContext.BaseDirectory;
            var path = Path.Combine(directory, "AncientPredictor.json");

            if (File.Exists(path))
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("version", out var versionElement))
                {
                    _version = versionElement.GetString() ?? "1.0.0";
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[AncientPredictor] Error reading version: {ex}");
        }
    }

    // ------------------------------------------------------------------
    // Overlay injection: wait for NRun to become available, then add
    // the AncientOverlay as a child of the scene tree (once).
    // ------------------------------------------------------------------
    private static bool _overlayInjected;

    private static void TryInjectOverlay()
    {
        if (_overlayInjected) return;

        // NRun.Instance is the run scene root, which hosts GlobalUi > MapScreen
        var nRun = NRun.Instance;
        if (nRun == null) return;

        var mapScreen = NMapScreen.Instance;
        if (mapScreen == null) return;

        // Create the overlay and add it to the CanvasLayer above the map
        var overlay = new AncientOverlay();
        overlay.Name = "AncientPredictorOverlay";

        // Add to the NRun node (which is the parent of the entire run UI).
        // This ensures our overlay lives for the duration of the run.
        nRun.AddChild(overlay);

        _overlayInjected = true;
        Logger.Info("[AncientPredictor] Overlay injected into the scene tree.");

        // Connect to map visibility changes
        mapScreen.Connect(CanvasItem.SignalName.VisibilityChanged,
            Callable.From(OnMapVisibilityChanged));

        // Also connect the map screen Opened/Closed signals
        mapScreen.Connect(NMapScreen.SignalName.Opened,
            Callable.From(OnMapOpened));
        mapScreen.Connect(NMapScreen.SignalName.Closed,
            Callable.From(OnMapClosed));

        // If map is already visible, show immediately
        if (mapScreen.IsOpen)
        {
            overlay.ShowOnMap();
        }
    }

    private static void OnMapVisibilityChanged()
    {
        var overlay = AncientOverlay.Instance;
        if (overlay == null) return;

        var mapScreen = NMapScreen.Instance;
        if (mapScreen == null) return;

        if (mapScreen.Visible && mapScreen.IsOpen)
            overlay.ShowOnMap();
        else
            overlay.HideFromMap();
    }

    private static void OnMapOpened()
    {
        AncientOverlay.Instance?.ShowOnMap();
    }

    private static void OnMapClosed()
    {
        AncientOverlay.Instance?.HideFromMap();
    }

    /// <summary>
    /// Called when the run is cleaned up (after game over, abandon, etc.).
    /// Removes the overlay from the scene tree.
    /// </summary>
    public static void OnRunCleanUp()
    {
        var overlay = AncientOverlay.Instance;
        if (overlay != null)
        {
            overlay.QueueFree();
        }
        _overlayInjected = false;
    }
}

// ==========================================================================
// Harmony Patches
// ==========================================================================

/// <summary>
/// Remove this mod from "gameplay relevant" mod list so it doesn't
/// affect save files or make saves "modded".
/// </summary>
[HarmonyPatch(typeof(ModManager), nameof(ModManager.GetGameplayRelevantModNameList))]
internal class ModManagerPatch
{
    private static void Postfix(ref List<string>? __result)
    {
        if (__result == null) return;
        __result.RemoveAll(name => name.StartsWith("AncientPredictor"));
        if (__result.Count == 0)
            __result = null;
    }
}

/// <summary>
/// Ensure the profile directory doesn't get the "modded/" prefix for our mod.
/// </summary>
[HarmonyPatch(typeof(UserDataPathProvider))]
internal class ProfileDirPatch
{
    [HarmonyPatch("GetProfileDir")]
    [HarmonyPostfix]
    private static void GetProfileDirPostfix(ref string __result)
    {
        if (__result.StartsWith("modded/"))
        {
            var text = __result;
            var length = "modded/".Length;
            __result = text.Substring(length, text.Length - length);
        }
    }
}

/// <summary>
/// Hook into RunManager.CleanUp to remove the overlay when the run ends.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
internal class RunCleanUpPatch
{
    private static void Prefix()
    {
        try
        {
            AncientPredictorMod.OnRunCleanUp();
        }
        catch (Exception ex)
        {
            AncientPredictorMod.Logger.Error($"[AncientPredictor] Error in CleanUp patch: {ex}");
        }
    }
}

/// <summary>
/// Mark predictions as stale when entering a new act (map is regenerated).
/// Uses Prefix because EnterNextAct is async; Prefix runs on the synchronous preamble.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterNextAct))]
internal class EnterNextActPatch
{
    private static void Prefix()
    {
        try
        {
            AncientOverlay.Instance?.MarkStale();
        }
        catch (Exception ex)
        {
            AncientPredictorMod.Logger.Error($"[AncientPredictor] Error in EnterNextAct patch: {ex}");
        }
    }
}

/// <summary>
/// Also mark stale when a saved run is loaded (act data may change).
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
internal class RunLaunchPatch
{
    private static void Postfix()
    {
        try
        {
            AncientOverlay.Instance?.MarkStale();
        }
        catch (Exception ex)
        {
            AncientPredictorMod.Logger.Error($"[AncientPredictor] Error in Launch patch: {ex}");
        }
    }
}
