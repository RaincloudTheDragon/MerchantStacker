using System;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MerchantStacker;

/// <summary>
/// Tracks the merchant stock row being purchased. Shop UI uses PlayMaker FSM vars,
/// not InventoryItemSelectable, so we resolve from FSM + SetShopItemPurchased target.
/// </summary>
internal static class ShopSelectionCache
{
    private static readonly string[] ItemObjectVarNames = { "Shop Item", "Item", "Selected Item" };
    private static readonly string[] ItemIndexVarNames = { "Current Item", "Item Number", "Selected Index" };

    internal static ShopItemStats? Stats { get; private set; }
    internal static ShopItem? Item => Stats != null ? Stats.Item : null;

    internal static void Remember(ShopItemStats? stats)
    {
        if (stats == null || stats.Item == null)
        {
            return;
        }

        Stats = stats;
    }

    internal static void Clear()
    {
        Stats = null;
    }

    /// <summary>
    /// Resolve highlighted/pending shop row from cache, FSM vars, or inventory cursor.
    /// </summary>
    internal static ShopItemStats? ResolveStats()
    {
        if (Stats?.Item != null)
        {
            return Stats;
        }

        if (TryResolveFromFsm(out ShopItemStats? fromFsm))
        {
            Remember(fromFsm);
            return fromFsm;
        }

        foreach (InventoryItemManager manager in UnityEngine.Object.FindObjectsByType<InventoryItemManager>(FindObjectsSortMode.None))
        {
            if (manager?.CurrentSelected == null)
            {
                continue;
            }

            var stats = GetStatsFromGameObject(manager.CurrentSelected.gameObject);
            if (stats?.Item != null)
            {
                Remember(stats);
                return stats;
            }
        }

        return null;
    }

    private static bool TryResolveFromFsm(out ShopItemStats? stats)
    {
        stats = null;
        foreach (PlayMakerFSM fsm in UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None))
        {
            if (fsm == null || !fsm.isActiveAndEnabled || fsm.FsmVariables == null)
            {
                continue;
            }

            foreach (string name in ItemObjectVarNames)
            {
                var goVar = fsm.FsmVariables.FindFsmGameObject(name);
                if (goVar?.Value == null)
                {
                    continue;
                }

                stats = GetStatsFromGameObject(goVar.Value);
                if (stats?.Item != null)
                {
                    MerchantStackerPlugin.Log.LogDebug($"Shop item from FSM '{name}': {stats.Item.DisplayName}");
                    return true;
                }
            }

            foreach (string name in ItemIndexVarNames)
            {
                var indexVar = fsm.FsmVariables.FindFsmInt(name);
                if (indexVar == null)
                {
                    continue;
                }

                var stock = fsm.GetComponent<ShopMenuStock>()
                    ?? fsm.GetComponentInParent<ShopMenuStock>()
                    ?? fsm.GetComponentInChildren<ShopMenuStock>(true);
                if (stock == null)
                {
                    continue;
                }

                int index = indexVar.Value;
                if (index < 0 || index >= stock.GetItemCount())
                {
                    continue;
                }

                stats = GetStatsFromGameObject(stock.GetItemGameObject(index));
                if (stats?.Item != null)
                {
                    MerchantStackerPlugin.Log.LogDebug($"Shop item from FSM '{name}'={index}: {stats.Item.DisplayName}");
                    return true;
                }
            }
        }

        stats = null;
        return false;
    }

    internal static ShopItemStats? GetStatsFromGameObject(GameObject? go)
    {
        if (go == null)
        {
            return null;
        }

        return go.GetComponent<ShopItemStats>()
            ?? go.GetComponentInChildren<ShopItemStats>(true)
            ?? go.GetComponentInParent<ShopItemStats>();
    }
}

[HarmonyPatch]
internal static class ShopSelectionCachePatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(InventoryItemSelectable), nameof(InventoryItemSelectable.Select))]
    private static void SelectPostfix(InventoryItemSelectable __instance)
    {
        ShopSelectionCache.Remember(ShopSelectionCache.GetStatsFromGameObject(__instance?.gameObject));
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(InventoryItemManager), nameof(InventoryItemManager.SetSelected), typeof(GameObject), typeof(bool))]
    private static void SetSelectedGoPostfix(GameObject selectedGameObject)
    {
        ShopSelectionCache.Remember(ShopSelectionCache.GetStatsFromGameObject(selectedGameObject));
    }

    [HarmonyPostfix]
    [HarmonyPatch(
        typeof(InventoryItemManager),
        nameof(InventoryItemManager.SetSelected),
        typeof(InventoryItemSelectable),
        typeof(InventoryItemManager.SelectionDirection?),
        typeof(bool))]
    private static void SetSelectedSelectablePostfix(InventoryItemSelectable selectable)
    {
        ShopSelectionCache.Remember(ShopSelectionCache.GetStatsFromGameObject(selectable?.gameObject));
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(HutongGames.PlayMaker.Actions.SetShopItemPurchased), nameof(HutongGames.PlayMaker.Actions.SetShopItemPurchased.OnEnter))]
    private static void CachePurchaseTargetV1(HutongGames.PlayMaker.Actions.SetShopItemPurchased __instance)
    {
        ShopSelectionCache.Remember(
            ShopSelectionCache.GetStatsFromGameObject(__instance.Target.GetSafe(__instance)));
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(HutongGames.PlayMaker.Actions.SetShopItemPurchasedV2), nameof(HutongGames.PlayMaker.Actions.SetShopItemPurchasedV2.OnEnter))]
    private static void CachePurchaseTargetV2(HutongGames.PlayMaker.Actions.SetShopItemPurchasedV2 __instance)
    {
        ShopSelectionCache.Remember(
            ShopSelectionCache.GetStatsFromGameObject(__instance.Target.GetSafe(__instance)));
    }

    /// <summary>FSM browse calls these for the highlighted row — keep cache warm before confirm.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ShopMenuStock), nameof(ShopMenuStock.GetItemGameObject))]
    private static void GetItemGameObjectPostfix(ShopMenuStock __instance, int itemNum, GameObject __result)
    {
        ShopSelectionCache.Remember(ShopSelectionCache.GetStatsFromGameObject(__result));
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ShopMenuStock), nameof(ShopMenuStock.GetCost))]
    private static void GetCostPostfix(ShopMenuStock __instance, int itemNum)
    {
        try
        {
            ShopSelectionCache.Remember(
                ShopSelectionCache.GetStatsFromGameObject(__instance.GetItemGameObject(itemNum)));
        }
        catch
        {
            // ignored
        }
    }
}
