using System;
using HarmonyLib;
using UnityEngine;

namespace MerchantStacker.Patches;

/// <summary>
/// Diagnostics + best-effort intercept of shop "UI CONFIRM".
/// Primary merchant qty path is purchase-time OpenInShop in ShopPurchasePatches —
/// that runs even when the shop pane does not use InventoryPaneInput.
/// </summary>
[HarmonyPatch]
internal static class ShopConfirmListPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(
        typeof(FSMUtility),
        nameof(FSMUtility.SendEventToGameObject),
        new[] { typeof(GameObject), typeof(string), typeof(bool) })]
    private static bool SendEventPrefix(GameObject go, string eventName)
    {
        if (string.IsNullOrEmpty(eventName) || go == null)
        {
            return true;
        }

        // Broad breadcrumb while we learn the real shop confirm event.
        if (eventName.StartsWith("UI ", StringComparison.Ordinal))
        {
            bool shop = FindShopStock(go) != null;
            if (shop || eventName == "UI CONFIRM")
            {
                MerchantStackerPlugin.Log.LogInfo(
                    $"FSM event '{eventName}' → '{go.name}' shopPane={shop}");
            }
        }

        if (eventName != "UI CONFIRM"
            || !MerchantStackerPlugin.Enabled.Value
            || PurchaseBatcher.IsBatching
            || QuantityPicker.Instance == null)
        {
            return true;
        }

        if (QuantityPicker.Instance.IsOpen)
        {
            return false;
        }

        ShopMenuStock? stock = FindShopStock(go);
        if (stock == null)
        {
            return true;
        }

        return !TryOpenInShopQty(stock, go);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(InventoryPaneInput), "PressSubmit")]
    private static void PressSubmitLog(InventoryPaneInput __instance)
    {
        MerchantStackerPlugin.Log.LogInfo(
            $"PressSubmit on '{__instance.gameObject.name}' shop={FindShopStock(__instance.gameObject) != null}");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ShopMenuStock), "BuildItemList")]
    private static void BuildItemListPostfix(ShopMenuStock __instance)
    {
        MerchantStackerPlugin.Log.LogInfo(
            $"Shop stock built: '{__instance.name}' items={__instance.GetItemCount()}");
    }

    internal static bool TryOpenInShopQty(ShopMenuStock stock, GameObject? eventGo)
    {
        ShopItemStats? stats = ShopSelectionCache.ResolveStats()
            ?? ResolveFromStockFsm(stock)
            ?? FindHighlightedBulk(stock);

        MerchantStackerPlugin.Log.LogInfo(
            $"TryOpenInShopQty selected={(stats?.Item != null ? stats.Item.DisplayName : "none")}");

        if (stats?.Item == null
            || !Eligibility.IsBulkEligible(stats.Item)
            || Eligibility.GetMaxQuantity(stats.Item) <= 1
            || QuantityPicker.Instance == null
            || QuantityPicker.Instance.IsOpen)
        {
            return false;
        }

        ShopSelectionCache.Remember(stats);

        Transform shopRoot = stock.transform;
        var pane = eventGo != null
            ? eventGo.GetComponent<InventoryPaneBase>() ?? eventGo.GetComponentInParent<InventoryPaneBase>()
            : null;
        pane ??= stock.GetComponentInParent<InventoryPaneBase>();
        if (pane != null)
        {
            shopRoot = pane.transform;
        }

        MerchantStackerPlugin.Log.LogInfo($"In-pane qty open: {stats.Item.DisplayName}");
        QuantityPicker.Instance.OpenInShop(
            shopRoot: shopRoot,
            title: stats.Item.DisplayName,
            item: stats.Item.Item as CollectableItem,
            unitCost: stats.Item.Cost,
            currency: stats.Item.CurrencyType,
            maxQuantity: Eligibility.GetMaxQuantity(stats.Item),
            stats: stats);
        return true;
    }

    private static ShopMenuStock? FindShopStock(GameObject go)
    {
        if (go == null)
        {
            return null;
        }

        return go.GetComponent<ShopMenuStock>()
            ?? go.GetComponentInChildren<ShopMenuStock>(true)
            ?? go.GetComponentInParent<ShopMenuStock>()
            ?? go.transform.root.GetComponentInChildren<ShopMenuStock>(true);
    }

    private static ShopItemStats? ResolveFromStockFsm(ShopMenuStock stock)
    {
        try
        {
            foreach (PlayMakerFSM fsm in stock.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (fsm?.FsmVariables == null)
                {
                    continue;
                }

                foreach (string name in new[] { "Shop Item", "Item", "Selected Item" })
                {
                    var goVar = fsm.FsmVariables.FindFsmGameObject(name);
                    var stats = ShopSelectionCache.GetStatsFromGameObject(goVar?.Value);
                    if (stats?.Item != null)
                    {
                        return stats;
                    }
                }

                foreach (string name in new[] { "Current Item", "Item Number", "Selected Index" })
                {
                    var indexVar = fsm.FsmVariables.FindFsmInt(name);
                    if (indexVar == null)
                    {
                        continue;
                    }

                    int index = indexVar.Value;
                    if (index < 0 || index >= stock.GetItemCount())
                    {
                        continue;
                    }

                    var stats = ShopSelectionCache.GetStatsFromGameObject(stock.GetItemGameObject(index));
                    if (stats?.Item != null)
                    {
                        return stats;
                    }
                }
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private static ShopItemStats? FindHighlightedBulk(ShopMenuStock stock)
    {
        foreach (InventoryItemManager manager in UnityEngine.Object.FindObjectsByType<InventoryItemManager>(FindObjectsSortMode.None))
        {
            if (manager?.CurrentSelected == null)
            {
                continue;
            }

            var stats = ShopSelectionCache.GetStatsFromGameObject(manager.CurrentSelected.gameObject);
            if (stats?.Item != null
                && Eligibility.IsBulkEligible(stats.Item)
                && Eligibility.GetMaxQuantity(stats.Item) > 1)
            {
                return stats;
            }
        }

        return null;
    }
}
