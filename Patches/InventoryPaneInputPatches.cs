using HarmonyLib;

namespace MerchantStacker.Patches;

/// <summary>
/// While the quantity picker owns the pad, stop inventory/confirm panes from reading input.
/// </summary>
[HarmonyPatch(typeof(InventoryPaneInput), "Update")]
internal static class InventoryPaneInputPatches
{
    [HarmonyPrefix]
    private static bool Prefix()
    {
        return QuantityPicker.Instance == null || !QuantityPicker.Instance.IsOpen;
    }
}
