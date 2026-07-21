using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace MerchantStacker.Patches;

/// <summary>
/// Show-confirm intercepts (separate class so a bad event patch can't disable these).
/// PlayMaker ActivateGameObject ends in GameObject.SetActive(true).
/// </summary>
[HarmonyPatch]
internal static class ShopConfirmShowPatches
{
    private static bool _inSetActiveHook;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameObject), nameof(GameObject.SetActive))]
    private static void GameObjectSetActivePostfix(GameObject __instance, bool value)
    {
        if (!value || _inSetActiveHook || __instance == null)
        {
            return;
        }

        string n = __instance.name;
        if (n != "Item Confirm Group" && n != "Confirm" && n != "UI List"
            && n != "Yes" && n != "No" && n != "Confirm msg" && n != "Costs"
            && n != "Thankyou")
        {
            return;
        }

        bool underConfirmUiList = n == "UI List"
            && __instance.transform.parent != null
            && __instance.transform.parent.name == "Confirm";
        bool underConfirmChrome = underConfirmUiList
            || n == "Confirm msg"
            || n == "Costs"
            || n == "Thankyou"
            || ((n == "Yes" || n == "No")
                && ShopConfirmListPatches.IsUnderShopConfirmPublic(__instance.transform));

        if (n == "UI List" && !underConfirmUiList)
        {
            return;
        }

        // Qty already owns the pad — FSM "Activate Yes No" re-enables chrome after we open.
        // Kill Yes/No/Costs immediately so left-stick can't drive vanilla confirm.
        if (QuantityPicker.Instance != null && QuantityPicker.Instance.IsOpen
            && (underConfirmChrome || n == "Confirm" || n == "Item Confirm Group"))
        {
            if (underConfirmChrome || n == "UI List" || n == "Confirm msg" || n == "Costs"
                || n == "Yes" || n == "No" || n == "Thankyou")
            {
                try
                {
                    _inSetActiveHook = true;
                    __instance.SetActive(false);
                }
                finally
                {
                    _inSetActiveHook = false;
                }
            }

            if (n == "Confirm" || n == "Item Confirm Group")
            {
                ShopConfirmListPatches.SuppressConfirmChromePublic(__instance.transform);
            }

            return;
        }

        if (__instance.transform.root.GetComponentInChildren<ShopMenuStock>(true) == null)
        {
            return;
        }

        GameObject? confirmGroup = n == "Item Confirm Group"
            ? __instance
            : ShopConfirmListPatches.FindConfirmGroupObjectPublic(__instance);
        if (confirmGroup == null)
        {
            return;
        }

        // Always restore Yes/No when we aren't replacing confirm (post-buy block, unique item, etc.).
        if (!ShopConfirmListPatches.CanInterceptConfirmInputPublic()
            || QuantityPicker.Instance == null)
        {
            ShopConfirmListPatches.RestoreConfirmChromePublic(confirmGroup.transform);
            return;
        }

        try
        {
            _inSetActiveHook = true;
            bool opened = ShopConfirmListPatches.TryOpenQtyForConfirmGroupPublic(
                confirmGroup, reason: $"SetActive({n})");
            if (opened && QuantityPicker.Instance != null && QuantityPicker.Instance.IsOpen)
            {
                ShopConfirmListPatches.SuppressConfirmChromePublic(confirmGroup.transform);
            }
            else
            {
                ShopConfirmListPatches.RestoreConfirmChromePublic(confirmGroup.transform);
            }
        }
        catch (Exception ex)
        {
            MerchantStackerPlugin.Log.LogWarning($"SetActive confirm hook: {ex.Message}");
        }
        finally
        {
            _inSetActiveHook = false;
        }
    }

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

            GameObject? confirmGroup = ShopConfirmListPatches.FindConfirmGroupObjectPublic(go);
            if (confirmGroup == null)
            {
                return;
            }

            if (QuantityPicker.Instance != null && QuantityPicker.Instance.IsOpen)
            {
                ShopConfirmListPatches.SuppressConfirmChromePublic(confirmGroup.transform);
                return;
            }

            bool opened = ShopConfirmListPatches.TryOpenQtyForConfirmGroupPublic(
                confirmGroup, reason: "ActivateGameObject");
            if (opened && QuantityPicker.Instance != null && QuantityPicker.Instance.IsOpen)
            {
                ShopConfirmListPatches.SuppressConfirmChromePublic(confirmGroup.transform);
            }
            else
            {
                ShopConfirmListPatches.RestoreConfirmChromePublic(confirmGroup.transform);
            }
        }
        catch (Exception ex)
        {
            MerchantStackerPlugin.Log.LogWarning($"ActivateGameObject hook: {ex.Message}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CallMethodProper), nameof(CallMethodProper.OnEnter))]
    private static void CallMethodProperPostfix(CallMethodProper __instance)
    {
        try
        {
            if (__instance.methodName == null || __instance.Fsm == null)
            {
                return;
            }

            if (!string.Equals(__instance.methodName.Value, "SetActive", StringComparison.Ordinal))
            {
                return;
            }

            GameObject? go = __instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject);
            if (go == null)
            {
                return;
            }

            GameObject? confirmGroup = ShopConfirmListPatches.FindConfirmGroupObjectPublic(go);
            if (confirmGroup == null)
            {
                return;
            }

            ShopConfirmListPatches.TryOpenQtyForConfirmGroupPublic(
                confirmGroup, reason: "CallMethodProper.SetActive");
        }
        catch (Exception ex)
        {
            MerchantStackerPlugin.Log.LogWarning($"CallMethodProper hook: {ex.Message}");
        }
    }
}

