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
        return TryIntercept(__instance, subItemIndex: 0);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SetShopItemPurchasedV2), nameof(SetShopItemPurchasedV2.OnEnter))]
    private static bool SetPurchasedV2Prefix(SetShopItemPurchasedV2 __instance)
    {
        int sub = __instance.SubItemIndex != null ? __instance.SubItemIndex.Value : 0;
        return TryIntercept(__instance, sub);
    }

    private static bool TryIntercept(SetShopItemPurchased action, int subItemIndex)
    {
        if (!MerchantStackerPlugin.Enabled.Value || PurchaseBatcher.IsBatching || QuantityPicker.Instance.IsOpen)
        {
            return true;
        }

        action.IsWaitingBool.Value = false;
        GameObject? target = action.Target.GetSafe(action);
        if (target == null)
        {
            return true;
        }

        var stats = target.GetComponent<ShopItemStats>();
        if (stats == null || !Eligibility.IsBulkEligible(stats.Item))
        {
            return true;
        }

        int max = Eligibility.GetMaxQuantity(stats.Item);
        if (max <= 1)
        {
            return true;
        }

        action.IsWaitingBool.Value = true;
        string title = stats.Item.DisplayName;
        int cost = stats.Item.Cost;
        var currency = stats.Item.CurrencyType;

        QuantityPicker.Instance.Open(
            title,
            cost,
            currency,
            max,
            onConfirm: qty =>
            {
                PurchaseBatcher.BuyShopItem(stats, qty, subItemIndex, () =>
                {
                    action.IsWaitingBool.Value = false;
                    if (GameCameras.instance != null)
                    {
                        GameCameras.instance.HUDIn();
                    }
                });
            },
            onCancel: () =>
            {
                action.IsWaitingBool.Value = false;
                EventRegister.SendEvent(EventRegisterEvents.ResetShopWindow);
                if (GameCameras.instance != null)
                {
                    GameCameras.instance.HUDIn();
                }
            });

        action.Finish();
        return false;
    }

    private static bool TryIntercept(SetShopItemPurchasedV2 action, int subItemIndex)
    {
        if (!MerchantStackerPlugin.Enabled.Value || PurchaseBatcher.IsBatching || QuantityPicker.Instance.IsOpen)
        {
            return true;
        }

        action.IsWaitingBool.Value = false;
        GameObject? target = action.Target.GetSafe(action);
        if (target == null)
        {
            return true;
        }

        var stats = target.GetComponent<ShopItemStats>();
        if (stats == null || !Eligibility.IsBulkEligible(stats.Item))
        {
            return true;
        }

        int max = Eligibility.GetMaxQuantity(stats.Item);
        if (max <= 1)
        {
            return true;
        }

        action.IsWaitingBool.Value = true;
        string title = stats.Item.DisplayName;
        int cost = stats.Item.Cost;
        var currency = stats.Item.CurrencyType;

        QuantityPicker.Instance.Open(
            title,
            cost,
            currency,
            max,
            onConfirm: qty =>
            {
                PurchaseBatcher.BuyShopItem(stats, qty, subItemIndex, () =>
                {
                    action.IsWaitingBool.Value = false;
                    if (GameCameras.instance != null)
                    {
                        GameCameras.instance.HUDIn();
                    }
                });
            },
            onCancel: () =>
            {
                action.IsWaitingBool.Value = false;
                EventRegister.SendEvent(EventRegisterEvents.ResetShopWindow);
                if (GameCameras.instance != null)
                {
                    GameCameras.instance.HUDIn();
                }
            });

        action.Finish();
        return false;
    }
}
