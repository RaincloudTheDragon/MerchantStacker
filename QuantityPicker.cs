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
    internal static QuantityPicker? Instance { get; private set; }

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
    private GameObject? _hiddenConfirmList;
    private GameObject? _hiddenConfirmMsg;
    private readonly List<GameObject> _hiddenNativeCost = new();
    private Action? _originalYes;
    private Action? _originalNo;
    private Action<int>? _onConfirm;
    private Action? _onCancel;
    private Action? _purchaseDone;
    private Action? _shopCancel;

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

    /// <summary>
    /// ScriptEngine / plugin unload: close any open session and drop the static instance.
    /// </summary>
    internal void ShutdownForReload()
    {
        SuppressHijack = false;
        try
        {
            if (_active)
            {
                if (_inShop)
                {
                    AbortInShopSession(resetShopWindow: true);
                }
                else
                {
                    Finish(confirmed: false);
                }
            }
            else
            {
                DestroyInShopHud(restoreTexts: true);
            }
        }
        catch (Exception ex)
        {
            MerchantStackerPlugin.Log.LogWarning($"QuantityPicker shutdown: {ex.Message}");
        }

        if (Instance == this)
        {
            Instance = null;
        }
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
        if (!MerchantStackerPlugin.Enabled.Value || maxQuantity < 1 || _active || stats?.Item == null)
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
        if (_hudRoot == null)
        {
            // Never leave IsOpen with no HUD — that PreHides Yes/No forever.
            MerchantStackerPlugin.Log.LogWarning("OpenInShop: HUD failed — restoring vanilla confirm");
            _active = false;
            _inShop = false;
            _shopRoot = null;
            _shopStats = null;
            InventoryPaneInput.IsInputBlocked = false;
            Patches.ShopConfirmListPatches.RestoreConfirmChromePublic(shopRoot.root);
            return;
        }

        RefreshInShopHud();
        StartCoroutine(EnsureHudSoon());
        MerchantStackerPlugin.Log.LogInfo(
            $"In-shop qty: {_title} max={_max} cost={_unitCost} hud=True");
    }

    /// <summary>Wire shop FSM wait/complete + cancel (reset window, no extra purchase).</summary>
    public void ArmShopPurchaseSession(Action? onPurchaseComplete, Action? onCancelPurchase)
    {
        _purchaseDone = onPurchaseComplete;
        _shopCancel = onCancelPurchase;
    }

    private void LateUpdate()
    {
        if (!_active || !_inShop)
        {
            return;
        }

        // FSM "Activate Yes No" re-enables chrome after we open — keep it dead every frame.
        SuppressInShopConfirmChrome();
        // Cancel/re-enter can leave Item Sprite inactive or renderer disabled — keep art up.
        EnsureConfirmItemArtVisible();

        // Only abort when the whole shop menu is gone.
        // Do NOT check ShopItemStats.activeInHierarchy — list rows are often disabled
        // while Item Confirm Group is up (that was aborting qty every frame).
        Transform? root = _shopRoot != null ? _shopRoot : _shopStats != null ? _shopStats.transform.root : null;
        if (root == null || !root || !root.gameObject.activeInHierarchy)
        {
            AbortInShopSession(resetShopWindow: true);
        }
    }

    /// <summary>Keep vanilla Yes/No/Costs off while qty replaces confirm.</summary>
    private void SuppressInShopConfirmChrome()
    {
        Transform? root = GetShopMenuRoot();
        if (root == null)
        {
            return;
        }

        Patches.ShopConfirmListPatches.SuppressConfirmChromePublic(root);
    }

    private Transform? GetShopMenuRoot()
    {
        try
        {
            if (_shopStats != null)
            {
                return _shopStats.transform.root;
            }

            if (_shopRoot != null)
            {
                return _shopRoot.root;
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    /// <summary>
    /// Keep confirm item name + logo visible. After cancel, FSM may leave Item Sprite off.
    /// </summary>
    private void EnsureConfirmItemArtVisible()
    {
        Transform? root = GetShopMenuRoot();
        if (root == null)
        {
            return;
        }

        Transform? group = FindNamedTransform(root, "Item Confirm Group");
        if (group == null)
        {
            return;
        }

        if (!group.gameObject.activeSelf)
        {
            group.gameObject.SetActive(true);
        }

        foreach (Transform t in group.GetComponentsInChildren<Transform>(true))
        {
            if (t == null || (t.name != "Item Sprite" && t.name != "Item name"))
            {
                continue;
            }

            // Only the confirm hero art (direct under Item Confirm Group), not Costs/Item Sprite.
            if (t.parent != group)
            {
                continue;
            }

            if (!t.gameObject.activeSelf)
            {
                t.gameObject.SetActive(true);
            }

            var sr = t.GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                sr.enabled = true;
                Color c = sr.color;
                if (c.a < 1f)
                {
                    c.a = 1f;
                    sr.color = c;
                }
            }

            var tmp = t.GetComponent<TextMeshPro>();
            if (tmp != null)
            {
                var mr = tmp.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    mr.enabled = true;
                }
            }
        }
    }

    /// <summary>Force-close qty without purchasing (shop closed or lost).</summary>
    private void AbortInShopSession(bool resetShopWindow)
    {
        if (!_active)
        {
            return;
        }

        MerchantStackerPlugin.Log.LogInfo("In-shop qty aborted (shop closed/disabled)");
        PurchaseBatcher.ClearPendingQuantity();
        PurchaseBatcher.ExpectingFsmPurchase = false;
        PurchaseBatcher.ClearShopPurchaseSuppression();
        var cancel = _shopCancel;
        _purchaseDone = null;
        _shopCancel = null;
        _active = false;
        _hijacked = false;
        _inShop = false;
        _shopRoot = null;
        _shopStats = null;
        DestroyInShopHud(restoreTexts: true);
        ResetHoldState();
        InventoryPaneInput.IsInputBlocked = false;

        // Clear FSM wait + reset confirm UI so we don't leave a blank pane + stuck cost.
        if (resetShopWindow)
        {
            cancel?.Invoke();
        }
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
        if (!MerchantStackerPlugin.Enabled.Value || maxQuantity < 1 || _active)
        {
            return;
        }

        BeginSession(title, item, unitCost, currency, maxQuantity, hijacked: true);
        _inShop = false;
        _originalYes = originalYes;
        _originalNo = originalNo;
        _onConfirm = null;
        _onCancel = null;
        _purchaseDone = null;
        _shopCancel = null;

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
        _purchaseDone = null;
        _shopCancel = null;

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
        // Keep ArmShopPurchaseSession callbacks — OpenInShop is called after Arm.
        _active = true;
        // PendingQuantity is set only on Finish(confirmed) — not here (blocked next open).
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

            Transform layoutParent = confirmGroup != null ? confirmGroup : confirm;
            DumpConfirmGroup(layoutParent);
            EnsureConfirmItemArtVisible();
            HideConfirmChrome(confirm, confirmGroup);
            HideNativeConfirmCosts(confirm);

            // Qty/cost stay on DontDestroyOnLoad (avoids stuck TMP overlays).
            _hudRoot = new GameObject("MerchantStacker_QtyHud");
            _hudRoot.layer = layoutParent.gameObject.layer;
            _hudRoot.transform.SetParent(transform, false);
            _hudRoot.transform.localPosition = Vector3.zero;
            _hudRoot.transform.localRotation = Quaternion.identity;
            _hudRoot.transform.localScale = Vector3.one;

            GetQtyColumnWorld(layoutParent, out Vector3 qtyWorld, out _, out _);
            GetQtyColumnLocal(layoutParent, out Vector3 qtyLocal, out Vector3 upLocal, out Vector3 downLocal);
            Vector3 currencyWorld = qtyWorld + layoutParent.TransformVector(new Vector3(1.5f, 0f, 0f));
            Vector3 costWorld = qtyWorld + layoutParent.TransformVector(new Vector3(2.4f, 0f, 0f));

            TextMeshPro? costTemplate = FindConfirmCostTmp(confirm) ?? template;
            float fs = Math.Max(5f, costTemplate.fontSize);

            _hudQty = CloneTmpWorld(template, _hudRoot.transform, "Qty", qtyWorld, "1", fs * 1.35f);
            _hudCost = CloneTmpWorld(
                costTemplate, _hudRoot.transform, "TotalCost", costWorld, "0", fs,
                TextAlignmentOptions.Left);

            var srcCostIcon = CostSpriteField?.GetValue(_shopStats) as SpriteRenderer;
            if (srcCostIcon != null)
            {
                _hudCurrencyIcon = CloneSpriteWorld(
                    srcCostIcon, _hudRoot.transform, "CurrencyIcon", currencyWorld, scaleMul: 1f);
                Sprite? currency = _shopStats.GetCurrencySprite();
                if (currency != null)
                {
                    _hudCurrencyIcon.sprite = currency;
                }
            }

            // tk2d Pointers only render in confirm local space (db1c770) — not world/DDOL.
            if (TryCreateVerticalArrowsLocal(confirm, layoutParent, upLocal, downLocal))
            {
                MerchantStackerPlugin.Log.LogInfo("Qty HUD arrows: local (pointer/small)");
            }
            else
            {
                MerchantStackerPlugin.Log.LogWarning("Qty HUD: no small arrow templates found");
            }

            MerchantStackerPlugin.Log.LogInfo(
                $"Qty HUD world qty={qtyWorld} local={qtyLocal} arrows={(_hudArrowUp != null)} currency={(_hudCurrencyIcon != null)}");
        }
        catch (Exception ex)
        {
            MerchantStackerPlugin.Log.LogError($"BuildConfirmQtyHud: {ex}");
        }
    }

    /// <summary>Hide Confirm/Costs so vanilla unit-price TMP is never rewritten to a bulk total.</summary>
    private void HideNativeConfirmCosts(Transform confirm)
    {
        _hiddenNativeCost.Clear();
        foreach (Transform t in confirm.GetComponentsInChildren<Transform>(true))
        {
            if (t == null || t.name != "Costs")
            {
                continue;
            }

            if (t.gameObject.activeSelf)
            {
                t.gameObject.SetActive(false);
                _hiddenNativeCost.Add(t.gameObject);
            }

            break;
        }
    }

    private static TextMeshPro? FindConfirmCostTmp(Transform confirm)
    {
        foreach (TextMeshPro tmp in confirm.GetComponentsInChildren<TextMeshPro>(true))
        {
            if (tmp != null && tmp.gameObject.name == "Item cost")
            {
                return tmp;
            }
        }

        return null;
    }

    /// <summary>World-space qty column just right of the confirm Item Sprite.</summary>
    private static void GetQtyColumnWorld(
        Transform layoutParent,
        out Vector3 qtyWorld,
        out Vector3 upWorld,
        out Vector3 downWorld)
    {
        GetQtyColumnLocal(layoutParent, out Vector3 qtyLocal, out Vector3 upLocal, out Vector3 downLocal);
        qtyWorld = layoutParent.TransformPoint(qtyLocal);
        upWorld = layoutParent.TransformPoint(upLocal);
        downWorld = layoutParent.TransformPoint(downLocal);
    }

    /// <summary>Confirm-group local qty column (same plane as Item Sprite, z≈-3).</summary>
    private static void GetQtyColumnLocal(
        Transform layoutParent,
        out Vector3 qtyLocal,
        out Vector3 upLocal,
        out Vector3 downLocal)
    {
        Transform? item = FindNamedTransform(layoutParent, "Item Sprite");
        Vector3 itemLp = item != null ? item.localPosition : new Vector3(-3.4f, 1.5f, -3f);
        // Prefer Item Sprite under Item Confirm Group (not nested Costs sprites).
        if (item != null && item.parent != layoutParent)
        {
            foreach (Transform t in layoutParent.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.name == "Item Sprite" && t.parent == layoutParent)
                {
                    itemLp = t.localPosition;
                    break;
                }
            }
        }

        float z = itemLp.z;
        qtyLocal = new Vector3(itemLp.x + 2.55f, itemLp.y, z);
        upLocal = new Vector3(qtyLocal.x, qtyLocal.y + 1.05f, z);
        downLocal = new Vector3(qtyLocal.x, qtyLocal.y - 1.05f, z);
    }

    /// <summary>
    /// Pointer carets in confirm local space (db1c770). World/DDOL clones stay invisible.
    /// </summary>
    private bool TryCreateVerticalArrowsLocal(
        Transform confirm,
        Transform layoutParent,
        Vector3 upLocal,
        Vector3 downLocal)
    {
        Transform? pointer = FindNamedTransform(confirm, "Pointer L")
            ?? FindNamedTransform(layoutParent, "Pointer L");
        if (pointer != null)
        {
            _hudArrowUp = CloneArrowLocal(pointer.gameObject, layoutParent, "ArrowUp", upLocal, 90f, 0.75f);
            _hudArrowDown = CloneArrowLocal(pointer.gameObject, layoutParent, "ArrowDown", downLocal, -90f, 0.75f);
            MerchantStackerPlugin.Log.LogInfo("Arrows from Confirm Pointer L (local vertical)");
            return true;
        }

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

            _hudArrowUp = CloneArrowLocal(u.gameObject, layoutParent, "ArrowUp", upLocal, 0f, 0.85f);
            _hudArrowDown = CloneArrowLocal(d.gameObject, layoutParent, "ArrowDown", downLocal, 0f, 0.85f);
            MerchantStackerPlugin.Log.LogInfo("Arrows from SimpleShopMenu up/down (local)");
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

    private static GameObject CloneArrowLocal(
        GameObject template,
        Transform layoutParent,
        string name,
        Vector3 localPos,
        float rotationZ,
        float scale)
    {
        var go = UnityEngine.Object.Instantiate(template, layoutParent);
        go.name = name;
        go.layer = layoutParent.gameObject.layer;
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
        Transform searchRoot = confirmGroup != null ? confirmGroup : confirm;
        Patches.ShopConfirmListPatches.SuppressConfirmChromePublic(searchRoot);

        Transform? uiList = confirm.Find("UI List");
        if (uiList != null)
        {
            _hiddenConfirmList = uiList.gameObject;
            if (uiList.gameObject.activeSelf)
            {
                uiList.gameObject.SetActive(false);
            }
        }

        Transform? msg = FindNamedTransform(confirm, "Confirm msg");
        if (msg != null)
        {
            _hiddenConfirmMsg = msg.gameObject;
            if (msg.gameObject.activeSelf)
            {
                msg.gameObject.SetActive(false);
            }
        }

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

    private static TextMeshPro CloneTmpWorld(
        TextMeshPro template,
        Transform parent,
        string name,
        Vector3 worldPos,
        string text,
        float fontSize,
        TextAlignmentOptions alignment = TextAlignmentOptions.Center)
    {
        var go = UnityEngine.Object.Instantiate(template.gameObject, parent);
        go.name = name;
        go.SetActive(true);
        go.transform.position = worldPos;
        go.transform.rotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

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

    private static SpriteRenderer CloneSpriteWorld(
        SpriteRenderer template,
        Transform parent,
        string name,
        Vector3 worldPos,
        float scaleMul)
    {
        var go = UnityEngine.Object.Instantiate(template.gameObject, parent);
        go.name = name;
        go.SetActive(true);
        go.transform.position = worldPos;
        go.transform.rotation = Quaternion.identity;
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

    /// <summary>
    /// On cancel: restore Yes/No activeSelf, then hide the whole confirm group.
    /// Leaving Yes/No inactive under the group caused a blank softlock on re-enter.
    /// </summary>
    private static void ForceHideConfirmChromeOnly(ShopItemStats? stats)
    {
        Transform? root = null;
        try
        {
            if (stats != null)
            {
                root = stats.transform.root;
            }
        }
        catch
        {
            root = null;
        }

        if (root == null)
        {
            foreach (ShopMenuStock stock in UnityEngine.Object.FindObjectsByType<ShopMenuStock>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (stock == null || !stock.gameObject.scene.IsValid())
                {
                    continue;
                }

                root = stock.transform.root;
                break;
            }
        }

        if (root == null)
        {
            return;
        }

        // Children keep activeSelf across parent toggle — re-enable before hiding group.
        Patches.ShopConfirmListPatches.RestoreConfirmChromePublic(root);
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t != null && t.name == "Item Confirm Group" && t.gameObject.activeSelf)
            {
                t.gameObject.SetActive(false);
            }
        }
    }

    private void DestroyInShopHud(bool restoreTexts, bool restoreConfirmChrome = true)
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
        }

        // Wipe our TMP meshes before destroy so world-space digits can't linger.
        ClearTmpVisual(_hudQty);
        ClearTmpVisual(_hudCost);

        // Arrows live under Item Confirm Group (not _hudRoot).
        if (_hudArrowUp != null)
        {
            UnityEngine.Object.DestroyImmediate(_hudArrowUp);
        }

        if (_hudArrowDown != null)
        {
            UnityEngine.Object.DestroyImmediate(_hudArrowDown);
        }

        if (_hudRoot != null)
        {
            UnityEngine.Object.DestroyImmediate(_hudRoot);
        }

        _hudRoot = null;
        _hudQty = null;
        _hudArrowUp = null;
        _hudArrowDown = null;
        _hudCost = null;
        _hudCurrencyIcon = null;
        ScrubLeftoverQtyHud();

        if (restoreConfirmChrome)
        {
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
                if (go == null)
                {
                    continue;
                }

                // Ensure native unit price is restored (never leave a bulk total on Item cost).
                foreach (TextMeshPro tmp in go.GetComponentsInChildren<TextMeshPro>(true))
                {
                    if (tmp != null && tmp.gameObject.name == "Item cost" && _unitCost > 0)
                    {
                        ApplyTmp(tmp, _unitCost.ToString(CultureInfo.InvariantCulture));
                    }
                }

                go.SetActive(true);
            }

            _hiddenNativeCost.Clear();
        }
        else
        {
            // Success path: chrome stays hidden for the FSM thank-you transition.
            _hiddenConfirmList = null;
            _hiddenConfirmMsg = null;
            _hiddenNativeCost.Clear();
        }

        if (restoreTexts)
        {
            _boundStock = null;
            _originalShopTitle = null;
            _boundCostText = null;
            _originalCostText = null;
        }
    }

    private static void ClearTmpVisual(TextMeshPro? tmp)
    {
        if (tmp == null)
        {
            return;
        }

        try
        {
            tmp.SetText(string.Empty);
            tmp.ForceMeshUpdate(true);
            var mr = tmp.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.enabled = false;
            }
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>Destroy any leftover qty HUD objects and disable stray TotalCost meshes.</summary>
    private static void ScrubLeftoverQtyHud()
    {
        foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (go == null)
            {
                continue;
            }

            // DontDestroyOnLoad HUD may report an invalid scene — still scrub by name/parent.
            bool sceneOk;
            try
            {
                sceneOk = go.scene.IsValid() || go.hideFlags != HideFlags.None;
            }
            catch
            {
                continue;
            }

            if (!sceneOk && go.name != "MerchantStacker_QtyHud")
            {
                continue;
            }

            if (go.name == "MerchantStacker_QtyHud"
                || go.name == "TotalCost"
                || go.name == "Qty"
                || go.name == "ArrowUp"
                || go.name == "ArrowDown"
                || go.name == "CurrencyIcon")
            {
                bool ours = go.name == "MerchantStacker_QtyHud"
                    || (go.transform.parent != null
                        && (go.transform.parent.name == "MerchantStacker_QtyHud"
                            || go.transform.parent.name == "MerchantStacker_QuantityPicker"
                            || ((go.name == "ArrowUp" || go.name == "ArrowDown")
                                && go.transform.parent.name == "Item Confirm Group")));
                if (!ours)
                {
                    continue;
                }

                foreach (TextMeshPro tmp in go.GetComponentsInChildren<TextMeshPro>(true))
                {
                    ClearTmpVisual(tmp);
                }

                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }

    /// <summary>Nuke any leftover world-space clones parented to the picker.</summary>
    private void ClearPickerHudChildren()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform child = transform.GetChild(i);
            if (child == null)
            {
                continue;
            }

            foreach (TextMeshPro tmp in child.GetComponentsInChildren<TextMeshPro>(true))
            {
                ClearTmpVisual(tmp);
            }

            UnityEngine.Object.DestroyImmediate(child.gameObject);
        }

        _hudRoot = null;
        _hudQty = null;
        _hudArrowUp = null;
        _hudArrowDown = null;
        _hudCost = null;
        _hudCurrencyIcon = null;
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

    /// <summary>
    /// Buy, show get-item popup, then ResetShopWindow.
    /// Do not leave the shop FSM on a half-hidden confirm (that softlocked A/B).
    /// </summary>
    private static void CompleteInShopPurchase(ShopItemStats shopStats, int qty, Action? purchaseDone)
    {
        var stock = shopStats.GetComponentInParent<ShopMenuStock>();
        PurchaseBatcher.ClearPendingQuantity();
        PurchaseBatcher.ExpectingFsmPurchase = false;

        PurchaseBatcher.BuyShopItem(
            shopStats,
            qty,
            subItemIndex: 0,
            onComplete: () =>
            {
                stock?.SetWasItemPurchased(true);
                ShowPurchaseFeedback(shopStats);
                ScrubLeftoverQtyHud();
                ForceHideConfirmChromeOnly(shopStats);
                // Clear any SetShopItemPurchased wait, then hard-reset to the item list.
                purchaseDone?.Invoke();
                EventRegister.SendEvent(EventRegisterEvents.ResetShopWindow);
                // BlockShopPurchases was set during Buy — must clear or next confirm softlocks.
                PurchaseBatcher.EndShopPurchaseBlock();
                Patches.ShopConfirmListPatches.ClearPendingQtyOpen();
                GameCameras.instance?.HUDIn();
                MerchantStackerPlugin.Log.LogInfo("Bulk buy done → popup + ResetShopWindow");
            });
    }

    /// <summary>Spawn the collectable get popup / hero reaction (shop SetPurchased uses showPopup:false).</summary>
    internal static void ShowPurchaseFeedback(ShopItemStats? stats)
    {
        try
        {
            if (stats?.Item?.Item is not CollectableItem item)
            {
                return;
            }

            CollectableUIMsg.Spawn(item);
            CollectableItemHeroReaction.DoReaction();
        }
        catch (Exception ex)
        {
            MerchantStackerPlugin.Log.LogWarning($"ShowPurchaseFeedback: {ex.Message}");
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
        var shopCancel = _shopCancel;

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
        _shopCancel = null;
        _item = null;

        // Cancel: scrub our HUD and force-hide confirm chrome. Do not re-enable Yes/No/Costs
        // (that left unit-price overlays). ResetShopWindow rebuilds a clean item list.
        DestroyInShopHud(restoreTexts: true, restoreConfirmChrome: false);
        ForceHideConfirmChromeOnly(shopStats);
        ClearPickerHudChildren();
        ResetHoldState();
        InventoryPaneInput.IsInputBlocked = false;
        Patches.ShopConfirmListPatches.ClearPendingQtyOpen();

        if (confirmed)
        {
            PurchaseBatcher.PendingQuantity = qty;
            MerchantStackerPlugin.Log.LogInfo($"Qty confirmed: {qty}");
        }
        else
        {
            PurchaseBatcher.ClearPendingQuantity();
            PurchaseBatcher.ExpectingFsmPurchase = false;
            PurchaseBatcher.EndShopPurchaseBlock();
        }

        if (inShop)
        {
            if (confirmed && shopStats != null)
            {
                CompleteInShopPurchase(shopStats, qty, purchaseDone);
            }
            else
            {
                ScrubLeftoverQtyHud();
                ClearPickerHudChildren();
                ForceHideConfirmChromeOnly(shopStats);
                ShopSelectionCache.Clear();
                Patches.ShopConfirmListPatches.ClearPendingQtyOpen();
                shopCancel?.Invoke();
                ScrubLeftoverQtyHud();
                ClearPickerHudChildren();
                MerchantStackerPlugin.Log.LogInfo("Qty cancelled → ResetShopWindow");
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
