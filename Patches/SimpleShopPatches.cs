using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MerchantStacker.Patches;

[HarmonyPatch]
internal static class SimpleShopPatches
{
    private static readonly FieldInfo SelectedIndexField =
        AccessTools.Field(typeof(SimpleShopMenu), "selectedIndex");

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
    private static SimpleShopMenu? _pendingMenu;
    private static SimpleShopMenuOwner? _pendingOwner;
    private static int _pendingIndex;
    private static int _pendingUnitCost;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SimpleShopMenu), "OnSubmitPressed")]
    private static bool OnSubmitPressedPrefix(SimpleShopMenu __instance)
    {
        if (!MerchantStackerPlugin.Enabled.Value
            || _handlingBulk
            || QuantityPicker.Instance == null
            || QuantityPicker.Instance.IsOpen)
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

        if (!TryExtractShopItem(item, out ShopItem? shopItem) || shopItem == null || !Eligibility.IsBulkEligible(shopItem))
        {
            return true;
        }

        int maxQty = Eligibility.GetMaxQuantity(shopItem);
        if (maxQty <= 1)
        {
            return true;
        }

        int selectedIndex = (int)SelectedIndexField.GetValue(__instance)!;
        var owner = (SimpleShopMenuOwner)OwnerField.GetValue(__instance)!;

        _pendingMenu = __instance;
        _pendingOwner = owner;
        _pendingIndex = selectedIndex;
        _pendingUnitCost = cost;

        QuantityPicker.Instance.Open(
            title: shopItem.DisplayName,
            item: shopItem.Item as CollectableItem,
            unitCost: cost,
            currency: shopItem.CurrencyType,
            maxQuantity: maxQty,
            onConfirm: qty =>
            {
                var menu = _pendingMenu;
                var pendingOwner = _pendingOwner;
                int index = _pendingIndex;
                int unitCost = _pendingUnitCost;
                ClearPending();
                if (menu != null && pendingOwner != null)
                {
                    ApplySimpleShopBulk(menu, pendingOwner, index, unitCost, qty);
                }
            },
            onCancel: ClearPending);

        return false;
    }

    private static void ClearPending()
    {
        _pendingMenu = null;
        _pendingOwner = null;
        _pendingIndex = -1;
        _pendingUnitCost = 0;
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
