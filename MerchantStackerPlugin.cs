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
    public const string PluginVersion = "0.1.0";

    internal static MerchantStackerPlugin Instance { get; private set; } = null!;
    internal static ManualLogSource Log { get; private set; } = null!;

    internal static ConfigEntry<bool> Enabled = null!;
    internal static ConfigEntry<int> DpadStep = null!;
    internal static ConfigEntry<int> StickStep = null!;
    internal static ConfigEntry<float> HoldInitialDelay = null!;
    internal static ConfigEntry<float> HoldMinRepeat = null!;
    internal static ConfigEntry<float> HoldAccel = null!;

    private Harmony? _harmony;

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

        var pickerGo = new GameObject("MerchantStacker_QuantityPicker");
        DontDestroyOnLoad(pickerGo);
        pickerGo.hideFlags = HideFlags.HideAndDontSave;
        pickerGo.AddComponent<QuantityPicker>();

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(ShopPurchasePatches));
        _harmony.PatchAll(typeof(SimpleShopPatches));
        _harmony.PatchAll(typeof(MachinePurchasePatches));

        Log.LogInfo($"{PluginName} v{PluginVersion} loaded.");
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }
}