/// <summary>
/// Replace the shop's vanilla Yes/No "Purchase item?" confirm with qty UI for bulk items.
/// </summary>
[HarmonyPatch]
internal static class ShopConfirmListPatches
{
    private static bool _pendingQtyOpen;
    /// <summary>Bulk row remembered at UI CONFIRM — used when SetActive(Confirm) runs before FSM vars update.</summary>
    private static ShopItemStats? _armedBulkStats;

    internal static void ClearPendingQtyOpen()
    {
        _pendingQtyOpen = false;
        _armedBulkStats = null;
    }

    internal static bool CanInterceptConfirmInputPublic() => CanInterceptConfirmInput();

    internal static GameObject? FindConfirmGroupObjectPublic(GameObject go) => FindConfirmGroupObject(go);

    internal static bool TryOpenQtyForConfirmGroupPublic(GameObject confirmGroup, string reason) =>
        TryOpenQtyForConfirmGroup(confirmGroup, reason);

    internal static void SuppressConfirmChromePublic(Transform root) => PreHideConfirmChrome(root);

    internal static void RestoreConfirmChromePublic(Transform root) => RestoreConfirmChrome(root);

    internal static bool IsUnderShopConfirmPublic(Transform? t) => IsUnderShopConfirmTransform(t);

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
    /// Shop idle/Yes use ListenForMenuActions → Fsm.Event → ProcessEvent.
    /// Do not also patch Fsm.Event(string) — wrong param names abort the whole class.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Fsm), nameof(Fsm.ProcessEvent), typeof(FsmEvent), typeof(FsmEventData))]
    private static bool FsmProcessEventPrefix(Fsm __instance, FsmEvent fsmEvent)
    {
        if (fsmEvent == null || string.IsNullOrEmpty(fsmEvent.Name))
        {
            return true;
        }

        string eventName = fsmEvent.Name;

        GameObject? ownerGo = null;
        try
        {
            ownerGo = __instance.GameObject;
        }
        catch
        {
            return true;
        }

        if (ownerGo == null || !IsShopFsmOwner(ownerGo))
        {
            return true;
        }

        // Qty owns the pad — swallow confirm-list navigation / submit so Yes/No can't move.
        if (QuantityPicker.Instance != null && QuantityPicker.Instance.IsOpen
            && IsConfirmNavOrSubmitEvent(eventName))
        {
            return false;
        }

        if (eventName != "UI CONFIRM" && eventName != "TO CONFIRM" && eventName != "UI SELECTION MADE")
        {
            return true;
        }

        return HandleConfirmEvent(ownerGo, eventName);
    }

