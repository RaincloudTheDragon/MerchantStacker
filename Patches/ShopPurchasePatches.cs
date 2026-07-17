using System;
using HarmonyLib;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace MerchantStacker.Patches;

/// <summary>
/// Reliable merchant qty entry: after the shop's own yes/no, open in-pane qty
/// (never DialogueYesNoBox). Also batches when PendingQuantity &gt; 1.
/// </summary>
[HarmonyPatch]
internal static class ShopPurchasePatches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SetShopItemPurchased), nameof(SetShopItemPurchased.OnEnter))]
    private static bool SetPurchasedPrefix(SetShopItemPurchased __instance)
    {
        return TryIntercept(
            __instance.Target.GetSafe(__instance),
            subItemIndex: 0,
            setWaiting: v => __instance.IsWaitingBool.Value = v,
            finish: __instance.Finish,
            onDone: () =>
            {
                __instance.IsWaitingBool.Value = false;
                GameCameras.instance?.HUDIn();
            });
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SetShopItemPurchasedV2), nameof(SetShopItemPurchasedV2.OnEnter))]
    private static bool SetPurchasedV2Prefix(SetShopItemPurchasedV2 __instance)
    {
        int sub = __instance.SubItemIndex != null ? __instance.SubItemIndex.Value : 0;
        return TryIntercept(
            __instance.Target.GetSafe(__instance),
            sub,
            setWaiting: v => __instance.IsWaitingBool.Value = v,
            finish: __instance.Finish,
            onDone: () =>
            {
                __instance.IsWaitingBool.Value = false;
                GameCameras.instance?.HUDIn();
            });
    }

    private static bool TryIntercept(
        GameObject? target,
        int subItemIndex,
        Action<bool> setWaiting,
        Action finish,
        Action onDone)
    {
        if (!MerchantStackerPlugin.Enabled.Value || PurchaseBatcher.IsBatching)
        {
            return true;
        }

        if (target == null)
        {
            MerchantStackerPlugin.Log.LogInfo("SetShopItemPurchased: null target");
            return true;
        }

        var stats = target.GetComponent<ShopItemStats>();
        MerchantStackerPlugin.Log.LogInfo(
            $"SetShopItemPurchased: {(stats?.Item != null ? stats.Item.DisplayName : target.name)} pending={PurchaseBatcher.PendingQuantity}");

        if (stats?.Item == null)
        {
            return true;
        }

        ShopSelectionCache.Remember(stats);

        int pending = PurchaseBatcher.ConsumePendingQuantity();
        if (pending > 1)
        {
            setWaiting(true);
            PurchaseBatcher.BuyShopItem(stats, pending, subItemIndex, onDone);
            finish();
            return false;
        }

        // Bulk item and no qty session yet → open in-pane qty (shop yes/no already accepted).
        if (Eligibility.IsBulkEligible(stats.Item)
            && Eligibility.GetMaxQuantity(stats.Item) > 1
            && QuantityPicker.Instance != null
            && !QuantityPicker.Instance.IsOpen)
        {
            Transform shopRoot = stats.transform;
            var stock = stats.GetComponentInParent<ShopMenuStock>();
            if (stock != null)
            {
                shopRoot = stock.transform;
            }
            else
            {
                foreach (ShopMenuStock s in UnityEngine.Object.FindObjectsByType<ShopMenuStock>(FindObjectsSortMode.None))
                {
                    if (s != null && s.isActiveAndEnabled)
                    {
                        shopRoot = s.transform;
                        break;
                    }
                }
            }

            var pane = stats.GetComponentInParent<InventoryPaneBase>();
            if (pane != null)
            {
                shopRoot = pane.transform;
            }

            MerchantStackerPlugin.Log.LogInfo($"Purchase → in-pane qty: {stats.Item.DisplayName}");

            setWaiting(true);
            finish();
            QuantityPicker.Instance.SetPurchaseDoneCallback(onDone);
            QuantityPicker.Instance.OpenInShop(
                shopRoot: shopRoot,
                title: stats.Item.DisplayName,
                item: stats.Item.Item as CollectableItem,
                unitCost: stats.Item.Cost,
                currency: stats.Item.CurrencyType,
                maxQuantity: Eligibility.GetMaxQuantity(stats.Item),
                stats: stats);
            return false;
        }

        return true;
    }
}
