using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using TMProOld;
using UnityEngine;

namespace MerchantStacker;

/// <summary>
/// Quantity session for bulk buys.
/// Merchant shops: draw/adjust inside the shop pane (never opens DialogueYesNoBox).
/// Machines / simple shops: may open DialogueYesNoBox when there is no shop pane.
/// </summary>
internal sealed class QuantityPicker : MonoBehaviour
{
    internal static QuantityPicker Instance { get; private set; } = null!;

    /// <summary>True while Open() is calling DialogueYesNoBox.Open (do not re-hijack).</summary>
    internal static bool SuppressHijack { get; private set; }

    private static readonly FieldInfo InstanceField =
        AccessTools.Field(typeof(DialogueYesNoBox), "_instance");

    private static readonly FieldInfo CurrentYesField =
        AccessTools.Field(typeof(YesNoBox), "currentYes");

    private static readonly FieldInfo CurrentNoField =
        AccessTools.Field(typeof(YesNoBox), "currentNo");

    private static readonly FieldInfo SelectedStateField =
        AccessTools.Field(typeof(YesNoBox), "selectedState");

    private static readonly MethodInfo DoEndMethod =
        AccessTools.Method(typeof(YesNoBox), "DoEnd");

    private static readonly FieldInfo RequiredCurrencyAmountField =
        AccessTools.Field(typeof(DialogueYesNoBox), "requiredCurrencyAmount");

    private static readonly FieldInfo CurrencyTextField =
        AccessTools.Field(typeof(DialogueYesNoBox), "currencyText");

    private static readonly FieldInfo InstantiatedItemsField =
        AccessTools.Field(typeof(DialogueYesNoBox), "instantiatedItems");

    private static readonly FieldInfo WillGetItemField =
        AccessTools.Field(typeof(DialogueYesNoBox), "willGetItem");

    private static readonly FieldInfo ItemTemplateField =
        AccessTools.Field(typeof(DialogueYesNoBox), "itemTemplate");

    private static readonly FieldInfo ItemsLayoutField =
        AccessTools.Field(typeof(DialogueYesNoBox), "itemsLayout");

    private static readonly FieldInfo ItemCostTextField =
        AccessTools.Field(typeof(ShopItemStats), "itemCostText");

    private static readonly FieldInfo TitleTextField =
        AccessTools.Field(typeof(ShopMenuStock), "titleText");

    private bool _active;
    private bool _hijacked;
    private bool _inShop;
    private int _quantity = 1;
    private int _min = 1;
    private int _max = 1;
    private int _unitCost;
    private CurrencyType _currency;
    private string _title = "Buy";
    private CollectableItem? _item;
    private ShopItemStats? _shopStats;
    private Transform? _shopRoot;
    private string? _originalCostText;
    private TextMeshPro? _boundCostText;
    private ShopMenuStock? _boundStock;
    private string? _originalShopTitle;
    private TextMeshPro? _boundQtyText;
    private string? _originalQtyText;
    private TextMeshPro? _boundHintText;
    private string? _originalHintText;
    private TextMeshPro? _boundTitleTmp;
    private GameObject? _ownedQtyGo;
    private TextMeshPro? _ownedQtyTmp;
    private readonly List<(TextMeshPro tmp, string original)> _extraBoundTexts = new();
    private Action? _originalYes;
    private Action? _originalNo;
    private Action<int>? _onConfirm;
    private Action? _onCancel;
    private Action? _purchaseDone;

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

    public bool IsOpen => _active;

    public int CurrentQuantity => _quantity;

    /// <summary>
    /// Quantity UI inside the merchant shop pane — replaces the shop's yes/no confirm.
    /// </summary>
    public void OpenInShop(
        Transform shopRoot,
        string title,
        CollectableItem? item,
        int unitCost,
        CurrencyType currency,
        int maxQuantity,
        ShopItemStats stats)
    {
        if (!MerchantStackerPlugin.Enabled.Value || maxQuantity <= 1 || _active || stats?.Item == null)
        {
            return;
        }

        BeginSession(title, item, unitCost, currency, maxQuantity, hijacked: false);
        _inShop = true;
        _shopRoot = shopRoot;
        _shopStats = stats;
        _onConfirm = null;
        _onCancel = null;
        BindShopCostText(stats);
        DumpShopTextMeshes("open");
        BindNativeShopLabels();
        EnsureOwnedQtyLabel();
        RefreshInShopHud();
        StartCoroutine(RebindNativeLabelsSoon());
        MerchantStackerPlugin.Log.LogInfo(
            $"In-shop qty: {_title} max={_max} cost={_unitCost} " +
            $"stock={(_boundStock != null)} costTmp={(_boundCostText != null)} " +
            $"qtyTmp={(_boundQtyText != null)} owned={(_ownedQtyTmp != null)} titleTmp={(_boundTitleTmp != null)}");
    }

