using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MerchantStacker.Patches;

/// <summary>
/// Hooks Silksong's purchase confirm box so quantity adjust happens on that UI
/// instead of showing a second placeholder menu afterward.
/// </summary>
[HarmonyPatch]
internal static class ConfirmDialogPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(
        typeof(DialogueYesNoBox),
        nameof(DialogueYesNoBox.Open),
        new Type[]
        {
            typeof(Action),
            typeof(Action),
            typeof(bool),
            typeof(string),
            typeof(CurrencyType),
            typeof(int),
            typeof(IReadOnlyList<SavedItem>),
            typeof(IReadOnlyList<int>),
            typeof(bool),
            typeof(bool),
            typeof(SavedItem),
            typeof(TakeItemTypes),
            typeof(YesNoAction.DisplayType),
        })]
    private static void OpenPrefix(
        ref Action yes,
        ref Action no,
        CurrencyType currencyType,
        int currencyAmount,
        IReadOnlyList<SavedItem> items,
        ref bool consumeCurrency,
        ref SavedItem willGetItem,
        ref YesNoAction.DisplayType displayType)
    {
        if (!MerchantStackerPlugin.Enabled.Value || PurchaseBatcher.IsBatching)
        {
            return;
        }

        if (!TryGetBulkTarget(
                ref willGetItem,
                ref displayType,
                currencyAmount,
                currencyType,
                items,
                out CollectableItem collectable,
                out int maxQty,
                out int unitCost))
        {
            PurchaseBatcher.ClearPendingQuantity();
            return;
        }

        // Merchant SetPurchased always charges; confirm must not also consume.
        consumeCurrency = false;

        Action originalYes = yes;
        Action originalNo = no;

        yes = () =>
        {
            int qty = QuantityPicker.Instance != null && QuantityPicker.Instance.IsOpen
                ? QuantityPicker.Instance.CurrentQuantity
                : Math.Max(1, PurchaseBatcher.PendingQuantity);
            PurchaseBatcher.PendingQuantity = Math.Max(1, qty);
            QuantityPicker.Instance?.EndSession();
            originalYes?.Invoke();
        };

        no = () =>
        {
            PurchaseBatcher.ClearPendingQuantity();
            QuantityPicker.Instance?.EndSession();
            originalNo?.Invoke();
        };

        QuantityPicker.Instance.BeginNativeSession(collectable, unitCost, currencyType, maxQty);
        MerchantStackerPlugin.Log.LogInfo(
            $"Qty confirm: {collectable.GetPopupName()} max={maxQty} cost={unitCost}");
    }

    private static bool TryGetBulkTarget(
        ref SavedItem willGetItem,
        ref YesNoAction.DisplayType displayType,
        int currencyAmount,
        CurrencyType currencyType,
        IReadOnlyList<SavedItem> items,
        out CollectableItem collectable,
        out int maxQty,
        out int unitCost)
    {
        collectable = null!;
        maxQty = 1;
        unitCost = currencyAmount;

        if (currencyAmount <= 0)
        {
            return false;
        }

        // Multi-item "give/take" prompts are not shop bulk buys.
        if (items != null && items.Count > 0)
        {
            return false;
        }

        CollectableItem? item = willGetItem as CollectableItem;
        ShopItem? shopItem = null;

        // Merchant confirms (e.g. Pebb) often pass currency cost only — no WillGetItem.
        if (item == null && TryResolveSelectedShopItem(currencyAmount, currencyType, out shopItem))
        {
            item = shopItem!.Item as CollectableItem;
            if (item != null)
            {
                willGetItem = item;
                displayType = YesNoAction.DisplayType.WillGetItems;
                unitCost = shopItem.Cost > 0 ? shopItem.Cost : currencyAmount;
            }
        }
        else if (item != null)
        {
            // Ensure the will-get icon/amount row is actually created for currency prompts.
            displayType = YesNoAction.DisplayType.WillGetItems;
            if (TryResolveSelectedShopItem(currencyAmount, currencyType, out shopItem)
                && shopItem!.Item == item)
            {
                unitCost = shopItem.Cost > 0 ? shopItem.Cost : currencyAmount;
            }
        }

        if (item == null)
        {
            return false;
        }

        if (shopItem != null)
        {
            if (!Eligibility.IsBulkEligible(shopItem))
            {
                return false;
            }

            maxQty = Eligibility.GetMaxQuantity(shopItem);
        }
        else
        {
            if (!item.CanGetMore() || item.IsAtMax())
            {
                return false;
            }

            int room = Eligibility.GetRoomUntilCap(item);
            int affordable = Eligibility.GetAffordableCount(unitCost, currencyType);
            maxQty = Math.Max(1, Math.Min(room, affordable));
        }

        if (maxQty <= 1)
        {
            return false;
        }

        collectable = item;
        return true;
    }

    /// <summary>
    /// Merchant shop UI keeps the highlighted line as InventoryItemManager.CurrentSelected
    /// (same GameObject PlayMaker stores in FSM var "Item" for SetShopItemPurchased).
    /// </summary>
    private static bool TryResolveSelectedShopItem(
        int currencyAmount,
        CurrencyType currencyType,
        out ShopItem? shopItem)
    {
        shopItem = null;

        foreach (InventoryItemManager manager in UnityEngine.Object.FindObjectsByType<InventoryItemManager>(FindObjectsSortMode.None))
        {
            if (manager == null || !manager.isActiveAndEnabled)
            {
                continue;
            }

            InventoryItemSelectable? selected = manager.CurrentSelected;
            if (selected == null)
            {
                continue;
            }

            if (!TryGetShopItemFrom(selected.gameObject, out ShopItem? candidate) || candidate == null)
            {
                continue;
            }

            if (!MatchesPurchase(candidate, currencyAmount, currencyType))
            {
                continue;
            }

            shopItem = candidate;
            return true;
        }

        // Fallback: PlayMaker "Item" variable written by InventoryItemManager.SetSelected.
        foreach (PlayMakerFSM fsm in UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None))
        {
            if (fsm == null || !fsm.isActiveAndEnabled || fsm.FsmVariables == null)
            {
                continue;
            }

            var itemVar = fsm.FsmVariables.FindFsmGameObject("Item");
            if (itemVar == null || itemVar.Value == null)
            {
                continue;
            }

            if (!TryGetShopItemFrom(itemVar.Value, out ShopItem? candidate) || candidate == null)
            {
                continue;
            }

            if (!MatchesPurchase(candidate, currencyAmount, currencyType))
            {
                continue;
            }

            shopItem = candidate;
            return true;
        }

        return false;
    }

    private static bool TryGetShopItemFrom(GameObject go, out ShopItem? shopItem)
    {
        shopItem = null;
        var stats = go.GetComponent<ShopItemStats>()
            ?? go.GetComponentInChildren<ShopItemStats>(includeInactive: true)
            ?? go.GetComponentInParent<ShopItemStats>();
        if (stats == null || stats.Item == null)
        {
            return false;
        }

        shopItem = stats.Item;
        return true;
    }

    private static bool MatchesPurchase(ShopItem item, int currencyAmount, CurrencyType currencyType)
    {
        if (item.CurrencyType != currencyType)
        {
            return false;
        }

        // CostReference / overrides can differ slightly; allow exact match only.
        return item.Cost == currencyAmount;
    }
}
