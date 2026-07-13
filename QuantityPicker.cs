using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMProOld;
using UnityEngine;

namespace MerchantStacker;

/// <summary>
/// Quantity adjustment layered on Silksong's native DialogueYesNoBox confirm UI.
/// Up/down change count; cost and item amount update live on the existing box.
/// </summary>
internal sealed class QuantityPicker : MonoBehaviour
{
    internal static QuantityPicker Instance { get; private set; } = null!;

    private static readonly FieldInfo InstanceField =
        AccessTools.Field(typeof(DialogueYesNoBox), "_instance");

    private static readonly FieldInfo RequiredCurrencyAmountField =
        AccessTools.Field(typeof(DialogueYesNoBox), "requiredCurrencyAmount");

    private static readonly FieldInfo CurrencyTextField =
        AccessTools.Field(typeof(DialogueYesNoBox), "currencyText");

    private static readonly FieldInfo InstantiatedItemsField =
        AccessTools.Field(typeof(DialogueYesNoBox), "instantiatedItems");

    private static readonly FieldInfo WillGetItemField =
        AccessTools.Field(typeof(DialogueYesNoBox), "willGetItem");

    private bool _sessionActive;
    private int _quantity = 1;
    private int _min = 1;
    private int _max = 1;
    private int _unitCost;
    private CurrencyType _currency;
    private CollectableItem? _willGetItem;

    private float _upTimer;
    private float _downTimer;
    private float _upInterval;
    private float _downInterval;
    private bool _upHeld;
    private bool _downHeld;
    private int _heldStep;

    private void Awake()
    {
        Instance = this;
    }

    public bool IsOpen => _sessionActive;

    public int CurrentQuantity => _quantity;

    /// <summary>
    /// Begin adjusting quantity on the currently opening DialogueYesNoBox.
    /// </summary>
    public void BeginNativeSession(CollectableItem willGetItem, int unitCost, CurrencyType currency, int maxQuantity)
    {
        _willGetItem = willGetItem;
        _unitCost = Math.Max(1, unitCost);
        _currency = currency;
        _min = 1;
        _max = Math.Max(1, maxQuantity);
        _quantity = 1;
        _sessionActive = true;
        PurchaseBatcher.PendingQuantity = 1;
        ResetHoldState();
        // Defer one frame so Open finishes wiring the box.
        StartCoroutine(RefreshNextFrame());
        MerchantStackerPlugin.Log.LogDebug($"Native qty session: {willGetItem.GetPopupName()} max={_max} cost={_unitCost}");
    }

    public void EndSession()
    {
        _sessionActive = false;
        _willGetItem = null;
        ResetHoldState();
    }

    private System.Collections.IEnumerator RefreshNextFrame()
    {
        yield return null;
        if (_sessionActive)
        {
            RefreshNativeDisplay();
        }
    }

    private void Update()
    {
        if (!_sessionActive)
        {
            return;
        }

        // Session ends when the confirm box instance goes away / closes.
        if (InstanceField.GetValue(null) == null)
        {
            EndSession();
            return;
        }

        int affordable = Eligibility.GetAffordableCount(_unitCost, _currency);
        int room = _willGetItem != null ? Eligibility.GetRoomUntilCap(_willGetItem) : _max;
        _max = Math.Max(1, Math.Min(affordable, room));
        if (_quantity > _max)
        {
            _quantity = _max;
            RefreshNativeDisplay();
        }

        var ih = ManagerSingleton<InputHandler>.Instance;
        if (ih == null)
        {
            return;
        }

        var actions = ih.inputActions;
        // Do not steal Submit/Cancel — DialogueYesNoBox yes/no handles those.

        int dpad = MerchantStackerPlugin.DpadStep.Value;
        int stick = MerchantStackerPlugin.StickStep.Value;

        bool upPressed = actions.Up.WasPressed || actions.RsUp.WasPressed;
        bool downPressed = actions.Down.WasPressed || actions.RsDown.WasPressed;
        bool upHeld = actions.Up.IsPressed || actions.RsUp.IsPressed;
        bool downHeld = actions.Down.IsPressed || actions.RsDown.IsPressed;

        int pressStep = actions.RsUp.WasPressed || actions.RsDown.WasPressed ? stick : dpad;
        int holdStep = actions.RsUp.IsPressed || actions.RsDown.IsPressed ? stick : dpad;

        if (upPressed)
        {
            Adjust(pressStep);
            BeginHold(up: true, holdStep);
        }
        else if (downPressed)
        {
            Adjust(-pressStep);
            BeginHold(up: false, holdStep);
        }
        else
        {
            TickHold(upHeld, downHeld, holdStep);
        }
    }