    /// <summary>Optional FSM/HUD cleanup after an in-shop bulk buy finishes or cancels.</summary>
    public void SetPurchaseDoneCallback(Action? onDone)
    {
        _purchaseDone = onDone;
    }

    /// <summary>
    /// Attach quantity controls to an already-opening DialogueYesNoBox (non-shop flows).
    /// </summary>
    public void Hijack(
        DialogueYesNoBox box,
        string title,
        CollectableItem? item,
        int unitCost,
        CurrencyType currency,
        int maxQuantity,
        Action? originalYes,
        Action? originalNo)
    {
        if (!MerchantStackerPlugin.Enabled.Value || maxQuantity <= 1 || _active)
        {
            return;
        }

        BeginSession(title, item, unitCost, currency, maxQuantity, hijacked: true);
        _inShop = false;
        _originalYes = originalYes;
        _originalNo = originalNo;
        _onConfirm = null;
        _onCancel = null;

        CurrentYesField.SetValue(box, (Action)(() => Finish(confirmed: true)));
        CurrentNoField.SetValue(box, (Action)(() => Finish(confirmed: false)));

        EnsureWillGetDisplay(box, item);
        StartCoroutine(RefreshAfterOpen());
        MerchantStackerPlugin.Log.LogInfo($"Hijack DialogueYesNoBox: {_title} max={_max} cost={_unitCost}");
    }

    /// <summary>
    /// Open DialogueYesNoBox (machines / simple shop — no merchant shop pane).
    /// </summary>
    public void Open(
        string title,
        CollectableItem? item,
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

        BeginSession(title, item, unitCost, currency, maxQuantity, hijacked: false);
        _inShop = false;
        _onConfirm = onConfirm;
        _onCancel = onCancel;
        _originalYes = null;
        _originalNo = null;

        SuppressHijack = true;
        try
        {
            DialogueYesNoBox.Open(
                yes: () => Finish(confirmed: true),
                no: () => Finish(confirmed: false),
                returnHud: true,
                text: _title,
                currencyType: _currency,
                currencyAmount: _unitCost,
                items: null,
                amounts: null,
                displayHudPopup: true,
                consumeCurrency: false,
                willGetItem: _item,
                takeItemType: TakeItemTypes.Silent,
                displayType: YesNoAction.DisplayType.WillGetItems);
        }
        finally
        {
            SuppressHijack = false;
        }

        StartCoroutine(RefreshAfterOpen());
        MerchantStackerPlugin.Log.LogInfo($"Native qty open: {_title} max={_max} cost={_unitCost}");
    }

    private void BeginSession(
        string title,
        CollectableItem? item,
        int unitCost,
        CurrencyType currency,
        int maxQuantity,
        bool hijacked)
    {
        _title = string.IsNullOrEmpty(title) ? "Buy" : title;
        _item = item;
        _unitCost = Math.Max(1, unitCost);
        _currency = currency;
        _min = 1;
        _max = Math.Max(1, maxQuantity);
        _quantity = 1;
        _hijacked = hijacked;
        _inShop = false;
        _shopRoot = null;
        _shopStats = null;
        _active = true;
        PurchaseBatcher.PendingQuantity = 1;
        ResetHoldState();
        InventoryPaneInput.IsInputBlocked = true;
    }

