using HarmonyLib;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace MerchantStacker.Patches;

[HarmonyPatch]
internal static class ShopPurchasePatches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SetShopItemPurchased), nameof(SetShopItemPurchased.OnEnter))]
    private static bool SetPurchasedPrefix(SetShopItemPurchased __instance)
    {
        return TryBatch(__instance, subItemIndex: 0);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SetShopItemPurchasedV2), nameof(SetShopItemPurchasedV2.OnEnter))]
    private static bool SetPurchasedV2Prefix(SetShopItemPurchasedV2 __instance)
    {
        int sub = __instance.SubItemIndex != null ? __instance.SubItemIndex.Value : 0;
        return TryBatch(__instance, sub);
    }

    private static bool TryBatch(SetShopItemPurchased action, int subItemIndex)
    {
        int qty = PurchaseBatcher.ConsumePendingQuantity();
        if (qty <= 1 || PurchaseBatcher.IsBatching)
        {
            return true;
        }

        GameObject? target = action.Target.GetSafe(action);
        if (target == null)
        {
            return true;
        }

        var stats = target.GetComponent<ShopItemStats>();
        if (stats == null || stats.Item == null)
        {
            return true;
        }

        action.IsWaitingBool.Value = true;
        PurchaseBatcher.BuyShopItem(stats, qty, subItemIndex, () =>
        {
            action.IsWaitingBool.Value = false;
            if (GameCameras.instance != null)
            {
                GameCameras.instance.HUDIn();
            }
        });
        action.Finish();
        return false;
    }

    private static bool TryBatch(SetShopItemPurchasedV2 action, int subItemIndex)
    {
        int qty = PurchaseBatcher.ConsumePendingQuantity();
        if (qty <= 1 || PurchaseBatcher.IsBatching)
        {
            return true;
        }

        GameObject? target = action.Target.GetSafe(action);
        if (target == null)
        {
            return true;
        }

        var stats = target.GetComponent<ShopItemStats>();
        if (stats == null || stats.Item == null)
        {
            return true;
        }

        action.IsWaitingBool.Value = true;
        PurchaseBatcher.BuyShopItem(stats, qty, subItemIndex, () =>
        {
            action.IsWaitingBool.Value = false;
            if (GameCameras.instance != null)
            {
                GameCameras.instance.HUDIn();
            }
        });
        action.Finish();
        return false;
    }
}