    private void BeginHold(bool up, int step)
    {
        _heldStep = step;
        float delay = MerchantStackerPlugin.HoldInitialDelay.Value;
        if (up)
        {
            _upHeld = true;
            _downHeld = false;
            _upTimer = delay;
            _upInterval = delay;
        }
        else
        {
            _downHeld = true;
            _upHeld = false;
            _downTimer = delay;
            _downInterval = delay;
        }
    }

    private void TickHold(bool upHeld, bool downHeld, int step)
    {
        _heldStep = step;
        float minRepeat = MerchantStackerPlugin.HoldMinRepeat.Value;
        float accel = MerchantStackerPlugin.HoldAccel.Value;
        float dt = Time.unscaledDeltaTime;

        if (upHeld && _upHeld)
        {
            _upTimer -= dt;
            if (_upTimer <= 0f)
            {
                Adjust(_heldStep);
                _upInterval = Math.Max(minRepeat, _upInterval * accel);
                _upTimer = _upInterval;
            }
        }
        else
        {
            _upHeld = false;
            _upInterval = MerchantStackerPlugin.HoldInitialDelay.Value;
        }

        if (downHeld && _downHeld)
        {
            _downTimer -= dt;
            if (_downTimer <= 0f)
            {
                Adjust(-_heldStep);
                _downInterval = Math.Max(minRepeat, _downInterval * accel);
                _downTimer = _downInterval;
            }
        }
        else
        {
            _downHeld = false;
            _downInterval = MerchantStackerPlugin.HoldInitialDelay.Value;
        }
    }

    private void Adjust(int delta)
    {
        int next = Math.Clamp(_quantity + delta, _min, _max);
        if (next == _quantity)
        {
            return;
        }

        _quantity = next;
        PurchaseBatcher.PendingQuantity = _quantity;
        RefreshNativeDisplay();
    }

    private void ResetHoldState()
    {
        _upTimer = 0f;
        _downTimer = 0f;
        _upHeld = false;
        _downHeld = false;
        _upInterval = MerchantStackerPlugin.HoldInitialDelay.Value;
        _downInterval = MerchantStackerPlugin.HoldInitialDelay.Value;
        _heldStep = MerchantStackerPlugin.DpadStep.Value;
    }

    private void RefreshNativeDisplay()
    {
        var box = InstanceField.GetValue(null) as DialogueYesNoBox;
        if (box == null)
        {
            return;
        }

        int totalCost = _unitCost * _quantity;
        RequiredCurrencyAmountField.SetValue(box, totalCost);

        if (CurrencyTextField.GetValue(box) is TMP_Text currencyText)
        {
            currencyText.text = totalCost > 0 ? totalCost.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        }

        if (_willGetItem != null && InstantiatedItemsField.GetValue(box) is List<SavedItemDisplay> items)
        {
            foreach (SavedItemDisplay display in items)
            {
                if (display != null && display.gameObject.activeSelf)
                {
                    // Force amount text even if DisplayAmount is false for some items.
                    display.Setup(_willGetItem, _quantity);
                    TryForceAmountText(display, _quantity);
                    break;
                }
            }
        }

        // Keep willGetItem reference in sync for InactiveYesText / at-max checks.
        WillGetItemField.SetValue(box, _willGetItem);
    }

    private static void TryForceAmountText(SavedItemDisplay display, int qty)
    {
        try
        {
            var amountField = AccessTools.Field(typeof(SavedItemDisplay), "amountText");
            if (amountField?.GetValue(display) is TMP_Text amountText)
            {
                amountText.text = qty > 1
                    ? qty.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty;
            }
        }
        catch
        {
            // ignored
        }
    }
}