    private void BindShopCostText(ShopItemStats stats)
    {
        _boundCostText = null;
        _originalCostText = null;
        _boundStock = null;
        _originalShopTitle = null;
        try
        {
            if (ItemCostTextField?.GetValue(stats) is TextMeshPro tmp)
            {
                _boundCostText = tmp;
                _originalCostText = tmp.text;
            }

            _boundStock = stats.GetComponentInParent<ShopMenuStock>();
            if (_boundStock != null)
            {
                _originalShopTitle = _boundStock.Title;
            }
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// Drive Silksong shop TextMeshPro only — no IMGUI popup, no foreign fonts.
    /// </summary>
    private void BindNativeShopLabels()
    {
        // Keep stock/cost bindings from BindShopCostText; only reset qty/hint picks.
        _boundQtyText = null;
        _originalQtyText = null;
        _boundHintText = null;
        _originalHintText = null;
        _boundTitleTmp = null;

        if (_shopRoot == null && _shopStats != null)
        {
            _shopRoot = _shopStats.transform.root;
        }

        if (_boundStock != null)
        {
            _boundTitleTmp = TitleTextField?.GetValue(_boundStock) as TextMeshPro;
        }

        var searchRoots = new List<Transform>();
        if (_shopRoot != null)
        {
            searchRoots.Add(_shopRoot);
        }

        if (_boundStock != null && !searchRoots.Contains(_boundStock.transform))
        {
            searchRoots.Add(_boundStock.transform);
        }

        if (_shopStats != null)
        {
            Transform statsRoot = _shopStats.transform.root;
            if (!searchRoots.Contains(statsRoot))
            {
                searchRoots.Add(statsRoot);
            }
        }

        var candidates = new List<TextMeshPro>();
        var seen = new HashSet<int>();
        foreach (Transform root in searchRoots)
        {
            foreach (TextMeshPro tmp in root.GetComponentsInChildren<TextMeshPro>(true))
            {
                if (tmp == null || !seen.Add(tmp.GetInstanceID()))
                {
                    continue;
                }

                if (tmp == _boundCostText || tmp == _boundTitleTmp || tmp == _ownedQtyTmp)
                {
                    continue;
                }

                string n = tmp.gameObject.name.ToLowerInvariant();
                if (n.Contains("yes") || n.Contains("no"))
                {
                    continue;
                }

                candidates.Add(tmp);
            }
        }

        candidates.Sort((a, b) =>
        {
            int active = b.gameObject.activeInHierarchy.CompareTo(a.gameObject.activeInHierarchy);
            return active != 0 ? active : b.fontSize.CompareTo(a.fontSize);
        });

        MerchantStackerPlugin.Log.LogInfo(
            $"TMP bind: roots={searchRoots.Count} candidates={candidates.Count} " +
            $"title={(_boundTitleTmp != null ? _boundTitleTmp.gameObject.name : "null")} " +
            $"cost={(_boundCostText != null ? _boundCostText.gameObject.name : "null")}");

        foreach (TextMeshPro tmp in candidates)
        {
            if (_boundQtyText == null)
            {
                _boundQtyText = tmp;
                _originalQtyText = tmp.text;
                MerchantStackerPlugin.Log.LogInfo(
                    $"Qty label ← '{PathOf(tmp)}' active={tmp.gameObject.activeInHierarchy} " +
                    $"size={tmp.fontSize} alpha={tmp.color.a:0.00} text='{Truncate(tmp.text)}'");
                continue;
            }

            if (_boundHintText == null)
            {
                _boundHintText = tmp;
                _originalHintText = tmp.text;
                MerchantStackerPlugin.Log.LogInfo(
                    $"Hint label ← '{PathOf(tmp)}' active={tmp.gameObject.activeInHierarchy} " +
                    $"size={tmp.fontSize} text='{Truncate(tmp.text)}'");
                break;
            }
        }

        foreach (InventoryArrowContainer arrows in EnumerateUnderRoots<InventoryArrowContainer>(searchRoots))
        {
            arrows.gameObject.SetActive(true);
        }
    }

    /// <summary>
    /// Clone the shop title TMP so we own a label the FSM will not wipe each frame.
    /// </summary>
    private void EnsureOwnedQtyLabel()
    {
        if (_ownedQtyTmp != null || _boundTitleTmp == null)
        {
            return;
        }

        try
        {
            _ownedQtyGo = UnityEngine.Object.Instantiate(
                _boundTitleTmp.gameObject,
                _boundTitleTmp.transform.parent);
            _ownedQtyGo.name = "MerchantStacker_Qty";
            _ownedQtyGo.SetActive(true);

            _ownedQtyTmp = _ownedQtyGo.GetComponent<TextMeshPro>();
            if (_ownedQtyTmp == null)
            {
                return;
            }

            // Sit just below the shop title, same local space / font / material.
            Transform t = _ownedQtyGo.transform;
            t.localPosition = _boundTitleTmp.transform.localPosition + new Vector3(0f, -1.1f, 0f);
            t.localRotation = _boundTitleTmp.transform.localRotation;
            t.localScale = _boundTitleTmp.transform.localScale;

            _ownedQtyTmp.enabled = true;
            _ownedQtyTmp.fontSize = Math.Max(_boundTitleTmp.fontSize * 1.4f, _boundTitleTmp.fontSize + 2f);
            Color c = _boundTitleTmp.color;
            c.a = 1f;
            _ownedQtyTmp.color = c;
            _ownedQtyTmp.alignment = TextAlignmentOptions.Center;

            var mr = _ownedQtyGo.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.enabled = true;
                mr.sortingOrder = Math.Max(mr.sortingOrder, 200);
            }

            MerchantStackerPlugin.Log.LogInfo(
                $"Owned qty TMP cloned from '{_boundTitleTmp.gameObject.name}' size={_ownedQtyTmp.fontSize}");
        }
        catch (Exception ex)
        {
            MerchantStackerPlugin.Log.LogWarning($"EnsureOwnedQtyLabel: {ex.Message}");
            _ownedQtyGo = null;
            _ownedQtyTmp = null;
        }
    }

    private void DumpShopTextMeshes(string reason)
    {
        try
        {
            Transform? root = _shopRoot;
            if (root == null && _shopStats != null)
            {
                root = _shopStats.transform.root;
            }

            if (root == null)
            {
                MerchantStackerPlugin.Log.LogWarning($"TMP dump ({reason}): no root");
                return;
            }

            TextMeshPro[] tmps = root.GetComponentsInChildren<TextMeshPro>(true);
            MerchantStackerPlugin.Log.LogInfo($"TMP dump ({reason}) under '{root.name}': count={tmps.Length}");
            int i = 0;
            foreach (TextMeshPro tmp in tmps)
            {
                if (tmp == null || i >= 24)
                {
                    break;
                }

                var mr = tmp.GetComponent<MeshRenderer>();
                MerchantStackerPlugin.Log.LogInfo(
                    $"  [{i}] '{PathOf(tmp)}' active={tmp.gameObject.activeInHierarchy} " +
                    $"enabled={tmp.enabled} size={tmp.fontSize:0.#} alpha={tmp.color.a:0.00} " +
                    $"renderer={(mr != null && mr.enabled)} sort={(mr != null ? mr.sortingOrder : -1)} " +
                    $"text='{Truncate(tmp.text)}'");
                i++;
            }
        }
        catch (Exception ex)
        {
            MerchantStackerPlugin.Log.LogWarning($"TMP dump failed: {ex.Message}");
        }
    }

    private System.Collections.IEnumerator RebindNativeLabelsSoon()
    {
        for (int i = 0; i < 4; i++)
        {
            yield return null;
            if (!_active || !_inShop)
            {
                yield break;
            }

            DumpShopTextMeshes($"frame{i}");
            BindNativeShopLabels();
            if (_ownedQtyTmp == null)
            {
                EnsureOwnedQtyLabel();
            }

            RefreshInShopHud();
        }
    }

    private void RefreshInShopHud()
    {
        int total = _unitCost * _quantity;
        string qtyLine = _quantity.ToString(CultureInfo.InvariantCulture);
        string titleLine = $"{_title}  × {_quantity}";
        string costLine = total.ToString(CultureInfo.InvariantCulture);

        // Native shop title + stock cost (re-applied every frame — FSM may overwrite).
        if (_boundStock != null)
        {
            _boundStock.Title = titleLine;
        }

        ApplyTmp(_boundTitleTmp, titleLine);
        ApplyTmp(_boundCostText, costLine);
        ApplyTmp(_boundQtyText, qtyLine);
        ApplyTmp(_boundHintText, costLine);
        ApplyTmp(_ownedQtyTmp, $"▲\n{qtyLine}\n▼");
    }

    private static void ApplyTmp(TextMeshPro? tmp, string value)
    {
        if (tmp == null)
        {
            return;
        }

        try
        {
            if (!tmp.gameObject.activeSelf)
            {
                tmp.gameObject.SetActive(true);
            }

            tmp.enabled = true;
            Color c = tmp.color;
            if (c.a < 0.9f)
            {
                c.a = 1f;
                tmp.color = c;
            }

            if (tmp.text != value)
            {
                tmp.SetText(value);
            }

            tmp.ForceMeshUpdate(true);
            var mr = tmp.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.enabled = true;
            }
        }
        catch
        {
            // ignored
        }
    }

