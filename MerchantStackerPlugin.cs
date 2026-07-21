using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using MerchantStacker.Patches;
using UnityEngine;

namespace MerchantStacker;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class MerchantStackerPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "io.github.raincloudthedragon.merchantstacker";
    public const string PluginName = "MerchantStacker";
    public const string PluginVersion = "1.0.0";

    internal static MerchantStackerPlugin Instance { get; private set; } = null!;
    internal static ManualLogSource Log { get; private set; } = null!;

    internal static ConfigEntry<bool> Enabled = null!;
    internal static ConfigEntry<int> DpadStep = null!;
    internal static ConfigEntry<int> StickStep = null!;
    internal static ConfigEntry<float> HoldInitialDelay = null!;
    internal static ConfigEntry<float> HoldMinRepeat = null!;
    internal static ConfigEntry<float> HoldAccel = null!;

    private Harmony? _harmony;
    private GameObject? _pickerGo;

    private void Awake()
    {
        Instance = this;
        Log = Logger;

        Enabled = Config.Bind("General", "Enabled", true, "Enable bulk-buy quantity submenu.");
        DpadStep = Config.Bind("Controls", "DpadStep", 1, "Quantity change for d-pad up/down.");
        StickStep = Config.Bind("Controls", "StickStep", 5, "Quantity change for right stick up/down.");
        HoldInitialDelay = Config.Bind("Controls", "HoldInitialDelay", 0.35f, "Seconds before hold-repeat starts.");
        HoldMinRepeat = Config.Bind("Controls", "HoldMinRepeat", 0.05f, "Fastest hold-repeat interval in seconds.");
        HoldAccel = Config.Bind("Controls", "HoldAccel", 0.85f, "Multiply repeat interval by this each step while held (< 1 accelerates).");

        _pickerGo = new GameObject("MerchantStacker_QuantityPicker");
        DontDestroyOnLoad(_pickerGo);
        _pickerGo.hideFlags = HideFlags.HideAndDontSave;
        _pickerGo.AddComponent<QuantityPicker>();

        _harmony = new Harmony(PluginGuid);
        TryPatch(typeof(ShopSelectionCachePatches));
        // Show hooks first / separate — must not die if an event patch fails.
        TryPatch(typeof(ShopConfirmShowPatches));
        TryPatch(typeof(ShopConfirmListPatches));
        TryPatch(typeof(ConfirmDialogPatches));
        TryPatch(typeof(InventoryPaneInputPatches));
        TryPatch(typeof(ShopPurchasePatches));
        TryPatch(typeof(SimpleShopPatches));
        TryPatch(typeof(MachinePurchasePatches));

        Log.LogInfo($"{PluginName} v{PluginVersion} loaded (F6 ScriptEngine reload supported).");
    }

    private void TryPatch(System.Type type)
    {
        try
        {
            _harmony!.PatchAll(type);
            Log.LogInfo($"Patched {type.Name}");
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Failed to patch {type.Name}: {ex}");
        }
    }

    /// <summary>
    /// ScriptEngine calls this on F6 reload — must unpatch Harmony and destroy loose objects.
    /// </summary>
    private void OnDestroy()
    {
        try
        {
            QuantityPicker.Instance?.ShutdownForReload();
        }
        catch (System.Exception ex)
        {
            Log?.LogWarning($"QuantityPicker unload: {ex.Message}");
        }

        if (_pickerGo != null)
        {
            DestroyImmediate(_pickerGo);
            _pickerGo = null;
        }

        PurchaseBatcher.ClearPendingQuantity();
        PurchaseBatcher.ExpectingFsmPurchase = false;
        PurchaseBatcher.ClearShopPurchaseSuppression();

        try
        {
            InventoryPaneInput.IsInputBlocked = false;
        }
        catch
        {
            // Game may already be tearing down.
        }

        _harmony?.UnpatchSelf();
        _harmony = null;

        if (Instance == this)
        {
            Instance = null!;
        }

        Log?.LogInfo($"{PluginName} unloaded.");
    }
}
