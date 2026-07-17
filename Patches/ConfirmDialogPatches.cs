using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MerchantStacker.Patches;

/// <summary>
/// Optional arming when something else opens DialogueYesNoBox (not merchant shop panes).
/// Merchant shops use in-pane qty via ShopConfirmListPatches — never open a second box.
/// </summary>
[HarmonyPatch]
internal static class ConfirmDialogPatches
{
    private static readonly FieldInfo CurrentYesField =
        AccessTools.Field(typeof(YesNoBox), "currentYes");

    private static readonly FieldInfo CurrentNoField =
        AccessTools.Field(typeof(YesNoBox), "currentNo");

    private static readonly FieldInfo RequiredCurrencyAmountField =
        AccessTools.Field(typeof(DialogueYesNoBox), "requiredCurrencyAmount");

    private static readonly FieldInfo WillGetItemField =
        AccessTools.Field(typeof(DialogueYesNoBox), "willGetItem");

    [HarmonyPostfix]
    [HarmonyPatch(typeof(YesNoBox), "InternalOpen")]
    private static void InternalOpenPostfix(YesNoBox __instance)
    {
        try
        {
            if (!MerchantStackerPlugin.Enabled.Value
                || PurchaseBatcher.IsBatching
                || QuantityPicker.SuppressHijack
                || QuantityPicker.Instance == null
                || QuantityPicker.Instance.IsOpen
                || __instance is not DialogueYesNoBox box)
            {
                return;
            }

            // Skip if a merchant shop pane is open — qty belongs in that pane.
            if (IsMerchantShopOpen())
            {
                return;
            }

            int cost = RequiredCurrencyAmountField?.GetValue(box) as int? ?? 0;
            if (cost <= 0)
            {
                return;
            }

            ShopItemStats? stats = ShopSelectionCache.ResolveStats();
            if (stats?.Item == null
                || !Eligibility.IsBulkEligible(stats.Item)
                || Eligibility.GetMaxQuantity(stats.Item) <= 1)
            {
                return;
            }

            CollectableItem? collectable =
                WillGetItemField?.GetValue(box) as CollectableItem
                ?? stats.Item.Item as CollectableItem;
            if (collectable == null)
            {
                return;
            }

            QuantityPicker.Instance.Hijack(
                box,
                title: stats.Item.DisplayName,
                item: collectable,
                unitCost: stats.Item.Cost > 0 ? stats.Item.Cost : cost,
                currency: stats.Item.CurrencyType,
                maxQuantity: Eligibility.GetMaxQuantity(stats.Item),
                originalYes: CurrentYesField?.GetValue(box) as Action,
                originalNo: CurrentNoField?.GetValue(box) as Action);
        }
        catch (Exception ex)
        {
            MerchantStackerPlugin.Log.LogError($"ConfirmDialogPatches: {ex}");
        }
    }

    private static bool IsMerchantShopOpen()
    {
        try
        {
            foreach (ShopMenuStock stock in UnityEngine.Object.FindObjectsByType<ShopMenuStock>(
                         FindObjectsSortMode.None))
            {
                if (stock != null && stock.isActiveAndEnabled)
                {
                    return true;
                }
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }
}
