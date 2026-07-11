using System;
using UnityEngine;

namespace MerchantStacker;

/// <summary>
/// Quantity submenu: d-pad ±step, right stick ±larger step, hold to accelerate.
/// </summary>
internal sealed class QuantityPicker : MonoBehaviour
{
    internal static QuantityPicker Instance { get; private set; } = null!;

    private bool _active;
    private int _quantity = 1;
    private int _min = 1;
    private int _max = 1;
    private int _unitCost;
    private CurrencyType _currency;
    private string _title = "Buy";
    private Action<int>? _onConfirm;
    private Action? _onCancel;

    private float _upTimer;
    private float _downTimer;
    private float _upInterval;
    private float _downInterval;
    private bool _upHeld;
    private bool _downHeld;
    private int _heldStep;

    private GUIStyle? _panelStyle;
    private GUIStyle? _titleStyle;
    private GUIStyle? _qtyStyle;
    private GUIStyle? _hintStyle;
    private GUIStyle? _arrowStyle;
    private Texture2D? _panelTex;

    private void Awake()
    {
        Instance = this;
    }

    public bool IsOpen => _active;

    public void Open(
        string title,
        int unitCost,
        CurrencyType currency,
        int maxQuantity,
        Action<int> onConfirm,
        Action onCancel)
    {
        if (!MerchantStackerPlugin.Enabled.Value || maxQuantity < 1)
        {
            onConfirm(1);
            return;
        }

        _title = string.IsNullOrEmpty(title) ? "Buy" : title;
        _unitCost = Math.Max(0, unitCost);
        _currency = currency;
        _min = 1;
        _max = Math.Max(1, maxQuantity);
        _quantity = 1;
        _onConfirm = onConfirm;
        _onCancel = onCancel;
        _active = true;
        ResetHoldState();
        MerchantStackerPlugin.Log.LogDebug($"Quantity picker open: {_title} max={_max} cost={_unitCost}");
    }

    public void Close()
    {
        _active = false;
        _onConfirm = null;
        _onCancel = null;
        ResetHoldState();
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

    private void Update()
    {
        if (!_active)
        {
            return;
        }

        // Refresh max in case currency/cap changed while open.
        int affordable = Eligibility.GetAffordableCount(_unitCost, _currency);
        if (affordable < _max)
        {
            _max = Math.Max(1, affordable);
        }

        if (_quantity > _max)
        {
            _quantity = _max;
        }

        var ih = ManagerSingleton<InputHandler>.Instance;
        if (ih == null)
        {
            return;
        }

        var actions = ih.inputActions;
        var platform = Platform.Current;

        switch (platform.GetMenuAction(actions))
        {
            case Platform.MenuActions.Submit:
                Confirm();
                return;
            case Platform.MenuActions.Cancel:
                Cancel();
                return;
        }

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
        _quantity = Math.Clamp(_quantity + delta, _min, _max);
    }

    private void Confirm()
    {
        if (!_active)
        {
            return;
        }

        int qty = _quantity;
        var cb = _onConfirm;
        Close();
        cb?.Invoke(qty);
    }

    private void Cancel()
    {
        if (!_active)
        {
            return;
        }

        var cb = _onCancel;
        Close();
        cb?.Invoke();
    }

    private void OnGUI()
    {
        if (!_active)
        {
            return;
        }

        EnsureStyles();

        float w = 420f;
        float h = 220f;
        var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        GUI.Box(rect, GUIContent.none, _panelStyle);

        var titleRect = new Rect(rect.x, rect.y + 16f, rect.width, 28f);
        GUI.Label(titleRect, _title, _titleStyle);

        var arrowUp = new Rect(rect.x, rect.y + 52f, rect.width, 28f);
        GUI.Label(arrowUp, "▲", _arrowStyle);

        var qtyRect = new Rect(rect.x, rect.y + 82f, rect.width, 56f);
        GUI.Label(qtyRect, _quantity.ToString(System.Globalization.CultureInfo.InvariantCulture), _qtyStyle);

        var arrowDown = new Rect(rect.x, rect.y + 138f, rect.width, 28f);
        GUI.Label(arrowDown, "▼", _arrowStyle);

        int total = _unitCost * _quantity;
        string currency = _currency == CurrencyType.Shard ? "Shards" : "Rosaries";
        var hint = new Rect(rect.x + 12f, rect.y + h - 40f, rect.width - 24f, 28f);
        GUI.Label(hint, $"Total {total} {currency}  ·  D-pad ±{MerchantStackerPlugin.DpadStep.Value}  ·  RS ±{MerchantStackerPlugin.StickStep.Value}", _hintStyle);
    }

    private void EnsureStyles()
    {
        if (_panelStyle != null)
        {
            return;
        }

        _panelTex = new Texture2D(1, 1);
        _panelTex.SetPixel(0, 0, new Color(0.05f, 0.05f, 0.08f, 0.92f));
        _panelTex.Apply();

        _panelStyle = new GUIStyle(GUI.skin.box)
        {
            normal = { background = _panelTex },
        };

        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.92f, 0.9f, 0.82f) },
        };

        _qtyStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 42,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white },
        };

        _arrowStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 22,
            normal = { textColor = new Color(0.75f, 0.78f, 0.85f) },
        };

        _hintStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 12,
            normal = { textColor = new Color(0.7f, 0.7f, 0.75f) },
            wordWrap = true,
        };
    }
}