    private void ClearBoundLabelRefs(bool restore)
    {
        if (restore)
        {
            if (_boundStock != null && _originalShopTitle != null)
            {
                _boundStock.Title = _originalShopTitle;
            }

            if (_boundCostText != null && _originalCostText != null)
            {
                ApplyTmp(_boundCostText, _originalCostText);
            }

            if (_boundQtyText != null && _originalQtyText != null)
            {
                ApplyTmp(_boundQtyText, _originalQtyText);
            }

            if (_boundHintText != null && _originalHintText != null)
            {
                ApplyTmp(_boundHintText, _originalHintText);
            }

            foreach ((TextMeshPro tmp, string original) in _extraBoundTexts)
            {
                ApplyTmp(tmp, original);
            }
        }

        if (_ownedQtyGo != null)
        {
            UnityEngine.Object.Destroy(_ownedQtyGo);
        }

        _ownedQtyGo = null;
        _ownedQtyTmp = null;
        _extraBoundTexts.Clear();
        _boundQtyText = null;
        _originalQtyText = null;
        _boundHintText = null;
        _originalHintText = null;
        _boundTitleTmp = null;
        if (restore)
        {
            _boundStock = null;
            _originalShopTitle = null;
            _boundCostText = null;
            _originalCostText = null;
        }
    }

