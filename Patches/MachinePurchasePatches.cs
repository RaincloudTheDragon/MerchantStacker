using System.Reflection;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace MerchantStacker.Patches;

/// <summary>
/// Rosary stringing machines use CostReference + TakeCurrency + CollectableItemCollect (not ShopItem).
/// </summary>
[HarmonyPatch]
internal static class MachinePurchasePatches
{
    private static int _pendingCollectQty;
    private static bool _skipNextCollect;
    private static bool _waitingForPicker;
    private static CollectableItem? _pendingItem;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(TakeCurrency), nameof(TakeCurrency.OnEnter))]
    private static bool TakeCurrencyPrefix(TakeCurrency __instance)
    {
        if (!MerchantStackerPlugin.Enabled.Value || PurchaseBatcher.IsBatching || _waitingForPicker)
        {
            return true;
        }

        if (__instance.CurrencyType.IsNone || __instance.Amount.Value <= 0)
        {
            return true;
        }

        if (!IsRosaryStringMachine(__instance))
        {
            return true;
        }

        var currency = (CurrencyType)(object)__instance.CurrencyType.Value;
        int unitCost = __instance.Amount.Value;

        CollectableItem? item = FindMachineCollectable(__instance);
        int room = item != null ? Eligibility.GetRoomUntilCap(item) : 20;
        int affordable = Eligibility.GetAffordableCount(unitCost, currency);
        int max = System.Math.Max(1, System.Math.Min(room, affordable));

        if (max <= 1 || affordable < 1)
        {
            return true;
        }

        string title = item != null ? item.GetPopupName() : "Rosary String";
        _waitingForPicker = true;
        _pendingItem = item;

        QuantityPicker.Instance.Open(
            title,
            unitCost,
            currency,
            max,
            onConfirm: qty =>
            {
                _waitingForPicker = false;
                if (qty <= 0)
                {
                    _skipNextCollect = true;
                    _pendingCollectQty = 0;
                    __instance.Finish();
                    return;
                }

                CurrencyManager.TakeCurrency(unitCost * qty, currency);
                _pendingCollectQty = qty;
                _skipNextCollect = false;
                __instance.Finish();
            },
            onCancel: () =>
            {
                _waitingForPicker = false;
                _skipNextCollect = true;
                _pendingCollectQty = 0;
                __instance.Finish();
            });

        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CollectableItemCollect), nameof(CollectableItemCollect.OnEnter))]
    private static bool CollectOnEnterPrefix(CollectableItemCollect __instance)
    {
        if (_skipNextCollect)
        {
            _skipNextCollect = false;
            _pendingCollectQty = 0;
            __instance.Finish();
            return false;
        }

        if (_pendingCollectQty > 0)
        {
            int qty = _pendingCollectQty;
            _pendingCollectQty = 0;
            var item = __instance.Item.Value as CollectableItem;
            if (item != null)
            {
                item.Collect(qty);
                MerchantStackerPlugin.Log.LogInfo($"Machine collected {qty}x {item.name}");
            }

            __instance.Finish();
            return false;
        }

        return true;
    }

    private static bool IsRosaryStringMachine(FsmStateAction action)
    {
        GameObject? owner = action.Owner;
        if (owner == null)
        {
            return false;
        }

        Transform t = owner.transform;
        while (t != null)
        {
            string n = t.name;
            if (n.Contains("rosary_string_machine", System.StringComparison.OrdinalIgnoreCase)
                || n.Contains("rosary_stringer", System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            t = t.parent;
        }

        return false;
    }

    private static CollectableItem? FindMachineCollectable(FsmStateAction action)
    {
        if (_pendingItem != null)
        {
            return _pendingItem;
        }

        try
        {
            Fsm? fsm = action.Fsm;
            if (fsm == null)
            {
                return null;
            }

            foreach (FsmObject objVar in fsm.Variables.ObjectVariables)
            {
                if (objVar.Value is CollectableItem collectable)
                {
                    return collectable;
                }
            }

            // Scan upcoming CollectableItemCollect actions in this FSM for the item reference.
            foreach (FsmState state in fsm.States)
            {
                foreach (FsmStateAction stateAction in state.Actions)
                {
                    if (stateAction is CollectableItemAction collectAction)
                    {
                        var item = TryGetCollectableFromAction(collectAction);
                        if (item != null)
                        {
                            return item;
                        }
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            MerchantStackerPlugin.Log.LogDebug($"FindMachineCollectable: {ex.Message}");
        }

        // Vanilla machine grants the small rosary string.
        return CollectableItemManager.GetItemByName("Rosary_Set_Small");
    }

    private static CollectableItem? TryGetCollectableFromAction(CollectableItemAction action)
    {
        foreach (FieldInfo field in action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (typeof(CollectableItem).IsAssignableFrom(field.FieldType))
            {
                return field.GetValue(action) as CollectableItem;
            }

            if (field.FieldType == typeof(FsmObject))
            {
                var fsmObj = field.GetValue(action) as FsmObject;
                if (fsmObj?.Value is CollectableItem c)
                {
                    return c;
                }
            }
        }

        return null;
    }
}