    private static bool IsConfirmNavOrSubmitEvent(string eventName)
    {
        return eventName == "UI CONFIRM"
            || eventName == "TO CONFIRM"
            || eventName == "UI SELECTION MADE"
            || eventName == "UI CANCEL"
            || eventName == "CANCEL"
            || eventName == "UI UP"
            || eventName == "UI DOWN"
            || eventName == "UI LEFT"
            || eventName == "UI RIGHT"
            || eventName == "UP"
            || eventName == "DOWN"
            || eventName == "LEFT"
            || eventName == "RIGHT"
            || eventName == "UI MOVE VERTICAL"
            || eventName == "UI MOVE HORIZONTAL";
    }

    /// <summary>
    /// Confirm Yes/No list reads left-stick in its own Update — mute it while qty is open.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UISelectionList), "Update")]
    private static bool ConfirmListUpdatePrefix(UISelectionList __instance)
    {
        if (QuantityPicker.Instance == null || !QuantityPicker.Instance.IsOpen)
        {
            return true;
        }

        if (!IsUnderShopConfirmList(__instance))
        {
            return true;
        }

        // Keep inactive; FSM may have re-enabled us this frame.
        if (__instance.gameObject.activeSelf)
        {
            __instance.gameObject.SetActive(false);
        }

        return false;
    }