    private void DestroyInShopHud(bool restoreTexts)
    {
        ClearBoundLabelRefs(restore: restoreTexts);
    }

    private static string PathOf(TextMeshPro tmp)
    {
        string path = tmp.gameObject.name;
        Transform? p = tmp.transform.parent;
        int guard = 0;
        while (p != null && guard++ < 6)
        {
            path = p.name + "/" + path;
            p = p.parent;
        }

        return path;
    }

    private static string Truncate(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }

        s = s.Replace('\n', ' ');
        return s.Length <= 40 ? s : s.Substring(0, 40) + "…";
    }

    private static IEnumerable<T> EnumerateUnderRoots<T>(List<Transform> roots)
        where T : Component
    {
        var seen = new HashSet<int>();
        foreach (Transform root in roots)
        {
            foreach (T c in root.GetComponentsInChildren<T>(true))
            {
                if (c != null && seen.Add(c.GetInstanceID()))
                {
                    yield return c;
                }
            }
        }
    }

    private System.Collections.IEnumerator RefreshAfterOpen()
    {
        yield return null;
        yield return null;
        if (_active && !_inShop)
        {
            RefreshNativeDisplay();
        }
    }

    private void Finish(bool confirmed)
    {
        if (!_active)
        {
            return;
        }

        int qty = Math.Max(1, _quantity);
        bool hijacked = _hijacked;
        bool inShop = _inShop;
        var originalYes = _originalYes;
        var originalNo = _originalNo;
        var onConfirm = _onConfirm;
        var onCancel = _onCancel;
        var shopStats = _shopStats;
        var purchaseDone = _purchaseDone;

        _active = false;
        _hijacked = false;
        _inShop = false;
        _shopRoot = null;
        _shopStats = null;
        _originalYes = null;
        _originalNo = null;
        _onConfirm = null;
        _onCancel = null;
        _purchaseDone = null;
        _item = null;
        DestroyInShopHud(restoreTexts: true);
        ResetHoldState();
        InventoryPaneInput.IsInputBlocked = false;

        if (confirmed)
        {
            PurchaseBatcher.PendingQuantity = qty;
            MerchantStackerPlugin.Log.LogInfo($"Qty confirmed: {qty}");
        }
        else
        {
            PurchaseBatcher.ClearPendingQuantity();
        }

        if (inShop)
        {
            if (confirmed && shopStats != null)
            {
                // Buy directly — shop FSM confirm already advanced past yes/no.
                PurchaseBatcher.ClearPendingQuantity();
                var stock = shopStats.GetComponentInParent<ShopMenuStock>();
                PurchaseBatcher.BuyShopItem(
                    shopStats,
                    qty,
                    subItemIndex: 0,
                    onComplete: () =>
                    {
                        stock?.SetWasItemPurchased(true);
                        purchaseDone?.Invoke();
                    });
            }
            else
            {
                purchaseDone?.Invoke();
            }

            return;
        }

        if (hijacked)
        {
            var box = InstanceField.GetValue(null) as YesNoBox;
            if (box != null)
            {
                CurrentYesField.SetValue(box, originalYes);
                CurrentNoField.SetValue(box, originalNo);
                SelectedStateField.SetValue(box, confirmed);
                DoEndMethod.Invoke(box, null);
            }
            else if (confirmed)
            {
                originalYes?.Invoke();
            }
            else
            {
                originalNo?.Invoke();
            }

            return;
        }

        ClearBoxHandlersAndClose();
        if (confirmed)
        {
            onConfirm?.Invoke(qty);
        }
        else
        {
            onCancel?.Invoke();
        }
    }

    private static void ClearBoxHandlersAndClose()
    {
        var box = InstanceField.GetValue(null) as DialogueYesNoBox;
        if (box != null)
        {
            CurrentYesField.SetValue(box, null);
            CurrentNoField.SetValue(box, null);
        }

        try
        {
            DialogueYesNoBox.ForceClose();
        }
        catch
        {
            // ignored
        }
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

        // Shop FSMs often rewrite title/cost every frame — keep ours on top.
        if (_inShop)
        {
            RefreshInShopHud();
        }

        int affordable = Eligibility.GetAffordableCount(_unitCost, _currency);
        int room = _item != null ? Eligibility.GetRoomUntilCap(_item) : _max;
        _max = Math.Max(1, Math.Min(Math.Max(1, affordable), Math.Max(1, room)));
        if (_quantity > _max)
        {
            _quantity = _max;
            RefreshDisplay();
        }

        var ih = ManagerSingleton<InputHandler>.Instance;
        if (ih == null)
        {
            return;
        }

        var actions = ih.inputActions;
        switch (Platform.Current.GetMenuAction(actions))
        {
            case Platform.MenuActions.Submit:
                Finish(confirmed: true);
                return;
            case Platform.MenuActions.Cancel:
                Finish(confirmed: false);
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
        int next = Math.Clamp(_quantity + delta, _min, _max);
        if (next == _quantity)
        {
            return;
        }

        _quantity = next;
        PurchaseBatcher.PendingQuantity = _quantity;
        RefreshDisplay();
        MerchantStackerPlugin.Log.LogInfo($"Qty -> {_quantity}");
    }

    private void RefreshDisplay()
    {
        if (_inShop)
        {
            RefreshInShopHud();
            return;
        }

        RefreshNativeDisplay();
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
            currencyText.text = totalCost.ToString(CultureInfo.InvariantCulture);
        }

        if (_item == null)
        {
            return;
        }

        WillGetItemField.SetValue(box, _item);
        EnsureWillGetDisplay(box, _item);

        if (InstantiatedItemsField.GetValue(box) is not List<SavedItemDisplay> items)
        {
            return;
        }

        foreach (SavedItemDisplay display in items)
        {
            if (display == null || !display.gameObject.activeSelf)
            {
                continue;
            }

            display.Setup(_item, _quantity);
            ForceAmountText(display, _quantity);
            break;
        }
    }

    private static void EnsureWillGetDisplay(DialogueYesNoBox box, CollectableItem? item)
    {
        if (item == null)
        {
            return;
        }

        try
        {
            if (InstantiatedItemsField.GetValue(box) is not List<SavedItemDisplay> items
                || ItemTemplateField.GetValue(box) is not SavedItemDisplay template)
            {
                return;
            }

            while (items.Count < 1)
            {
                items.Add(UnityEngine.Object.Instantiate(template, template.transform.parent));
            }

            items[0].gameObject.SetActive(true);
            items[0].Setup(item, 1);
            if (ItemsLayoutField.GetValue(box) is Behaviour layout)
            {
                layout.gameObject.SetActive(true);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void ForceAmountText(SavedItemDisplay display, int qty)
    {
        try
        {
            var amountField = AccessTools.Field(typeof(SavedItemDisplay), "amountText");
            if (amountField?.GetValue(display) is TMP_Text amountText)
            {
                amountText.gameObject.SetActive(true);
                amountText.text = qty.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // ignored
        }
    }
}
