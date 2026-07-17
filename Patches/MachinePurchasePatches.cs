using System.Reflection;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace MerchantStacker.Patches;

/// <summary>
/// Rosary stringing machines: if confirm already chose a quantity, multiply Take/Collect.
/// Otherwise fall back to opening the native DialogueYesNoBox (with qty session).
/// </summary>
[HarmonyPatch]
internal static class MachinePurchasePatches
{
    private static int _pendingCollectQty;
    private static bool _skipNextCollect;
    private static bool _waitingForConfirm;
    private static CollectableItem? _pendingItem;
    private static TakeCurrency? _heldTakeAction;
    private static int _heldUnitCost;
    private static CurrencyType _heldCurrency;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(TakeCurrency), nameof(TakeCurrency.OnEnter))]
    private static bool TakeCurrencyPrefix(TakeCurrency __instance)
    {
        if (!MerchantStackerPlugin.Enabled.Value || PurchaseBatcher.IsBatching || _waitingForConfirm)
        {
            return true;
        }

        if (__instance.CurrencyType.IsNone || __instance.Amount.Value <= 0)
        {
            return true;
        }

        // Confirm dialog already chose quantity (merchant-style machine flow).
        int pendingQty = PurchaseBatcher.ConsumePendingQuantity();
        if (pendingQty > 1 && IsRosaryStringMachine(__instance))
        {
            var currency = (CurrencyType)(object)__instance.CurrencyType.Value;
            int unitCost = __instance.Amount.Value;
            CurrencyManager.TakeCurrency(unitCost * pendingQty, currency);
            _pendingCollectQty = pendingQty;
            _skipNextCollect = false;
            __instance.Finish();
            return false;
        }

        if (pendingQty == 1)
        {
            // Single purchase via native confirm — let vanilla TakeCurrency run.
            return true;
        }

        if (!IsRosaryStringMachine(__instance))
        {
            return true;
        }

        var currencyType = (CurrencyType)(object)__instance.CurrencyType.Value;
        int unit = __instance.Amount.Value;
        CollectableItem? item = FindMachineCollectable(__instance);
        int room = item != null ? Eligibility.GetRoomUntilCap(item) : 20;
        int affordable = Eligibility.GetAffordableCount(unit, currencyType);
        int max = System.Math.Max(1, System.Math.Min(room, affordable));

        if (max <= 1 || item == null)
        {
            return true;
        }

        // Open quantity picker (owns pad; native confirm blocks vertical input).
        _waitingForConfirm = true;
        _heldTakeAction = __instance;
        _heldUnitCost = unit;
        _heldCurrency = currencyType;
        _pendingItem = item;

        QuantityPicker.Instance.Open(
            title: item.GetPopupName(),
            item: item,
            unitCost: unit,
            currency: currencyType,
            maxQuantity: max,
            onConfirm: qty =>
            {
                _waitingForConfirm = false;
                var action = _heldTakeAction;
                _heldTakeAction = null;
                if (action == null)
                {
                    return;
                }

                CurrencyManager.TakeCurrency(_heldUnitCost * qty, _heldCurrency);
                _pendingCollectQty = qty;
                _skipNextCollect = false;
                action.Finish();
            },
            onCancel: () =>
            {
                _waitingForConfirm = false;
                PurchaseBatcher.ClearPendingQuantity();
                _skipNextCollect = true;
                _pendingCollectQty = 0;
                var action = _heldTakeAction;
                _heldTakeAction = null;
                action?.Finish();
            });

        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CollectableItemAction), nameof(CollectableItemAction.OnEnter))]
    private static bool CollectOnEnterPrefix(CollectableItemAction __instance)
    {
        if (__instance is not CollectableItemCollect collect)
        {
            return true;
        }

        if (_skipNextCollect)
        {
            _skipNextCollect = false;
            _pendingCollectQty = 0;
            collect.Finish();
            return false;
        }

        if (_pendingCollectQty > 0)
        {
            int qty = _pendingCollectQty;
            _pendingCollectQty = 0;
            var item = collect.Item.Value as CollectableItem;
            if (item != null)
            {
                item.Collect(qty);
                MerchantStackerPlugin.Log.LogInfo($"Machine collected {qty}x {item.name}");
            }

            collect.Finish();
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
