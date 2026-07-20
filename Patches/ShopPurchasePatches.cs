using System;
using HarmonyLib;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace MerchantStacker.Patches;

/// <summary>
/// Merchant purchases: qty is opened when confirm appears (ShopConfirmListPatches).
/// This patch batches PendingQuantity or blocks duplicate SetShopItemPurchased.
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
            setWaiting: v =>
            {
                if (__instance.IsWaitingBool != null)
                {
                    __instance.IsWaitingBool.Value = v;
                }
            },
            finish: __instance.Finish,
            onPurchaseComplete: () =>
            {
                if (__instance.IsWaitingBool != null)
                {
                    __instance.IsWaitingBool.Value = false;
                }

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
            setWaiting: v =>
            {
                if (__instance.IsWaitingBool != null)
                {
                    __instance.IsWaitingBool.Value = v;
                }
            },
            finish: __instance.Finish,
            onPurchaseComplete: () =>
            {
                if (__instance.IsWaitingBool != null)
                {
                    __instance.IsWaitingBool.Value = false;
                }

                GameCameras.instance?.HUDIn();
            });
    }

    private static bool TryIntercept(
        GameObject? target,
        int subItemIndex,
        Action<bool> setWaiting,
        Action finish,
        Action onPurchaseComplete)
    {
        if (!MerchantStackerPlugin.Enabled.Value || PurchaseBatcher.IsBatching)
        {
            return true;
        }

        // Qty UI owns the pad — never let Yes/No purchase fire underneath.
        if (QuantityPicker.Instance != null && QuantityPicker.Instance.IsOpen)
        {
            MerchantStackerPlugin.Log.LogInfo("SetShopItemPurchased blocked (qty picker open)");
            setWaiting(false);
            finish();
            return false;
        }

        // Already bulk-bought this confirm — skip until shop resets.
        if (PurchaseBatcher.BlockShopPurchases)
        {
            MerchantStackerPlugin.Log.LogInfo("SetShopItemPurchased blocked (bulk already done)");
            setWaiting(false);
            finish();
            return false;
        }

        if (target == null)
        {
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

        PurchaseBatcher.ExpectingFsmPurchase = false;

        int pending = PurchaseBatcher.ConsumePendingQuantity();
        // Qty UI sets pending >= 1, then Submits Yes so the FSM reaches here and can
        // continue into the vanilla get-item / thank-you animation after we clear wait.
        if (pending >= 1)
        {
            setWaiting(true);
            PurchaseBatcher.BuyShopItem(
                stats,
                pending,
                subItemIndex,
                onComplete: () =>
                {
                    QuantityPicker.ShowPurchaseFeedback(stats);
                    onPurchaseComplete();
                });
            finish();
            return false;
        }

        // Bulk item reached purchase without qty UI — open qty instead of voiding the buy.
        if (Eligibility.IsBulkEligible(stats.Item) && Eligibility.GetMaxQuantity(stats.Item) > 1)
        {
            setWaiting(true);
            if (ShopConfirmListPatches.TryOpenQtyImmediate(stats))
            {
                MerchantStackerPlugin.Log.LogInfo(
                    "SetShopItemPurchased → opened late qty UI (Yes beat confirm hook)");
                // Keep FSM waiting; qty confirm buys then clears wait → get-item anim.
                QuantityPicker.Instance!.ArmShopPurchaseSession(
                    onPurchaseComplete: onPurchaseComplete,
                    onCancelPurchase: () =>
                    {
                        setWaiting(false);
                        PurchaseBatcher.EndShopPurchaseBlock();
                        EventRegister.SendEvent(EventRegisterEvents.ResetShopWindow);
                        GameCameras.instance?.HUDIn();
                    });
                finish();
                return false;
            }

            // Last resort: allow a single vanilla purchase rather than doing nothing.
            MerchantStackerPlugin.Log.LogWarning(
                "SetShopItemPurchased: qty UI unavailable — allowing single vanilla buy");
            setWaiting(false);
            return true;
        }

        return true;
    }
}
