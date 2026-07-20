using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace MerchantStacker.Patches;

/// <summary>
/// Replace the shop's vanilla Yes/No "Purchase item?" confirm with qty UI for bulk items.
/// </summary>
[HarmonyPatch]
internal static class ShopConfirmListPatches
{
    private static bool _pendingQtyOpen;

    [HarmonyPrefix]
    [HarmonyPatch(
        typeof(FSMUtility),
        nameof(FSMUtility.SendEventToGameObject),
        new[] { typeof(GameObject), typeof(string), typeof(bool) })]
    private static bool SendEventToGameObjectPrefix(GameObject go, string eventName)
    {
        return HandleConfirmEvent(go, eventName);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayMakerFSM), nameof(PlayMakerFSM.SendEvent), typeof(string))]
    private static void PlayMakerSendEventPrefix(PlayMakerFSM __instance, string eventName)
    {
        HandleConfirmEvent(__instance != null ? __instance.gameObject : null, eventName);
    }

    /// <summary>
    /// PlayMaker activates confirm UI here — GameObject.SetActive Harmony is unreliable on Unity.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ActivateGameObject), nameof(ActivateGameObject.OnEnter))]
    private static void ActivateGameObjectPostfix(ActivateGameObject __instance)
    {
        try
        {
            if (__instance.activate == null || !__instance.activate.Value || __instance.Fsm == null)
            {
                return;
            }

            GameObject? go = __instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject);
            if (go == null)
            {
                return;
            }

            GameObject? confirmGroup = FindConfirmGroupObject(go);
            if (confirmGroup == null || !confirmGroup.activeInHierarchy)
            {
                return;
            }

            TryOpenQtyForConfirmGroup(confirmGroup, reason: "ActivateGameObject");
        }
        catch (Exception ex)
        {
            MerchantStackerPlugin.Log.LogWarning($"ActivateGameObject hook: {ex.Message}");
        }
    }

    /// <summary>
    /// Block vanilla Yes on the confirm list — open qty instead (safety net if A uses Submit).
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UISelectionListItem), nameof(UISelectionListItem.Submit))]
    private static bool ConfirmYesSubmitPrefix(UISelectionListItem __instance)
    {
        if (!CanInterceptConfirmInput())
        {
            return true;
        }

        if (!IsShopConfirmYes(__instance))
        {
            return true;
        }

        ShopItemStats? stats = ResolveBulkStatsNear(null)
            ?? ResolveBulkStatsNear(__instance.gameObject);
        if (stats == null || stats.Item == null)
        {
            return true;
        }

        if (QuantityPicker.Instance!.IsOpen)
        {
            return false;
        }

        MerchantStackerPlugin.Log.LogInfo(
            $"Confirm Yes Submit → qty: {stats.Item.DisplayName}");
        OpenQtyForStats(stats, reason: "YesSubmit");
        return false;
    }

    /// <summary>
    /// Confirm Yes/No list becoming active — secondary steal if ActivateGameObject missed.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(UISelectionList), nameof(UISelectionList.SetActive))]
    private static void ConfirmListSetActivePostfix(UISelectionList __instance, bool value)
    {
        if (!value || !CanInterceptConfirmInput() || QuantityPicker.Instance!.IsOpen)
        {
            return;
        }

        if (!IsUnderShopConfirmList(__instance))
        {
            return;
        }

        ShopMenuStock? stock = __instance.GetComponentInParent<ShopMenuStock>(true) ?? FindAnyShopStock();
        ShopItemStats? stats = ResolveStatsFromStock(stock)
            ?? ResolveBulkStatsNear(__instance.gameObject)
            ?? ResolveBulkStatsNear(null);

        if (stats == null || stats.Item == null)
        {
            return;
        }

        __instance.gameObject.SetActive(false);
        PreHideConfirmChrome(__instance.transform.root);
        MerchantStackerPlugin.Log.LogInfo(
            $"Confirm UI List SetActive → open qty: {stats.Item.DisplayName}");
        OpenQtyForStats(stats, reason: "ConfirmList.SetActive", stockHint: stock);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ShopMenuStock), "BuildItemList")]
    private static void BuildItemListPostfix(ShopMenuStock __instance)
    {
        PurchaseBatcher.EndShopPurchaseBlock();
        MerchantStackerPlugin.Log.LogInfo(
            $"Shop stock built: '{__instance.name}' items={__instance.GetItemCount()}");
    }

    /// <returns>False to suppress the FSM event.</returns>
    private static bool HandleConfirmEvent(GameObject? go, string? eventName)
    {
        if (string.IsNullOrEmpty(eventName))
        {
            return true;
        }

        if (eventName == "RESET SHOP WINDOW" || eventName == "RESET SHOP")
        {
            PurchaseBatcher.EndShopPurchaseBlock();
            _pendingQtyOpen = false;
            ShopSelectionCache.Clear();
        }

        if (!CanInterceptConfirmInput())
        {
            // Still block confirm spam while qty owns the pad.
            if (QuantityPicker.Instance != null && QuantityPicker.Instance.IsOpen
                && (eventName == "UI CONFIRM" || eventName == "TO CONFIRM"))
            {
                return false;
            }

            return true;
        }

        if (eventName != "TO CONFIRM" && eventName != "UI CONFIRM")
        {
            return true;
        }

        ShopItemStats? stats = ResolveBulkStatsNear(go) ?? ResolveBulkStatsNear(null);
        bool onConfirm = HasActiveConfirmGroup();

        // Already on confirm: A is "Yes" — steal it (never let purchase start).
        if (eventName == "UI CONFIRM" && onConfirm)
        {
            if (stats != null && stats.Item != null)
            {
                MerchantStackerPlugin.Log.LogInfo(
                    $"UI CONFIRM on confirm → qty (steal Yes): {stats.Item.DisplayName}");
                OpenQtyForStats(stats, reason: "steal-Yes");
                return false;
            }

            // Stats missing — still block purchase and keep trying to open qty.
            foreach (Transform group in FindActiveConfirmGroups())
            {
                TryOpenQtyForConfirmGroup(group.gameObject, reason: "steal-Yes-retry");
            }

            return false;
        }

        // Opening confirm from the item list — allow FSM, replace chrome ASAP.
        if (stats != null && stats.Item != null)
        {
            MerchantStackerPlugin.Log.LogInfo(
                $"Confirm event '{eventName}' → schedule qty for {stats.Item.DisplayName}");
            PreHideConfirmChrome(stats.transform.root);
            OpenQtyForStats(stats, reason: eventName!);
        }
        else
        {
            MerchantStackerPlugin.Log.LogInfo(
                $"Confirm event '{eventName}' go={go?.name} — no bulk stats yet");
        }

        return true;
    }

    private static void TryOpenQtyForConfirmGroup(GameObject confirmGroup, string reason)
    {
        if (!CanInterceptConfirmInput() || QuantityPicker.Instance == null || QuantityPicker.Instance.IsOpen)
        {
            return;
        }

        PreHideConfirmChrome(confirmGroup.transform);

        ShopMenuStock? stock = confirmGroup.GetComponentInParent<ShopMenuStock>(true) ?? FindAnyShopStock();
        ShopItemStats? stats = ResolveStatsFromStock(stock)
            ?? ResolveBulkStatsNear(confirmGroup)
            ?? ResolveBulkStatsNear(null);

        if (stats == null || stats.Item == null)
        {
            if (!_pendingQtyOpen)
            {
                _pendingQtyOpen = true;
                QuantityPicker.Instance.StartCoroutine(
                    RetryOpenFromConfirmGroup(confirmGroup, stock));
            }

            return;
        }

        MerchantStackerPlugin.Log.LogInfo(
            $"{reason} → open qty: {stats.Item.DisplayName}");
        OpenQtyForStats(stats, reason: reason, stockHint: stock);
    }

    private static GameObject? FindConfirmGroupObject(GameObject go)
    {
        if (go == null)
        {
            return null;
        }

        if (go.name == "Item Confirm Group")
        {
            return go;
        }

        foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
        {
            if (t != null && t.name == "Item Confirm Group" && t.gameObject.activeInHierarchy)
            {
                return t.gameObject;
            }
        }

        return null;
    }

    internal static bool TryOpenQtyImmediate(ShopItemStats stats)
    {
        return OpenQtyForStats(stats, reason: "immediate");
    }

    internal static bool TryOpenQtyFromVisibleConfirm(GameObject? eventSource, string reason)
    {
        ShopItemStats? stats = ResolveBulkStatsNear(eventSource) ?? ResolveBulkStatsNear(null);
        return stats?.Item != null && OpenQtyForStats(stats, reason);
    }

    private static bool CanInterceptConfirmInput()
    {
        return MerchantStackerPlugin.Enabled.Value
            && !PurchaseBatcher.IsBatching
            && !PurchaseBatcher.BlockShopPurchases
            && !PurchaseBatcher.ExpectingFsmPurchase
            && PurchaseBatcher.PendingQuantity <= 0
            && QuantityPicker.Instance != null;
    }

    private static bool OpenQtyForStats(ShopItemStats? stats, string reason, ShopMenuStock? stockHint = null)
    {
        if (QuantityPicker.Instance == null || QuantityPicker.Instance.IsOpen)
        {
            return false;
        }

        // Must use Unity's overloaded == (destroyed objects are "null").
        if (stats == null)
        {
            return false;
        }

        ShopItem? item = stats.Item;
        if (item == null
            || !Eligibility.IsBulkEligible(item)
            || Eligibility.GetMaxQuantity(item) <= 1)
        {
            return false;
        }

        ShopMenuStock? stock = stockHint;
        if (stock == null)
        {
            try
            {
                stock = stats.GetComponentInParent<ShopMenuStock>(true);
            }
            catch
            {
                stock = null;
            }
        }

        stock ??= FindAnyShopStock();
        if (stock == null)
        {
            MerchantStackerPlugin.Log.LogWarning(
                $"Confirm qty: no ShopMenuStock (via '{reason}') for {item.DisplayName}");
            return false;
        }

        Transform root = stock.transform.root;
        PreHideConfirmChrome(root);
        ShopSelectionCache.Remember(stats);

        if (FindActiveConfirmGroup(root) != null)
        {
            OpenQtyReplacingConfirm(stock, stats);
            return QuantityPicker.Instance.IsOpen;
        }

        if (_pendingQtyOpen)
        {
            return true;
        }

        _pendingQtyOpen = true;
        QuantityPicker.Instance.StartCoroutine(OpenQtyAfterConfirmShows(stock, stats, reason));
        MerchantStackerPlugin.Log.LogInfo(
            $"Confirm → schedule qty replace: {item.DisplayName} (via '{reason}')");
        return true;
    }

    private static IEnumerator OpenQtyAfterConfirmShows(ShopMenuStock stock, ShopItemStats stats, string reason)
    {
        for (int i = 0; i < 20; i++)
        {
            yield return null;
            if (QuantityPicker.Instance == null || QuantityPicker.Instance.IsOpen)
            {
                _pendingQtyOpen = false;
                yield break;
            }

            if (stats == null || stock == null)
            {
                _pendingQtyOpen = false;
                yield break;
            }

            PreHideConfirmChrome(stock.transform.root);

            if (FindActiveConfirmGroup(stock.transform.root) != null || i >= 1)
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
            $"Confirm qty: failed to open (via '{reason}')");
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

    private static IEnumerator RetryOpenFromConfirmGroup(GameObject confirmGroup, ShopMenuStock? stock)
    {
        for (int i = 0; i < 10; i++)
        {
            yield return null;
            if (QuantityPicker.Instance == null || QuantityPicker.Instance.IsOpen)
            {
                _pendingQtyOpen = false;
                yield break;
            }

            if (confirmGroup == null || !confirmGroup.activeInHierarchy)
            {
                _pendingQtyOpen = false;
                yield break;
            }

            PreHideConfirmChrome(confirmGroup.transform);
            stock ??= confirmGroup.GetComponentInParent<ShopMenuStock>(true) ?? FindAnyShopStock();
            ShopItemStats? stats = ResolveStatsFromStock(stock)
                ?? ResolveBulkStatsNear(confirmGroup)
                ?? ResolveBulkStatsNear(null);

            if (stats == null || stats.Item == null)
            {
                continue;
            }

            _pendingQtyOpen = false;
            MerchantStackerPlugin.Log.LogInfo(
                $"Item Confirm Group retry → open qty: {stats.Item.DisplayName}");
            OpenQtyForStats(stats, reason: "ItemConfirmGroup.retry", stockHint: stock);
            yield break;
        }

        _pendingQtyOpen = false;
    }

    private static void PreHideConfirmChrome(Transform root)
    {
        if (root == null)
        {
            return;
        }

        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == null || !t.gameObject.activeSelf)
            {
                continue;
            }

            string n = t.name;
            if (n == "UI List" && t.parent != null && t.parent.name == "Confirm")
            {
                t.gameObject.SetActive(false);
            }
            else if (n == "Confirm msg" || n == "Costs")
            {
                t.gameObject.SetActive(false);
            }
        }
    }

    private static bool HasActiveConfirmGroup()
    {
        return FindActiveConfirmGroups().Count > 0;
    }

    /// <summary>
    /// Lightweight: only scene-valid stocks (include inactive — list is off during confirm).
    /// Used sparingly on UI CONFIRM, not every frame.
    /// </summary>
    private static List<Transform> FindActiveConfirmGroups()
    {
        var list = new List<Transform>();
        foreach (ShopMenuStock stock in UnityEngine.Object.FindObjectsByType<ShopMenuStock>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (stock == null)
            {
                continue;
            }

            try
            {
                if (!stock.gameObject.scene.IsValid())
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            Transform? group = FindActiveConfirmGroup(stock.transform.root);
            if (group != null)
            {
                list.Add(group);
            }
        }

        return list;
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

    private static ShopItemStats? ResolveBulkStatsNear(GameObject? go)
    {
        ShopItemStats? stats = null;
        if (go != null)
        {
            stats = ShopSelectionCache.GetStatsFromGameObject(go)
                ?? ResolveStatsFromStock(FindShopStock(go));
        }

        stats ??= ShopSelectionCache.ResolveStats()
            ?? ResolveStatsFromStock(FindAnyShopStock())
            ?? FindHighlightedBulk();

        // Unity destroyed-object check before touching .Item / GetComponent.
        if (stats == null || stats.Item == null)
        {
            return null;
        }

        if (!Eligibility.IsBulkEligible(stats.Item) || Eligibility.GetMaxQuantity(stats.Item) <= 1)
        {
            return null;
        }

        return stats;
    }

    private static ShopItemStats? ResolveStatsFromStock(ShopMenuStock? stock)
    {
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

    private static bool IsShopConfirmYes(UISelectionListItem item)
    {
        if (item == null)
        {
            return false;
        }

        string n = item.gameObject.name;
        if (!n.Equals("Yes", StringComparison.OrdinalIgnoreCase)
            && n.IndexOf("yes", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        return IsUnderShopConfirmTransform(item.transform);
    }

    private static bool IsUnderShopConfirmList(UISelectionList list)
    {
        return list != null && IsUnderShopConfirmTransform(list.transform);
    }

    private static bool IsUnderShopConfirmTransform(Transform? t)
    {
        bool underConfirm = false;
        while (t != null)
        {
            if (t.name == "Confirm" || t.name == "Item Confirm Group" || t.name == "UI List")
            {
                if (t.name == "Confirm" || t.name == "Item Confirm Group")
                {
                    underConfirm = true;
                }
            }

            if (underConfirm
                && (t.GetComponent<ShopMenuStock>() != null
                    || t.GetComponentInParent<ShopMenuStock>(true) != null
                    || t.GetComponent<InventoryPaneBase>() != null
                    || t.GetComponentInParent<InventoryPaneBase>(true) != null))
            {
                return true;
            }

            t = t.parent;
        }

        return underConfirm && FindAnyShopStock() != null;
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

    private static ShopMenuStock? FindAnyShopStock()
    {
        ShopMenuStock? fallback = null;
        foreach (ShopMenuStock stock in UnityEngine.Object.FindObjectsByType<ShopMenuStock>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (stock == null || !stock.gameObject.scene.IsValid())
            {
                continue;
            }

            try
            {
                if (stock.GetItemCount() <= 0)
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            if (stock.isActiveAndEnabled)
            {
                return stock;
            }

            fallback ??= stock;
        }

        return fallback;
    }

    private static ShopItemStats? FindHighlightedBulk()
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