    /// <summary>
    /// Block vanilla Yes on the confirm list — open qty instead (safety net if A uses Submit).
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UISelectionListItem), nameof(UISelectionListItem.Submit))]
    private static bool ConfirmYesSubmitPrefix(UISelectionListItem __instance)
    {
        if (QuantityPicker.Instance != null && QuantityPicker.Instance.IsOpen
            && IsShopConfirmYes(__instance))
        {
            return false;
        }

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

        if (stats == null || stats.Item == null || !Eligibility.ShouldOfferBulkQty(stats.Item))
        {
            return;
        }

        MerchantStackerPlugin.Log.LogInfo(
            $"Confirm UI List SetActive → open qty: {stats.Item.DisplayName}");
        if (OpenQtyForStats(stats, reason: "SetActive(UI List)", stockHint: stock)
            && QuantityPicker.Instance != null
            && QuantityPicker.Instance.IsOpen)
        {
            __instance.gameObject.SetActive(false);
            PreHideConfirmChrome(__instance.transform.root);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ShopMenuStock), "BuildItemList")]
    private static void BuildItemListPostfix(ShopMenuStock __instance)
    {
        // ResetShopWindow rebuilds the list — clear post-buy block so qty can open again.
        PurchaseBatcher.EndShopPurchaseBlock();
        ClearPendingQtyOpen();
        RestoreConfirmChrome(__instance.transform.root);

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
            ClearPendingQtyOpen();
            ShopSelectionCache.Clear();
        }

        if (!CanInterceptConfirmInput())
        {
            // Still block confirm spam while qty owns the pad.
            if (QuantityPicker.Instance != null && QuantityPicker.Instance.IsOpen
                && (eventName == "UI CONFIRM" || eventName == "TO CONFIRM"
                    || eventName == "UI SELECTION MADE"))
            {
                return false;
            }

            return true;
        }

        ShopItemStats? stats = ResolveBulkStatsNear(go) ?? ResolveBulkStatsNear(null);

        // Yes/No while confirm is up — never let bulk Yes purchase; open qty instead.
        if (eventName == "UI SELECTION MADE")
        {
            if (QuantityPicker.Instance != null && QuantityPicker.Instance.IsOpen)
            {
                return false;
            }

            if (stats != null && stats.Item != null
                && Eligibility.ShouldOfferBulkQty(stats.Item)
                && HasActiveConfirmGroup())
            {
                MerchantStackerPlugin.Log.LogInfo(
                    $"UI SELECTION MADE → qty (block Yes): {stats.Item.DisplayName}");
                OpenQtyForStats(stats, reason: "UI SELECTION MADE");
                return false;
            }

            return true;
        }

        if (eventName != "TO CONFIRM" && eventName != "UI CONFIRM")
        {
            return true;
        }

        // UI CONFIRM is before Can Buy — never open or PreHide here.
        // Arm the live bulk row so SetActive(Confirm) can open even if FSM vars lag.
        if (eventName == "UI CONFIRM")
        {
            ShopItemStats? current = ResolveCurrentShopStats(go);
            if (current?.Item != null && Eligibility.ShouldOfferBulkQty(current.Item))
            {
                ShopSelectionCache.Remember(current);
                _armedBulkStats = current;
                MerchantStackerPlugin.Log.LogInfo(
                    $"Fsm 'UI CONFIRM' → armed qty for {current.Item.DisplayName}");
            }
            else
            {
                _armedBulkStats = null;
                _pendingQtyOpen = false;
            }

            return true;
        }

        // Sub-item path → confirm (after can-buy checks in that flow).
        if (eventName == "TO CONFIRM" && stats != null && stats.Item != null
            && Eligibility.ShouldOfferBulkQty(stats.Item))
        {
            _armedBulkStats = stats;
            MerchantStackerPlugin.Log.LogInfo(
                $"Fsm '{eventName}' → arm qty for {stats.Item.DisplayName}");
            OpenQtyForStats(stats, reason: eventName!);
        }

        return true;
    }

    private static bool TryOpenQtyForConfirmGroup(GameObject confirmGroup, string reason)
    {
        if (!CanInterceptConfirmInput() || QuantityPicker.Instance == null || QuantityPicker.Instance.IsOpen)
        {
            return QuantityPicker.Instance != null && QuantityPicker.Instance.IsOpen;
        }

        ShopMenuStock? stock = confirmGroup.GetComponentInParent<ShopMenuStock>(true) ?? FindAnyShopStock();
        // Live selection, then row armed at UI CONFIRM (not an unrelated stale cache).
        ShopItemStats? current = ResolveCurrentShopStats(confirmGroup)
            ?? ResolveStatsFromStock(stock)
            ?? _armedBulkStats;

        if (current?.Item != null)
        {
            ShopSelectionCache.Remember(current);
        }

        if (current == null || current.Item == null)
        {
            // Selection not readable this frame — retry if UI CONFIRM armed a bulk row.
            if (!_pendingQtyOpen && _armedBulkStats != null && QuantityPicker.Instance != null)
            {
                _pendingQtyOpen = true;
                QuantityPicker.Instance.StartCoroutine(
                    RetryOpenFromConfirmGroup(confirmGroup, stock));
            }

            return false;
        }

        if (!Eligibility.ShouldOfferBulkQty(current.Item))
        {
            _pendingQtyOpen = false;
            _armedBulkStats = null;
            return false;
        }

        // FSM already reached Confirm (Can Buy passed) — do not re-check CanBuy here.

        MerchantStackerPlugin.Log.LogInfo(
            $"{reason} → open qty: {current.Item.DisplayName}");
        bool opened = OpenQtyForStats(current, reason: reason, stockHint: stock);
        if (opened)
        {
            _armedBulkStats = null;
        }

        return opened;
    }

    /// <summary>
    /// ActivateGameObject often targets Confirm / UI List — walk children and parents for the group.
    /// </summary>
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
            if (t != null && t.name == "Item Confirm Group")
            {
                return t.gameObject;
            }
        }

        Transform? p = go.transform.parent;
        int guard = 0;
        while (p != null && guard++ < 12)
        {
            if (p.name == "Item Confirm Group")
            {
                return p.gameObject;
            }

            p = p.parent;
        }

        return null;
    }

    private static bool IsShopFsmOwner(GameObject ownerGo)
    {
        if (ownerGo == null)
        {
            return false;
        }

        if (ownerGo.GetComponentInParent<ShopMenuStock>(true) != null
            || ownerGo.GetComponentInChildren<ShopMenuStock>(true) != null)
        {
            return true;
        }

        string n = ownerGo.name;
        return n.IndexOf("Shop", StringComparison.OrdinalIgnoreCase) >= 0;
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
        // Do not gate on PendingQuantity — BeginSession used to set it to 1 and
        // permanently blocked the next confirm open after a buy.
        return MerchantStackerPlugin.Enabled.Value
            && !PurchaseBatcher.IsBatching
            && !PurchaseBatcher.BlockShopPurchases
            && !PurchaseBatcher.ExpectingFsmPurchase
            && QuantityPicker.Instance != null
            && !QuantityPicker.Instance.IsOpen;
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
        if (item == null || !Eligibility.ShouldOfferBulkQty(item))
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

        // Never open on UI CONFIRM (before Can Buy) or because leftover confirm chrome is visible.
        bool openNow = reason.StartsWith("SetActive", StringComparison.Ordinal)
            || reason.StartsWith("Activate", StringComparison.Ordinal)
            || reason.StartsWith("CallMethod", StringComparison.Ordinal)
            || reason == "TO CONFIRM"
            || reason == "UI SELECTION MADE"
            || reason == "YesSubmit"
            || reason == "immediate"
            || reason == "ConfirmWait"
            || reason.IndexOf("retry", StringComparison.OrdinalIgnoreCase) >= 0;

        if (!openNow)
        {
            return false;
        }

        ShopSelectionCache.Remember(stats);

        _pendingQtyOpen = false;
        // Open first — only hide Yes/No after qty owns the pad (avoids blank softlock).
        OpenQtyReplacingConfirm(stock, stats);
        if (QuantityPicker.Instance.IsOpen)
        {
            PreHideConfirmChrome(stock.transform.root);
            MerchantStackerPlugin.Log.LogInfo(
                $"Confirm → qty replace: {item.DisplayName} (via '{reason}')");
            return true;
        }

        MerchantStackerPlugin.Log.LogWarning(
            $"Confirm qty: OpenInShop failed for {item.DisplayName} (via '{reason}') — left vanilla confirm");
        return false;
    }

    private static IEnumerator OpenQtyAfterConfirmShows(ShopMenuStock stock, ShopItemStats stats, string reason)
    {
        for (int i = 0; i < 45; i++)
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

            // Only once confirm chrome is up (after Can Buy). Never open on a bare frame wait.
            if (FindActiveConfirmGroup(stock.transform.root) == null)
            {
                continue;
            }

            PreHideConfirmChrome(stock.transform.root);
            OpenQtyReplacingConfirm(stock, stats);
            _pendingQtyOpen = false;
            if (QuantityPicker.Instance != null && QuantityPicker.Instance.IsOpen)
            {
                yield break;
            }
        }

        _pendingQtyOpen = false;
        MerchantStackerPlugin.Log.LogWarning(
            $"Confirm qty: failed to open (via '{reason}')");
    }

    /// <summary>UI CONFIRM before cache/FSM vars are warm — open when confirm chrome appears.</summary>
    private static IEnumerator WaitForConfirmThenOpen(GameObject? eventSource)
    {
        for (int i = 0; i < 45; i++)
        {
            yield return null;
            if (QuantityPicker.Instance == null || QuantityPicker.Instance.IsOpen)
            {
                _pendingQtyOpen = false;
                yield break;
            }

            Transform? group = null;
            foreach (Transform t in FindActiveConfirmGroups())
            {
                group = t;
                break;
            }

            if (group == null)
            {
                continue;
            }

            ShopItemStats? stats = ResolveBulkStatsNear(eventSource) ?? ResolveBulkStatsNear(group.gameObject);
            if (stats == null || stats.Item == null)
            {
                continue;
            }

            _pendingQtyOpen = false;
            MerchantStackerPlugin.Log.LogInfo(
                $"Confirm wait → open qty: {stats.Item.DisplayName}");
            OpenQtyForStats(stats, reason: "ConfirmWait", stockHint: FindShopStock(group.gameObject));
            yield break;
        }

        _pendingQtyOpen = false;
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
        for (int i = 0; i < 12; i++)
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
                _armedBulkStats = null;
                yield break;
            }

            stock ??= confirmGroup.GetComponentInParent<ShopMenuStock>(true) ?? FindAnyShopStock();
            ShopItemStats? stats = ResolveCurrentShopStats(confirmGroup)
                ?? ResolveStatsFromStock(stock)
                ?? _armedBulkStats;

            if (stats == null || stats.Item == null || !Eligibility.ShouldOfferBulkQty(stats.Item))
            {
                continue;
            }

            _pendingQtyOpen = false;
            MerchantStackerPlugin.Log.LogInfo(
                $"Item Confirm Group retry → open qty: {stats.Item.DisplayName}");
            if (OpenQtyForStats(stats, reason: "ItemConfirmGroup.retry", stockHint: stock))
            {
                _armedBulkStats = null;
                PreHideConfirmChrome(confirmGroup.transform);
                yield break;
            }
        }

        _pendingQtyOpen = false;
        if (confirmGroup != null)
        {
            RestoreConfirmChrome(confirmGroup.transform);
        }
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
            bool confirmUiList = n == "UI List" && t.parent != null && t.parent.name == "Confirm";
            // Thankyou = vanilla "Item Purchased" leftover after a prior buy.
            if (confirmUiList || n == "Confirm msg" || n == "Costs" || n == "Yes" || n == "No"
                || n == "Thankyou")
            {
                // Only kill Yes/No under the shop confirm list (not unrelated nodes).
                if ((n == "Yes" || n == "No") && !IsUnderShopConfirmTransform(t))
                {
                    continue;
                }

                t.gameObject.SetActive(false);
            }
        }
    }

    /// <summary>
    /// Re-enable Confirm + Yes/No/Costs. Qty PreHide leaves Confirm inactive — without
    /// reactivating it, Yes/No stay invisible (blank softlock when max qty drops to 1).
    /// </summary>
    private static void RestoreConfirmChrome(Transform root)
    {
        if (root == null)
        {
            return;
        }

        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == null)
            {
                continue;
            }

            string n = t.name;

            // Parent panel must be on or Yes/No never appear.
            if (n == "Confirm"
                && t.parent != null
                && t.parent.name == "Item Confirm Group"
                && !t.gameObject.activeSelf)
            {
                t.gameObject.SetActive(true);
                continue;
            }

            bool confirmUiList = n == "UI List" && t.parent != null && t.parent.name == "Confirm";
            if (!confirmUiList && n != "Confirm msg" && n != "Costs" && n != "Yes" && n != "No")
            {
                continue;
            }

            if ((n == "Yes" || n == "No") && !IsUnderShopConfirmTransform(t))
            {
                continue;
            }

            // Skip "Confirm msg" — leave Purchase Item? text off; Yes/No is enough.
            if (n == "Confirm msg")
            {
                continue;
            }

            if (!t.gameObject.activeSelf)
            {
                t.gameObject.SetActive(true);
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

    /// <summary>Live highlighted shop row from FSM/stock — ignores stale cache.</summary>
    private static ShopItemStats? ResolveCurrentShopStats(GameObject? go)
    {
        ShopMenuStock? stock = FindShopStock(go) ?? FindAnyShopStock();
        ShopItemStats? current = ResolveStatsFromStock(stock);
        if (current == null && go != null)
        {
            current = ShopSelectionCache.GetStatsFromGameObject(go);
        }

        current ??= FindHighlightedBulk();
        if (current == null || current.Item == null)
        {
            return null;
        }

        return current;
    }

    private static ShopItemStats? ResolveBulkStatsNear(GameObject? go)
    {
        // Prefer live selection. Only accept cache if it matches that row.
        ShopItemStats? current = ResolveCurrentShopStats(go);
        if (current?.Item != null)
        {
            ShopSelectionCache.Remember(current);
            if (!Eligibility.ShouldOfferBulkQty(current.Item))
            {
                return null;
            }

            return current;
        }

        return null;
    }

    private static ShopItemStats? ResolveStatsFromStock(ShopMenuStock? stock)
    {
        if (stock == null)
        {
            return null;
        }

        try
        {
            // FSMs often live on Shop Menu root, not only under Item List.
            var fsms = stock.transform.root.GetComponentsInChildren<PlayMakerFSM>(true);
            foreach (PlayMakerFSM fsm in fsms)
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

                foreach (string name in new[] { "Current Item", "Item Number", "Selected Index", "Initial Item" })
                {
                    var indexVar = fsm.FsmVariables.FindFsmInt(name);
                    if (indexVar == null)
                    {
                        continue;
                    }

                    ShopItemStats? stats = StatsFromStockIndex(stock, indexVar.Value);
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

    private static ShopItemStats? StatsFromStockIndex(ShopMenuStock stock, int index)
    {
        int count = stock.GetItemCount();
        // Try 0-based, then 1-based (FSM often stores 1..N).
        foreach (int i in new[] { index, index - 1 })
        {
            if (i < 0 || i >= count)
            {
                continue;
            }

            var stats = ShopSelectionCache.GetStatsFromGameObject(stock.GetItemGameObject(i));
            if (stats?.Item != null)
            {
                return stats;
            }
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
            if (stats?.Item != null && Eligibility.ShouldOfferBulkQty(stats.Item))
            {
                return stats;
            }
        }

        return null;
    }
}
