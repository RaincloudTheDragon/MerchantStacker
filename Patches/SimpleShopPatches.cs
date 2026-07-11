using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MerchantStacker.Patches;

[HarmonyPatch]
internal static class SimpleShopPatches
{
    private static readonly FieldInfo SelectedIndexField =
        AccessTools.Field(typeof(SimpleShopMenu), "selectedIndex");

    private static readonly FieldInfo ShopItemsField =
        AccessTools.Field(typeof(SimpleShopMenu), "shopItems");

    private static readonly FieldInfo OwnerField =
        AccessTools.Field(typeof(SimpleShopMenu), "owner");

    private static readonly FieldInfo StateField =
        AccessTools.Field(typeof(SimpleShopMenu), "state");

    private static readonly FieldInfo OpenTimeField =
        AccessTools.Field(typeof(SimpleShopMenu), "openTime");

    private static readonly FieldInfo DidPurchaseField =
        AccessTools.Field(typeof(SimpleShopMenu), "didPurchase");

    private static readonly FieldInfo PurchasedIndexField =
        AccessTools.Field(typeof(SimpleShopMenu), "purchasedIndex");

    private static bool _handlingBulk;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SimpleShopMenu), "OnSubmitPressed")]
    private static bool OnSubmitPressedPrefix(SimpleShopMenu __instance)
    {
        if (!MerchantStackerPlugin.Enabled.Value || _handlingBulk || QuantityPicker.Instance.IsOpen)
        {
            return true;
        }

        float openTime = (float)OpenTimeField.GetValue(__instance)!;
        if (openTime > Time.time)
        {
            return true;
        }

        // State.ItemList == 0
        object state = StateField.GetValue(__instance)!;
        if ((int)state != 0)
        {
            return true;
        }

        var item = __instance.GetCurrentSelectedItem();
        if (item == null)
        {
            return true;
        }

        int cost = item.GetCost();
        if (cost <= 0 || PlayerData.instance.geo < cost)
        {
            return true;
        }

        // Simple shop items are not always ShopItem; only intercept when we can resolve a stackable collectable.
        if (!TryGetBulkInfo(item, out string title, out int maxQty))
        {
            return true;
        }

        if (maxQty <= 1)
        {
            return true;
        }

        int selectedIndex = (int)SelectedIndexField.GetValue(__instance)!;
        var owner = (SimpleShopMenuOwner)OwnerField.GetValue(__instance)!;

        QuantityPicker.Instance.Open(
            title,
            cost,
            CurrencyType.Money,
            maxQty,
            onConfirm: qty => ApplySimpleShopBulk(__instance, owner, selectedIndex, cost, qty),
            onCancel: () => { });

        return false;
    }

    private static void ApplySimpleShopBulk(
        SimpleShopMenu menu,
        SimpleShopMenuOwner owner,
        int selectedIndex,
        int unitCost,
        int quantity)
    {
        _handlingBulk = true;
        try
        {
            DidPurchaseField.SetValue(menu, true);
            PurchasedIndexField.SetValue(menu, selectedIndex);

            int bought = 0;
            for (int i = 0; i < quantity; i++)
            {
                if (PlayerData.instance.geo < unitCost)
                {
                    break;
                }

                CurrencyManager.TakeCurrency(unitCost, CurrencyType.Money);
                owner.PurchaseNoClose(selectedIndex);
                bought++;

                if (!owner.HasStockLeft())
                {
                    break;
                }

                owner.RefreshStock();
            }

            MerchantStackerPlugin.Log.LogInfo($"Simple shop bulk bought {bought}");
            PurchasedIndexField.SetValue(menu, -1);

            if (owner.HasStockLeft())
            {
                owner.RefreshStock();
                AccessTools.Method(typeof(SimpleShopMenu), "ScrollTo", new[] { typeof(int), typeof(bool) })
                    ?.Invoke(menu, new object[] { 0, true });
                menu.ConfirmNo(waitFrame: true);
            }
            else
            {
                var pane = AccessTools.Field(typeof(SimpleShopMenu), "pane").GetValue(menu) as InventoryPaneStandalone;
                pane?.PaneEnd();
            }
        }
        finally
        {
            _handlingBulk = false;
        }
    }

    private static bool TryGetBulkInfo(ISimpleShopItem item, out string title, out int maxQty)
    {
        title = item.GetDisplayName();
        maxQty = 1;

        // Heuristic: if the display name looks like a known refill, allow bulk by geo only (cap 20).
        // Caravan/quest simple shops usually DelayPurchase or are unique — skip those.
        if (item.DelayPurchase())
        {
            return false;
        }

        int cost = item.GetCost();
        int affordable = Eligibility.GetAffordableCount(cost, CurrencyType.Money);
        if (affordable <= 1)
        {
            return false;
        }

        // Prefer resolving via ShopItem if the concrete type wraps one.
        if (TryExtractShopItem(item, out ShopItem? shopItem) && shopItem != null)
        {
            if (!Eligibility.IsBulkEligible(shopItem))
            {
                return false;
            }

            title = shopItem.DisplayName;
            maxQty = Eligibility.GetMaxQuantity(shopItem);
            return maxQty > 1;
        }

        // Rosary-machine-like simple stock: infinite + stackable naming is unreliable.
        // Only auto-bulk when affordable > 1 and cost matches typical refill prices is too magic.
        // Leave non-ShopItem simple shops to MachinePurchasePatches / merchant path.
        return false;
    }

    private static bool TryExtractShopItem(ISimpleShopItem item, out ShopItem? shopItem)
    {
        shopItem = null;
        foreach (var field in item.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (typeof(ShopItem).IsAssignableFrom(field.FieldType))
            {
                shopItem = field.GetValue(item) as ShopItem;
                return shopItem != null;
            }
        }

        return false;
    }
}
