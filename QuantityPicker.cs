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

    private static readonly FieldInfo CostSpriteField =
        AccessTools.Field(typeof(ShopItemStats), "costSprite");

    private static readonly FieldInfo ItemSpriteField =
        AccessTools.Field(typeof(ShopItemStats), "itemSprite");

    private static readonly FieldInfo ScrollUpArrowField =
        AccessTools.Field(typeof(ScrollView), "upArrow");

    private static readonly FieldInfo ScrollDownArrowField =
        AccessTools.Field(typeof(ScrollView), "downArrow");

    private static readonly FieldInfo SimpleUpArrowField =
        AccessTools.Field(typeof(SimpleShopMenu), "upArrow");

    private static readonly FieldInfo SimpleDownArrowField =
        AccessTools.Field(typeof(SimpleShopMenu), "downArrow");

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
    private GameObject? _hudRoot;
    private TextMeshPro? _hudQty;
    private TextMeshPro? _hudCost;
    private GameObject? _hudArrowUp;
    private GameObject? _hudArrowDown;
    private SpriteRenderer? _hudCurrencyIcon;
    private bool _costIsNative;
    private string? _nativeMoneyCostOriginal;
    private GameObject? _hiddenConfirmList;
    private GameObject? _hiddenConfirmMsg;
    private readonly List<GameObject> _hiddenNativeCost = new();
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
        BuildConfirmQtyHud();
        RefreshInShopHud();
        StartCoroutine(EnsureHudSoon());
        MerchantStackerPlugin.Log.LogInfo(
            $"In-shop qty: {_title} max={_max} cost={_unitCost} hud={(_hudRoot != null)}");
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
    /// Qty HUD using confirm-local layout: small Pointer carets (rotated vertical) + qty,
    /// reusing native Costs/Money for rosary icon + total.
    /// </summary>
    private void BuildConfirmQtyHud()
    {
        if (_hudRoot != null || _shopStats == null)
        {
            return;
        }

        try
        {
            Transform menuRoot = _shopStats.transform.root;
            Transform? confirmGroup = FindNamedTransform(menuRoot, "Item Confirm Group");
            Transform? confirm = FindNamedTransform(menuRoot, "Confirm");
            if (confirm == null)
            {
                MerchantStackerPlugin.Log.LogWarning("Qty HUD: Confirm transform not found");
                return;
            }

            TextMeshPro? template = FindTemplateTmp(confirm) ?? _boundCostText;
            if (template == null)
            {
                MerchantStackerPlugin.Log.LogWarning("Qty HUD: no TMP template");
                return;
            }

            Transform hudParent = confirmGroup != null ? confirmGroup : confirm;
            DumpConfirmGroup(hudParent);
            HideConfirmChrome(confirm, confirmGroup);

            // Reuse native money row (Geo Sprite + Item cost) — do not clone/hide it.
            BindNativeMoneyCost(confirm);

            _hudRoot = new GameObject("MerchantStacker_QtyHud");
            _hudRoot.layer = hudParent.gameObject.layer;
            _hudRoot.transform.SetParent(hudParent, false);
            _hudRoot.transform.localPosition = Vector3.zero;
            _hudRoot.transform.localRotation = Quaternion.identity;
            _hudRoot.transform.localScale = Vector3.one;

            // Layout in confirm-group local space (same plane as Item Sprite, z≈-3).
            GetQtyColumnLocal(hudParent, out Vector3 qtyLocal, out Vector3 upLocal, out Vector3 downLocal);

            // Match native cost font size (~5) rather than Yes (10).
            float fs = _hudCost != null ? Math.Max(5f, _hudCost.fontSize) : Math.Max(5f, template.fontSize);
            _hudQty = CloneTmp(template, _hudRoot.transform, "Qty", qtyLocal, "1", fs * 1.35f);

            if (_hudCost == null)
            {
                // Fallback clone if Money/Item cost missing.
                Vector3 costLocal = qtyLocal + new Vector3(2.4f, 0f, 0f);
                _hudCost = CloneTmp(
                    template, _hudRoot.transform, "TotalCost", costLocal, "0", fs,
                    TextAlignmentOptions.Left);
                _costIsNative = false;

                var srcCostIcon = CostSpriteField?.GetValue(_shopStats) as SpriteRenderer;
                if (srcCostIcon != null)
                {
                    _hudCurrencyIcon = CloneSprite(
                        srcCostIcon, _hudRoot.transform, "CurrencyIcon",
                        qtyLocal + new Vector3(1.5f, 0f, 0f), scaleMul: 1f);
                    Sprite? currency = _shopStats.GetCurrencySprite();
                    if (currency != null)
                    {
                        _hudCurrencyIcon.sprite = currency;
                    }
                }
            }

            if (TryCreateVerticalArrows(confirm, hudParent, upLocal, downLocal))
            {
                MerchantStackerPlugin.Log.LogInfo("Qty HUD arrows: vertical (pointer/small)");
            }
            else
            {
                MerchantStackerPlugin.Log.LogWarning("Qty HUD: no small arrow templates found");
            }

            MerchantStackerPlugin.Log.LogInfo(
                $"Qty HUD local qty={qtyLocal} nativeCost={_costIsNative} arrows={(_hudArrowUp != null)}");
        }
        catch (Exception ex)
        {
            MerchantStackerPlugin.Log.LogError($"BuildConfirmQtyHud: {ex}");
        }
    }

    /// <summary>Use Confirm/Costs/Money/Item cost + Geo Sprite instead of cloning duplicates.</summary>
    private void BindNativeMoneyCost(Transform confirm)
    {
        _costIsNative = false;
        _hudCost = null;
        _hudCurrencyIcon = null;

        Transform? money = null;
        foreach (Transform t in confirm.GetComponentsInChildren<Transform>(true))
        {
            if (t != null && t.name == "Money" && t.parent != null && t.parent.name == "Costs")
            {
                money = t;
                break;
            }
        }

        if (money == null)
        {
            return;
        }

        foreach (TextMeshPro tmp in money.GetComponentsInChildren<TextMeshPro>(true))
        {
            if (tmp != null && tmp.gameObject.name == "Item cost")
            {
                _hudCost = tmp;
                _nativeMoneyCostOriginal = tmp.text;
                _costIsNative = true;
                tmp.gameObject.SetActive(true);
                break;
            }
        }

        foreach (SpriteRenderer sr in money.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (sr == null)
            {
                continue;
            }

            // Prefer the Geo Sprite specifically.
            if (sr.gameObject.name == "Geo Sprite"
                || (ContainsIgnore(sr.gameObject.name, "geo") && _hudCurrencyIcon == null))
            {
                _hudCurrencyIcon = sr;
                sr.enabled = true;
                sr.gameObject.SetActive(true);
            }
        }

        // Hide material/tool cost row so only rosaries show for money purchases.
        Transform? costs = money.parent;
        if (costs != null)
        {
            foreach (Transform child in costs)
            {
                if (child != null && child.name != "Money" && child.gameObject.activeSelf)
                {
                    child.gameObject.SetActive(false);
                    _hiddenNativeCost.Add(child.gameObject);
                }
            }
        }
    }

    /// <summary>
    /// Qty column just right of Item Sprite, on the same local Z plane as the confirm art.
    /// </summary>
    private static void GetQtyColumnLocal(
        Transform hudParent,
        out Vector3 qtyLocal,
        out Vector3 upLocal,
        out Vector3 downLocal)
    {
        Transform? item = FindNamedTransform(hudParent, "Item Sprite");
        Vector3 itemLp = item != null ? item.localPosition : new Vector3(-3.4f, 1.5f, -3f);
        float z = itemLp.z;

        // Dump: Item Sprite ≈ (-3.44, 1.52, -3); sit clear to its right.
        qtyLocal = new Vector3(itemLp.x + 2.55f, itemLp.y, z);
        upLocal = new Vector3(qtyLocal.x, qtyLocal.y + 1.05f, z);
        downLocal = new Vector3(qtyLocal.x, qtyLocal.y - 1.05f, z);
    }

    /// <summary>
    /// Prefer small menu Pointer carets (rotated to vertical). Avoid Quest-list wide scroll arrows.
    /// </summary>
    private bool TryCreateVerticalArrows(
        Transform confirm,
        Transform hudParent,
        Vector3 upLocal,
        Vector3 downLocal)
    {
        // 1) SimpleShopMenu true up/down animators (when present in scene).
        foreach (SimpleShopMenu menu in Resources.FindObjectsOfTypeAll<SimpleShopMenu>())
        {
            if (menu == null || !menu.gameObject.scene.IsValid())
            {
                continue;
            }

            var u = SimpleUpArrowField?.GetValue(menu) as Component;
            var d = SimpleDownArrowField?.GetValue(menu) as Component;
            if (u == null || d == null || IsWideArrow(u.gameObject) || IsWideArrow(d.gameObject))
            {
                continue;
            }

            _hudArrowUp = CloneArrow(u.gameObject, _hudRoot!.transform, "ArrowUp", upLocal, 0f, 0.85f);
            _hudArrowDown = CloneArrow(d.gameObject, _hudRoot.transform, "ArrowDown", downLocal, 0f, 0.85f);
            MerchantStackerPlugin.Log.LogInfo("Arrows from SimpleShopMenu up/down");
            return true;
        }

        // 2) Yes/No Pointer L — small tk2d carets; rotate into vertical.
        Transform? pointer = FindNamedTransform(confirm, "Pointer L")
            ?? FindNamedTransform(hudParent, "Pointer L");
        if (pointer != null)
        {
            // Pointer L faces left; ±90° makes up/down.
            _hudArrowUp = CloneArrow(pointer.gameObject, _hudRoot!.transform, "ArrowUp", upLocal, 90f, 0.75f);
            _hudArrowDown = CloneArrow(pointer.gameObject, _hudRoot.transform, "ArrowDown", downLocal, -90f, 0.75f);
            MerchantStackerPlugin.Log.LogInfo("Arrows from Confirm Pointer L (rotated vertical)");
            return true;
        }

        // 3) Compact InvAnimateUpAndDown only (reject wide save/quest pan arrows).
        InvAnimateUpAndDown? smallUp = null;
        InvAnimateUpAndDown? smallDown = null;
        foreach (InvAnimateUpAndDown arrow in Resources.FindObjectsOfTypeAll<InvAnimateUpAndDown>())
        {
            if (arrow == null || !arrow.gameObject.scene.IsValid() || IsWideArrow(arrow.gameObject))
            {
                continue;
            }

            string path = PathOf(arrow.transform);
            if (ContainsIgnore(path, "Quest") || ContainsIgnore(path, "Map") || ContainsIgnore(path, "Pan"))
            {
                continue;
            }

            string n = arrow.gameObject.name;
            if (ContainsIgnore(n, "up") || n.EndsWith("U", StringComparison.Ordinal))
            {
                smallUp ??= arrow;
            }
            else if (ContainsIgnore(n, "down") || n.EndsWith("D", StringComparison.Ordinal))
            {
                smallDown ??= arrow;
            }
        }

        if (smallUp != null)
        {
            GameObject downSrc = smallDown != null ? smallDown.gameObject : smallUp.gameObject;
            _hudArrowUp = CloneArrow(smallUp.gameObject, _hudRoot!.transform, "ArrowUp", upLocal, 0f, 0.85f);
            _hudArrowDown = CloneArrow(
                downSrc, _hudRoot.transform, "ArrowDown", downLocal,
                ReferenceEquals(downSrc, smallUp.gameObject) ? 180f : 0f,
                0.85f);
            MerchantStackerPlugin.Log.LogInfo($"Arrows from compact InvAnimate '{smallUp.name}'");
            return true;
        }

        return false;
    }

    /// <summary>Quest/save pan arrows are very wide; menu/slider carets are compact.</summary>
    private static bool IsWideArrow(GameObject go)
    {
        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null && mr.bounds.size.x > 1.6f)
        {
            return true;
        }

        var sr = go.GetComponent<SpriteRenderer>();
        if (sr != null && sr.bounds.size.x > 1.6f)
        {
            return true;
        }

        return false;
    }

    private static GameObject CloneArrow(
        GameObject template,
        Transform parent,
        string name,
        Vector3 localPos,
        float rotationZ,
        float scale)
    {
        var go = UnityEngine.Object.Instantiate(template, parent);
        go.name = name;
        go.SetActive(true);
        go.transform.localPosition = localPos;
        go.transform.localRotation = Quaternion.Euler(0f, 0f, rotationZ);
        go.transform.localScale = Vector3.one * scale;

        go.GetComponent<InvAnimateUpAndDown>()?.Show();

        foreach (MeshRenderer mr in go.GetComponentsInChildren<MeshRenderer>(true))
        {
            mr.enabled = true;
            mr.sortingOrder = Math.Max(mr.sortingOrder, 260);
        }

        foreach (SpriteRenderer sr in go.GetComponentsInChildren<SpriteRenderer>(true))
        {
            sr.enabled = true;
            sr.sortingOrder = Math.Max(sr.sortingOrder, 260);
        }

        return go;
    }

    /// <summary>Disable Yes/No list and selection cursors so they don't sit on the qty digits.</summary>
    private void HideConfirmChrome(Transform confirm, Transform? confirmGroup)
    {
        Transform? uiList = confirm.Find("UI List");
        if (uiList != null && uiList.gameObject.activeSelf)
        {
            _hiddenConfirmList = uiList.gameObject;
            _hiddenConfirmList.SetActive(false);
        }

        Transform? msg = FindNamedTransform(confirm, "Confirm msg");
        if (msg != null && msg.gameObject.activeSelf)
        {
            _hiddenConfirmMsg = msg.gameObject;
            _hiddenConfirmMsg.SetActive(false);
        }

        Transform searchRoot = confirmGroup != null ? confirmGroup : confirm;
        foreach (Transform t in searchRoot.GetComponentsInChildren<Transform>(true))
        {
            if (t == null)
            {
                continue;
            }

            string n = t.name;
            if (!ContainsIgnore(n, "cursor") && !ContainsIgnore(n, "selector"))
            {
                continue;
            }

            if (t.gameObject.activeSelf)
            {
                t.gameObject.SetActive(false);
            }
        }
    }

    private static void DumpConfirmGroup(Transform hudParent)
    {
        var lines = new List<string> { $"Confirm dump '{PathOf(hudParent)}':" };
        foreach (Transform t in hudParent.GetComponentsInChildren<Transform>(true))
        {
            if (t == null || t == hudParent)
            {
                continue;
            }

            // Only direct-ish useful nodes: sprites + TMP.
            var tmp = t.GetComponent<TextMeshPro>();
            var sr = t.GetComponent<SpriteRenderer>();
            var mr = t.GetComponent<MeshRenderer>();
            if (tmp == null && sr == null && mr == null)
            {
                continue;
            }

            string detail = tmp != null
                ? $"TMP '{tmp.text}' size={tmp.fontSize:0.#}"
                : sr != null
                    ? $"SR '{sr.sprite?.name}' bounds={sr.bounds.size}"
                    : "Mesh";
            Vector3 lp = t.localPosition;
            lines.Add($"  {PathOf(t)} local=({lp.x:0.##},{lp.y:0.##},{lp.z:0.##}) active={t.gameObject.activeSelf} {detail}");
        }

        MerchantStackerPlugin.Log.LogInfo(string.Join("\n", lines));
    }

    private static bool ContainsIgnore(string haystack, string needle) =>
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private System.Collections.IEnumerator EnsureHudSoon()
    {
        for (int i = 0; i < 5; i++)
        {
            yield return null;
            if (!_active || !_inShop)
            {
                yield break;
            }

            if (_hudRoot == null)
            {
                BuildConfirmQtyHud();
            }

            RefreshInShopHud();
        }
    }

    private void RefreshInShopHud()
    {
        int total = _unitCost * _quantity;
        string qtyLine = _quantity.ToString(CultureInfo.InvariantCulture);
        string costLine = total.ToString(CultureInfo.InvariantCulture);

        ApplyTmp(_hudQty, qtyLine);
        ApplyTmp(_hudCost, costLine);
        SetArrowVisible(_hudArrowUp, _quantity < _max);
        SetArrowVisible(_hudArrowDown, _quantity > _min);

        if (_hudCurrencyIcon != null && _shopStats != null)
        {
            Sprite? currency = _shopStats.GetCurrencySprite();
            if (currency != null)
            {
                _hudCurrencyIcon.sprite = currency;
            }

            _hudCurrencyIcon.enabled = true;
            _hudCurrencyIcon.gameObject.SetActive(true);
        }
    }

    private static void SetArrowVisible(GameObject? arrow, bool fullyVisible)
    {
        if (arrow == null)
        {
            return;
        }

        // Don't tint tk2d atlas materials (no _Color) — shrink slightly when at range end.
        float baseScale = 0.75f;
        arrow.transform.localScale = Vector3.one * (fullyVisible ? baseScale : baseScale * 0.55f);

        foreach (MeshRenderer mr in arrow.GetComponentsInChildren<MeshRenderer>(true))
        {
            mr.enabled = true;
        }

        foreach (SpriteRenderer sr in arrow.GetComponentsInChildren<SpriteRenderer>(true))
        {
            sr.enabled = true;
        }

        arrow.GetComponent<InvAnimateUpAndDown>()?.Show();
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

    private static void SetTmpAlpha(TextMeshPro? tmp, float alpha)
    {
        if (tmp == null)
        {
            return;
        }

        Color c = tmp.color;
        c.a = alpha;
        tmp.color = c;
    }

    private static TextMeshPro CloneTmp(
        TextMeshPro template,
        Transform parent,
        string name,
        Vector3 localPos,
        string text,
        float fontSize,
        TextAlignmentOptions alignment = TextAlignmentOptions.Center)
    {
        var go = UnityEngine.Object.Instantiate(template.gameObject, parent);
        go.name = name;
        go.SetActive(true);
        go.transform.localPosition = localPos;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        // Drop any chrome that came along with Yes/Text (frames, fades, etc.).
        for (int i = go.transform.childCount - 1; i >= 0; i--)
        {
            UnityEngine.Object.Destroy(go.transform.GetChild(i).gameObject);
        }

        var tmp = go.GetComponent<TextMeshPro>();
        tmp.enabled = true;
        tmp.fontSize = fontSize;
        tmp.alignment = alignment;
        tmp.enableWordWrapping = false;
        Color c = template.color;
        c.a = 1f;
        tmp.color = c;
        tmp.SetText(text);
        tmp.ForceMeshUpdate(true);

        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.enabled = true;
            mr.sortingOrder = Math.Max(mr.sortingOrder, 250);
        }

        return tmp;
    }

    private static SpriteRenderer CloneSprite(
        SpriteRenderer template,
        Transform parent,
        string name,
        Vector3 localPos,
        float scaleMul)
    {
        var go = UnityEngine.Object.Instantiate(template.gameObject, parent);
        go.name = name;
        go.SetActive(true);
        go.transform.localPosition = localPos;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = template.transform.localScale * scaleMul;

        var sr = go.GetComponent<SpriteRenderer>();
        sr.enabled = true;
        Color c = sr.color;
        c.a = 1f;
        sr.color = c;
        sr.sortingOrder = Math.Max(sr.sortingOrder, 250);
        return sr;
    }

    private static Transform? FindNamedTransform(Transform root, string name)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t != null && t.name == name)
            {
                return t;
            }
        }

        return null;
    }

    private static TextMeshPro? FindTemplateTmp(Transform confirm)
    {
        // Prefer Yes label font (size ~10 in confirm UI); fall back to any TMP under confirm.
        foreach (TextMeshPro tmp in confirm.GetComponentsInChildren<TextMeshPro>(true))
        {
            if (tmp != null && tmp.gameObject.name == "Text"
                && tmp.transform.parent != null
                && tmp.transform.parent.name == "Yes")
            {
                return tmp;
            }
        }

        foreach (TextMeshPro tmp in confirm.GetComponentsInChildren<TextMeshPro>(true))
        {
            if (tmp != null)
            {
                return tmp;
            }
        }

        return null;
    }

    private void DestroyInShopHud(bool restoreTexts)
    {
        if (restoreTexts)
        {
            if (_boundStock != null && _originalShopTitle != null)
            {
                _boundStock.Title = _originalShopTitle;
            }

            if (_boundCostText != null && _originalCostText != null)
            {
                ApplyTmp(_boundCostText, _originalCostText);
            }

            if (_costIsNative && _hudCost != null && _nativeMoneyCostOriginal != null)
            {
                ApplyTmp(_hudCost, _nativeMoneyCostOriginal);
            }
        }

        if (_hiddenConfirmList != null)
        {
            _hiddenConfirmList.SetActive(true);
            _hiddenConfirmList = null;
        }

        if (_hiddenConfirmMsg != null)
        {
            _hiddenConfirmMsg.SetActive(true);
            _hiddenConfirmMsg = null;
        }

        foreach (GameObject go in _hiddenNativeCost)
        {
            if (go != null)
            {
                go.SetActive(true);
            }
        }

        _hiddenNativeCost.Clear();

        if (_hudRoot != null)
        {
            UnityEngine.Object.Destroy(_hudRoot);
        }

        _hudRoot = null;
        _hudQty = null;
        _hudArrowUp = null;
        _hudArrowDown = null;
        _hudCost = null;
        _hudCurrencyIcon = null;
        _costIsNative = false;
        _nativeMoneyCostOriginal = null;

        if (restoreTexts)
        {
            _boundStock = null;
            _originalShopTitle = null;
            _boundCostText = null;
            _originalCostText = null;
        }
    }

    private static string PathOf(Transform t)
    {
        string path = t.name;
        Transform? p = t.parent;
        int guard = 0;
        while (p != null && guard++ < 6)
        {
            path = p.name + "/" + path;
            p = p.parent;
        }

        return path;
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
