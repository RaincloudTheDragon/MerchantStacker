using System.Collections;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MerchantStacker.Patches;

/// <summary>
/// When the shop opens Item Confirm for a bulk-eligible item, replace Yes/No with qty UI.
/// </summary>
[HarmonyPatch]
internal static class ShopConfirmListPatches
{
    private static bool _pendingQtyOpen;
    private static float _pollCooldown;

    [HarmonyPrefix]
    [HarmonyPatch(
        typeof(FSMUtility),
        nameof(FSMUtility.SendEventToGameObject),
        new[] { typeof(GameObject), typeof(string), typeof(bool) })]
    private static bool SendEventToGameObjectPrefix(GameObject go, string eventName)
    {
        return HandleConfirmEvent(go, eventName);
    }

    /// <summary>ShopSubItemSelection uses PlayMakerFSM.SendEvent("TO CONFIRM") directly.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayMakerFSM), nameof(PlayMakerFSM.SendEvent), typeof(string))]
    private static void PlayMakerSendEventPrefix(PlayMakerFSM __instance, string eventName)
    {
        HandleConfirmEvent(__instance != null ? __instance.gameObject : null, eventName);
    }

    /// <summary>
    /// Fallback: if confirm chrome is visible for a bulk item and qty isn't open, open it.
    /// InventoryPaneInput's GameObject often isn't under ShopMenuStock, so event hooks can miss.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(InventoryPaneInput), "Update")]
    private static void InventoryPaneUpdatePostfix()
    {
        if (!MerchantStackerPlugin.Enabled.Value
            || PurchaseBatcher.IsBatching
            || PurchaseBatcher.BlockShopPurchases
            || PurchaseBatcher.ExpectingFsmPurchase
            || QuantityPicker.Instance == null
            || QuantityPicker.Instance.IsOpen
            || _pendingQtyOpen)
        {
            return;
        }

        _pollCooldown -= Time.unscaledDeltaTime;
        if (_pollCooldown > 0f)
        {
            return;
        }

        _pollCooldown = 0.1f;

        // Only poll once Item Confirm Group is actually up (avoids false opens).
        ShopItemStats? stats = ShopSelectionCache.ResolveStats() ?? FindHighlightedBulk();
        if (stats?.Item == null)
        {
            return;
        }

        if (FindActiveConfirmGroup(stats.transform.root) == null)
        {
            return;
        }

        TryOpenQtyFromVisibleConfirm(eventSource: null, reason: "poll");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ShopMenuStock), "BuildItemList")]
    private static void BuildItemListPostfix(ShopMenuStock __instance)
    {
        PurchaseBatcher.EndShopPurchaseBlock();
        MerchantStackerPlugin.Log.LogInfo(
            $"Shop stock built: '{__instance.name}' items={__instance.GetItemCount()}");
    }

    /// <returns>False to suppress the FSM event (only while qty owns confirm).</returns>
    private static bool HandleConfirmEvent(GameObject? go, string? eventName)
    {
        if (string.IsNullOrEmpty(eventName))
        {
            return true;
        }

        if (eventName == "RESET SHOP WINDOW" || eventName == "RESET SHOP")
        {
            PurchaseBatcher.EndShopPurchaseBlock();
        }

        // Block confirm while qty picker owns input.
        if (QuantityPicker.Instance != null && QuantityPicker.Instance.IsOpen
            && (eventName == "UI CONFIRM" || eventName == "TO CONFIRM"))
        {
            return false;
        }

        if (!MerchantStackerPlugin.Enabled.Value
            || PurchaseBatcher.IsBatching
            || PurchaseBatcher.BlockShopPurchases
            || PurchaseBatcher.ExpectingFsmPurchase
            || QuantityPicker.Instance == null
            || QuantityPicker.Instance.IsOpen)
        {
            return true;
        }

        if (eventName == "TO CONFIRM" || eventName == "UI CONFIRM")
        {
            TryOpenQtyFromVisibleConfirm(go, reason: eventName!);
        }

        return true;
    }

    internal static bool TryOpenQtyFromVisibleConfirm(GameObject? eventSource, string reason)
    {
        if (_pendingQtyOpen
            || PurchaseBatcher.ExpectingFsmPurchase
            || QuantityPicker.Instance == null
            || QuantityPicker.Instance.IsOpen)
        {
            return false;
        }

        ShopItemStats? stats = ShopSelectionCache.ResolveStats()
            ?? ResolveStatsNear(eventSource)
            ?? FindHighlightedBulk();

        if (stats?.Item == null
            || !Eligibility.IsBulkEligible(stats.Item)
            || Eligibility.GetMaxQuantity(stats.Item) <= 1)
        {
            return false;
        }

        ShopMenuStock? stock = FindShopStock(eventSource)
            ?? stats.GetComponentInParent<ShopMenuStock>()
            ?? FindAnyActiveShopStock();

        if (stock == null)
        {
            MerchantStackerPlugin.Log.LogWarning(
                $"Confirm qty: no ShopMenuStock (via '{reason}') for {stats.Item.DisplayName}");
            return false;
        }

        // Hide Yes/No immediately so the vanilla "Purchase item?" chrome never sticks around.
        PreHideConfirmChrome(stats.transform.root);
        ShopSelectionCache.Remember(stats);
        _pendingQtyOpen = true;
        QuantityPicker.Instance.StartCoroutine(OpenQtyAfterConfirmShows(stock, stats, reason));
        MerchantStackerPlugin.Log.LogInfo(
            $"Confirm → schedule qty replace: {stats.Item.DisplayName} (via '{reason}')");
        return true;
    }

    /// <summary>Disable Confirm/UI List + Confirm msg as soon as we know qty will replace them.</summary>
    private static void PreHideConfirmChrome(Transform root)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == null)
            {
                continue;
            }

            if (t.name == "UI List" && t.parent != null && t.parent.name == "Confirm" && t.gameObject.activeSelf)
            {
                t.gameObject.SetActive(false);
            }
            else if (t.name == "Confirm msg" && t.gameObject.activeSelf)
            {
                t.gameObject.SetActive(false);
            }
        }
    }

    /// <summary>
    /// Recovery path when Yes fired SetShopItemPurchased before qty opened.
    /// </summary>
    internal static bool TryOpenQtyImmediate(ShopItemStats stats)
    {
        if (QuantityPicker.Instance == null || QuantityPicker.Instance.IsOpen || stats?.Item == null)
        {
            return false;
        }

        if (!Eligibility.IsBulkEligible(stats.Item) || Eligibility.GetMaxQuantity(stats.Item) <= 1)
        {
            return false;
        }

        ShopMenuStock? stock = stats.GetComponentInParent<ShopMenuStock>() ?? FindAnyActiveShopStock();
        if (stock == null)
        {
            return false;
        }

        OpenQtyReplacingConfirm(stock, stats);
        return QuantityPicker.Instance.IsOpen;
    }

    private static IEnumerator OpenQtyAfterConfirmShows(ShopMenuStock stock, ShopItemStats stats, string reason)
    {
        for (int i = 0; i < 12; i++)
        {
            yield return null;
            if (QuantityPicker.Instance == null || QuantityPicker.Instance.IsOpen)
            {
                _pendingQtyOpen = false;
                yield break;
            }

            if (FindActiveConfirmGroup(stats.transform.root) == null && i < 2)
            {
                // Give the FSM a couple frames to enable confirm chrome.
                continue;
            }

            // Open even if group search is flaky — HideConfirmChrome no-ops if missing.
            if (FindActiveConfirmGroup(stats.transform.root) != null || i >= 3)
            {
                OpenQtyReplacingConfirm(stock, stats);
                _pendingQtyOpen = false;
                if (QuantityPicker.Instance != null && QuantityPicker.Instance.IsOpen)
                {
                    yield break;
                }
            }
        }

        _pendingQtyOpen = false;
        MerchantStackerPlugin.Log.LogWarning(
            $"Confirm qty: failed to open for {stats.Item?.DisplayName} (via '{reason}')");
    }

    private static void OpenQtyReplacingConfirm(ShopMenuStock stock, ShopItemStats stats)
    {
        if (QuantityPicker.Instance == null || QuantityPicker.Instance.IsOpen || stats?.Item == null)
        {
            return;
        }

        Transform shopRoot = stock.transform;
        var pane = stock.GetComponentInParent<InventoryPaneBase>();
        if (pane != null)
        {
            shopRoot = pane.transform;
        }

        MerchantStackerPlugin.Log.LogInfo($"Replace confirm with qty: {stats.Item.DisplayName}");

        QuantityPicker.Instance.ArmShopPurchaseSession(
            onPurchaseComplete: null,
            onCancelPurchase: () =>
            {
                PurchaseBatcher.EndShopPurchaseBlock();
                EventRegister.SendEvent(EventRegisterEvents.ResetShopWindow);
                GameCameras.instance?.HUDIn();
            });

        QuantityPicker.Instance.OpenInShop(
            shopRoot: shopRoot,
            title: stats.Item.DisplayName,
            item: stats.Item.Item as CollectableItem,
            unitCost: stats.Item.Cost,
            currency: stats.Item.CurrencyType,
            maxQuantity: Eligibility.GetMaxQuantity(stats.Item),
            stats: stats);
    }

    private static Transform? FindActiveConfirmGroup(Transform root)
    {
        if (root == null)
        {
            return null;
        }

        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t != null && t.name == "Item Confirm Group" && t.gameObject.activeInHierarchy)
            {
                return t;
            }
        }

        return null;
    }

    private static ShopMenuStock? FindShopStock(GameObject? go)
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

    private static ShopMenuStock? FindAnyActiveShopStock()
    {
        foreach (ShopMenuStock stock in Object.FindObjectsByType<ShopMenuStock>(FindObjectsSortMode.None))
        {
            if (stock != null && stock.isActiveAndEnabled && stock.GetItemCount() > 0)
            {
                return stock;
            }
        }

        return null;
    }

    private static ShopItemStats? ResolveStatsNear(GameObject? go)
    {
        ShopMenuStock? stock = FindShopStock(go) ?? FindAnyActiveShopStock();
        if (stock == null)
        {
            return null;
        }

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

    private static ShopItemStats? FindHighlightedBulk()
    {
        foreach (InventoryItemManager manager in Object.FindObjectsByType<InventoryItemManager>(FindObjectsSortMode.None))
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
