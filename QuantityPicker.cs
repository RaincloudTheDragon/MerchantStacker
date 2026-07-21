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

    /// <summary>Cached menu caret mesh/material extracted once from a live Pointer/ScrollView arrow.</summary>
    private static Mesh? _cachedCaretMesh;
    private static Material? _cachedCaretMaterial;
    private static int _cachedCaretSortLayer;
    private static int _cachedCaretSortOrder;
    private static Sprite? _cachedArrowSprite;
    private static bool _arrowAssetsSearched;

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
    /// <summary>Cached Yes/No/Costs/etc. — LateUpdate only toggles these (no tree walk).</summary>
    private readonly List<GameObject> _chromeToKeepOff = new();
    private GameObject? _itemArtSprite;
    private GameObject? _itemArtName;
    private SpriteRenderer? _itemArtSpriteSr;
    private TextMeshPro? _itemArtNameTmp;
    private int _lastHudQty = int.MinValue;
    private int _lastHudTotal = int.MinValue;
    private bool _lastArrowUpOn;
    private bool _lastArrowDownOn;
    private float _maxRecalcTimer;
    private int _chromeKeepOffFrame;
    private MeshRenderer? _itemArtNameMr;
    private Vector3 _qtyLocal;
    private Vector3 _costLocal;
    private Vector3 _currencyLocal;
    private Vector3 _arrowUpLocal;
    private Vector3 _arrowDownLocal;
    private Vector3 _arrowUpBaseLocalScale = Vector3.one;
    private Vector3 _arrowDownBaseLocalScale = Vector3.one;
    private Quaternion _arrowUpLocalRot = Quaternion.identity;
    private Quaternion _arrowDownLocalRot = Quaternion.identity;
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

        // Art only — chrome is locked via ActivateGameObject prefix (no per-frame SetActive).
        if ((++_chromeKeepOffFrame & 15) == 0)
        {
            KeepCachedItemArtVisible();
        }

        Transform? root = _shopRoot != null ? _shopRoot : _shopStats != null ? _shopStats.transform.root : null;
        if (root == null || !root || !root.gameObject.activeInHierarchy)
        {
            AbortInShopSession(resetShopWindow: true);
        }
    }

    /// <summary>Unused — chrome lock is ActivateGameObject prefix; kept for one-shot hide list.</summary>
    private void KeepCachedChromeOff()
    {
        for (int i = 0; i < _chromeToKeepOff.Count; i++)
        {
            GameObject go = _chromeToKeepOff[i];
            if (go != null && go.activeSelf)
            {
                go.SetActive(false);
            }
        }
    }

    private void KeepCachedItemArtVisible()
    {
        if (_itemArtSprite != null && !_itemArtSprite.activeSelf)
        {
            _itemArtSprite.SetActive(true);
        }

        if (_itemArtSpriteSr != null && !_itemArtSpriteSr.enabled)
        {
            _itemArtSpriteSr.enabled = true;
        }

        if (_itemArtName != null && !_itemArtName.activeSelf)
        {
            _itemArtName.SetActive(true);
        }

        if (_itemArtNameTmp != null)
        {
            if (!_itemArtNameTmp.enabled)
            {
                _itemArtNameTmp.enabled = true;
            }

            if (_itemArtNameMr != null && !_itemArtNameMr.enabled)
            {
                _itemArtNameMr.enabled = true;
            }
        }
    }

    /// <summary>One-time scan when qty opens — hide list for Restore path.</summary>
    private void CacheChromeToKeepOff(Transform confirm, Transform? confirmGroup)
    {
        _chromeToKeepOff.Clear();
        Transform searchRoot = confirmGroup != null ? confirmGroup : confirm;
        foreach (Transform t in searchRoot.GetComponentsInChildren<Transform>(true))
        {
            if (t == null)
            {
                continue;
            }

            string n = t.name;
            bool confirmUiList = n == "UI List" && t.parent != null && t.parent.name == "Confirm";
            if (confirmUiList || n == "Confirm msg" || n == "Costs" || n == "Thankyou"
                || n == "Money" || n == "Item cost" || n == "Geo Sprite"
                || ((n == "Yes" || n == "No") && Patches.ShopConfirmListPatches.IsUnderShopConfirmPublic(t)))
            {
                _chromeToKeepOff.Add(t.gameObject);
            }
        }

        Patches.ShopConfirmShowPatches.LockConfirmChrome = true;
        KeepCachedChromeOff();
    }

    private void CacheItemArt(Transform layoutParent)
    {
        _itemArtSprite = null;
        _itemArtName = null;
        _itemArtSpriteSr = null;
        _itemArtNameTmp = null;
        foreach (Transform t in layoutParent)
        {
            if (t == null)
            {
                continue;
            }

            if (t.name == "Item Sprite")
            {
                _itemArtSprite = t.gameObject;
                _itemArtSpriteSr = t.GetComponent<SpriteRenderer>();
            }
            else if (t.name == "Item name")
            {
                _itemArtName = t.gameObject;
                _itemArtNameTmp = t.GetComponent<TextMeshPro>();
                _itemArtNameMr = _itemArtNameTmp != null
                    ? _itemArtNameTmp.GetComponent<MeshRenderer>()
                    : null;
            }
        }
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
    /// One-shot / open-path only — LateUpdate uses KeepCachedItemArtVisible.
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

        CacheItemArt(group);
        KeepCachedItemArtVisible();
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

        Patches.ShopConfirmShowPatches.BuildingQtyHud = true;
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
            EnsureConfirmItemArtVisible();

            GetQtyRowLocal(
                layoutParent,
                out _qtyLocal,
                out _arrowUpLocal,
                out _arrowDownLocal,
                out _currencyLocal,
                out _costLocal);

            _hudRoot = new GameObject("MerchantStacker_QtyHud");
            _hudRoot.layer = layoutParent.gameObject.layer;
            _hudRoot.transform.SetParent(layoutParent, false);
            _hudRoot.transform.localPosition = Vector3.zero;
            _hudRoot.transform.localRotation = Quaternion.identity;
            _hudRoot.transform.localScale = Vector3.one;

            TextMeshPro? costTemplate = FindConfirmCostTmp(confirm) ?? template;
            float fs = Math.Max(5f, costTemplate.fontSize);

            _hudQty = CloneTmpLocal(template, _hudRoot.transform, "Qty", _qtyLocal, "1", fs * 2.7f);
            _hudCost = CloneTmpLocal(
                costTemplate, _hudRoot.transform, "TotalCost", _costLocal, "0", fs * 2f,
                TextAlignmentOptions.Left);

            // Currency bead first — also used as visible up/down caret template.
            var srcCostIcon = CostSpriteField?.GetValue(_shopStats) as SpriteRenderer;
            if (srcCostIcon != null)
            {
                _hudCurrencyIcon = CloneSpriteLocal(
                    srcCostIcon, _hudRoot.transform, "CurrencyIcon", _currencyLocal, scaleMul: 2f);
                Sprite? currency = _shopStats.GetCurrencySprite();
                if (currency != null)
                {
                    _hudCurrencyIcon.sprite = currency;
                }
            }

            if (TryCreateVerticalArrowsLocal(confirm, layoutParent, _hudRoot.transform, _arrowUpLocal, _arrowDownLocal))
            {
                MerchantStackerPlugin.Log.LogInfo("Qty HUD arrows: ok");
            }
            else
            {
                MerchantStackerPlugin.Log.LogWarning("Qty HUD: no arrow templates found");
            }

            HideConfirmChrome(confirm, confirmGroup);
            HideNativeConfirmCosts(confirm);
            CacheChromeToKeepOff(confirm, confirmGroup);

            _lastHudQty = int.MinValue;
            _lastHudTotal = int.MinValue;
            _lastArrowUpOn = !(_quantity < _max);
            _lastArrowDownOn = !(_quantity > _min);
            MatchArrowDrawOrderToQty();
            RefreshInShopHud();
            ReassertHudPoses();
            ForceArrowRenderersOn();
            MerchantStackerPlugin.Log.LogInfo(
                $"Qty HUD row qty={_qtyLocal} cost={_costLocal} arrows={(_hudArrowUp != null)} "
                + $"arrowSize={MeshSize(_hudArrowUp)}");
        }
        catch (Exception ex)
        {
            MerchantStackerPlugin.Log.LogWarning($"Qty HUD build failed: {ex.Message}");
            DestroyInShopHud(restoreTexts: true, restoreConfirmChrome: true);
        }
        finally
        {
            Patches.ShopConfirmShowPatches.BuildingQtyHud = false;
        }
    }

    /// <summary>Hide Confirm/Costs so vanilla unit-price TMP is never rewritten to a bulk total.</summary>
    private void HideNativeConfirmCosts(Transform confirm)
    {
        _hiddenNativeCost.Clear();
        foreach (Transform t in confirm.GetComponentsInChildren<Transform>(true))
        {
            if (t == null)
            {
                continue;
            }

            string n = t.name;
            if (n != "Costs" && n != "Money" && n != "Item cost" && n != "Geo Sprite")
            {
                continue;
            }

            // Kill renderers too — SetActive alone can leave a visible flash / hitch fight.
            foreach (MeshRenderer mr in t.GetComponentsInChildren<MeshRenderer>(true))
            {
                mr.enabled = false;
            }

            foreach (SpriteRenderer sr in t.GetComponentsInChildren<SpriteRenderer>(true))
            {
                sr.enabled = false;
            }

            if (t.gameObject.activeSelf)
            {
                t.gameObject.SetActive(false);
            }

            _hiddenNativeCost.Add(t.gameObject);
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

    /// <summary>
    /// Low row under the art: [qty] then bead+total to its right.
    /// </summary>
    private static void GetQtyRowLocal(
        Transform layoutParent,
        out Vector3 qtyLocal,
        out Vector3 upLocal,
        out Vector3 downLocal,
        out Vector3 currencyLocal,
        out Vector3 costLocal)
    {
        Vector3 itemLp = new Vector3(-3.4f, 1.5f, -3f);
        foreach (Transform t in layoutParent)
        {
            if (t != null && t.name == "Item Sprite")
            {
                itemLp = t.localPosition;
                break;
            }
        }

        float z = itemLp.z;
        // Much lower than the art; qty left of bead/cost.
        float rowY = itemLp.y - 3.5f;
        float cx = itemLp.x;
        qtyLocal = new Vector3(cx - 0.9f, rowY, z);
        upLocal = new Vector3(qtyLocal.x, rowY + 1.6f, z);
        downLocal = new Vector3(qtyLocal.x, rowY - 1.6f, z);
        currencyLocal = new Vector3(cx + 0.9f, rowY, z);
        costLocal = new Vector3(cx + 2.4f, rowY, z);
    }

    /// <summary>
    /// Prefer SpriteRenderer carets (bead test proved visible). tk2d mesh bake is unreliable
    /// (tiny mesh + atlas material often draws nothing on a plain MeshRenderer).
    /// </summary>
    private bool TryCreateVerticalArrowsLocal(
        Transform confirm,
        Transform layoutParent,
        Transform arrowParent,
        Vector3 upLocal,
        Vector3 downLocal)
    {
        SpriteRenderer? styleSrc = _hudCurrencyIcon
            ?? CostSpriteField?.GetValue(_shopStats) as SpriteRenderer;

        // 1) Unity Sprite arrow on the working SpriteRenderer setup.
        EnsureArrowSpriteCached();
        if (_cachedArrowSprite != null && styleSrc != null)
        {
            _hudArrowUp = CloneSpriteCaret(styleSrc, arrowParent, "ArrowUp", upLocal, 90f, 2.0f);
            _hudArrowDown = CloneSpriteCaret(styleSrc, arrowParent, "ArrowDown", downLocal, 270f, 2.0f);
            ApplySprite(_hudArrowUp, _cachedArrowSprite);
            ApplySprite(_hudArrowDown, _cachedArrowSprite);
            RememberArrowBaseScales();
            MerchantStackerPlugin.Log.LogInfo(
                $"Arrows from sprite '{_cachedArrowSprite.name}' size={SpriteSize(_hudArrowUp)}");
            return true;
        }

        // 2) Currency bead — known-visible in this HUD (visibility probe that worked).
        if (styleSrc != null && styleSrc.sprite != null)
        {
            _hudArrowUp = CloneSpriteCaret(styleSrc, arrowParent, "ArrowUp", upLocal, 90f, 2.0f);
            _hudArrowDown = CloneSpriteCaret(styleSrc, arrowParent, "ArrowDown", downLocal, 270f, 2.0f);
            RememberArrowBaseScales();
            MerchantStackerPlugin.Log.LogInfo(
                $"Arrows from currency sprite '{styleSrc.sprite.name}' size={SpriteSize(_hudArrowUp)}");
            return true;
        }

        // 3) Optional tk2d mesh bake only if source geometry is already substantial.
        EnsureCaretMeshCached(confirm, layoutParent);
        if (_cachedCaretMesh != null && _cachedCaretMaterial != null)
        {
            _hudArrowUp = CreateMeshCaret(arrowParent, "ArrowUp", upLocal, 90f, 0.9f);
            _hudArrowDown = CreateMeshCaret(arrowParent, "ArrowDown", downLocal, -90f, 0.9f);
            RememberArrowBaseScales();
            MerchantStackerPlugin.Log.LogInfo(
                $"Arrows from tk2d caret mesh size={MeshSize(_hudArrowUp)}");
            return _hudArrowUp != null && _hudArrowDown != null;
        }

        return false;
    }

    /// <summary>
    /// Capture Pointer/ScrollView tk2d mesh+material once. Plain MeshFilter clones render;
    /// full Pointer Instantiate did not (InvAnimate / empty clone).
    /// </summary>
    private static void EnsureCaretMeshCached(Transform confirm, Transform layoutParent)
    {
        if (_cachedCaretMesh != null && _cachedCaretMaterial != null)
        {
            return;
        }

        GameObject? src = null;

        Transform? pointer = FindNamedTransform(confirm, "Pointer L")
            ?? FindNamedTransform(layoutParent, "Pointer L");
        if (pointer != null)
        {
            Transform? yes = pointer.parent;
            while (yes != null && yes.name != "Yes" && yes.name != "Confirm")
            {
                yes = yes.parent;
            }

            if (yes != null && yes.name == "Yes" && !yes.gameObject.activeSelf)
            {
                yes.gameObject.SetActive(true);
            }

            pointer.gameObject.SetActive(true);
            pointer.GetComponent<InvAnimateUpAndDown>()?.Show();
            src = pointer.gameObject;
        }

        if (src == null)
        {
            foreach (ScrollView view in Resources.FindObjectsOfTypeAll<ScrollView>())
            {
                if (view == null || !view.gameObject.scene.IsValid())
                {
                    continue;
                }

                var up = ScrollUpArrowField?.GetValue(view) as Component;
                if (up == null)
                {
                    continue;
                }

                up.gameObject.SetActive(true);
                (up as InvAnimateUpAndDown)?.Show();
                src = up.gameObject;
                break;
            }
        }

        if (src == null)
        {
            return;
        }

        var mf = src.GetComponentInChildren<MeshFilter>(true);
        var mr = src.GetComponentInChildren<MeshRenderer>(true);
        if (mf == null || mr == null || mf.sharedMesh == null || mr.sharedMaterial == null)
        {
            MerchantStackerPlugin.Log.LogWarning(
                $"Caret bake skipped: mesh={mf?.sharedMesh != null} mat={mr?.sharedMaterial != null}");
            return;
        }

        Vector3 meshSize = mf.sharedMesh.bounds.size;
        if (Mathf.Max(meshSize.x, meshSize.y) < 0.08f)
        {
            // Pointer often reports a near-empty mesh when cloned/baked — don't cache it.
            MerchantStackerPlugin.Log.LogInfo(
                $"Caret bake skipped: mesh too small ({meshSize})");
            return;
        }

        // Own copy so hiding/destroying the template can't clear our mesh.
        _cachedCaretMesh = UnityEngine.Object.Instantiate(mf.sharedMesh);
        _cachedCaretMesh.name = "MerchantStacker_CaretMesh";
        _cachedCaretMaterial = mr.sharedMaterial;
        _cachedCaretSortLayer = mr.sortingLayerID;
        _cachedCaretSortOrder = Math.Max(mr.sortingOrder + 20, 260);
        MerchantStackerPlugin.Log.LogInfo(
            $"Cached tk2d caret mesh verts={_cachedCaretMesh.vertexCount} "
            + $"bounds={_cachedCaretMesh.bounds.size}");
    }

    private static void EnsureArrowSpriteCached()
    {
        if (_arrowAssetsSearched)
        {
            return;
        }

        _arrowAssetsSearched = true;

        // Prefer SpriteRenderers already in the loaded inventory UI.
        foreach (InventoryArrowContainer box in Resources.FindObjectsOfTypeAll<InventoryArrowContainer>())
        {
            if (box == null || !box.gameObject.scene.IsValid())
            {
                continue;
            }

            foreach (SpriteRenderer sr in box.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr?.sprite != null && LooksLikeArrowSprite(sr.sprite.name, sr.gameObject.name))
                {
                    _cachedArrowSprite = sr.sprite;
                    MerchantStackerPlugin.Log.LogInfo($"Cached arrow sprite from InventoryArrowContainer '{sr.sprite.name}'");
                    return;
                }
            }
        }

        foreach (Sprite sp in Resources.FindObjectsOfTypeAll<Sprite>())
        {
            if (sp == null || !LooksLikeArrowSprite(sp.name, sp.name))
            {
                continue;
            }

            // Prefer compact UI carets over map/wide art.
            if (sp.rect.width > 128f || sp.rect.height > 128f)
            {
                continue;
            }

            _cachedArrowSprite = sp;
            MerchantStackerPlugin.Log.LogInfo($"Cached arrow sprite '{sp.name}' ({sp.rect.width}x{sp.rect.height})");
            return;
        }
    }

    private static bool LooksLikeArrowSprite(string spriteName, string goName)
    {
        string n = spriteName + " " + goName;
        if (ContainsIgnore(n, "map") || ContainsIgnore(n, "quest") || ContainsIgnore(n, "pan")
            || ContainsIgnore(n, "compass") || ContainsIgnore(n, "marker"))
        {
            return false;
        }

        return ContainsIgnore(n, "arrow") || ContainsIgnore(n, "caret") || ContainsIgnore(n, "chevron")
            || ContainsIgnore(n, "pointer");
    }

    private static GameObject CreateMeshCaret(
        Transform parent,
        string name,
        Vector3 localPos,
        float rotationZ,
        float scale)
    {
        var go = new GameObject(name);
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = Quaternion.Euler(0f, 0f, rotationZ);
        go.transform.localScale = Vector3.one * scale;

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = _cachedCaretMesh;

        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = _cachedCaretMaterial;
        mr.sortingLayerID = _cachedCaretSortLayer;
        mr.sortingOrder = _cachedCaretSortOrder;
        mr.enabled = true;

        // If baked mesh is tiny in world space, enlarge until readable (~0.8uu).
        float extent = Mathf.Max(mr.bounds.size.x, mr.bounds.size.y);
        if (extent > 1e-4f && extent < 0.5f)
        {
            go.transform.localScale *= 0.8f / extent;
        }

        return go;
    }

    private static void ApplySprite(GameObject? go, Sprite sprite)
    {
        if (go == null)
        {
            return;
        }

        var sr = go.GetComponent<SpriteRenderer>();
        if (sr != null)
        {
            sr.sprite = sprite;
            sr.enabled = true;
        }
    }

    private void RememberArrowBaseScales()
    {
        if (_hudArrowUp != null)
        {
            _arrowUpBaseLocalScale = _hudArrowUp.transform.localScale;
            _arrowUpLocalRot = _hudArrowUp.transform.localRotation;
        }

        if (_hudArrowDown != null)
        {
            _arrowDownBaseLocalScale = _hudArrowDown.transform.localScale;
            _arrowDownLocalRot = _hudArrowDown.transform.localRotation;
        }
    }

    private void DestroyArrowPair()
    {
        if (_hudArrowUp != null)
        {
            UnityEngine.Object.Destroy(_hudArrowUp);
            _hudArrowUp = null;
        }

        if (_hudArrowDown != null)
        {
            UnityEngine.Object.Destroy(_hudArrowDown);
            _hudArrowDown = null;
        }
    }

    private static Vector3 MeshSize(GameObject? go)
    {
        if (go == null)
        {
            return Vector3.zero;
        }

        var mr = go.GetComponentInChildren<MeshRenderer>(true);
        if (mr != null)
        {
            return mr.bounds.size;
        }

        return SpriteSize(go);
    }

    private static Vector3 SpriteSize(GameObject? go)
    {
        if (go == null)
        {
            return Vector3.zero;
        }

        var sr = go.GetComponentInChildren<SpriteRenderer>(true);
        return sr != null ? sr.bounds.size : Vector3.zero;
    }

    /// <summary>Up/down carets from a SpriteRenderer already proven visible in this HUD.</summary>
    private static GameObject CloneSpriteCaret(
        SpriteRenderer template,
        Transform parent,
        string name,
        Vector3 localPos,
        float rotationZ,
        float scaleMul)
    {
        var go = UnityEngine.Object.Instantiate(template.gameObject, parent);
        go.name = name;
        go.layer = parent.gameObject.layer;
        go.SetActive(true);
        go.transform.localPosition = localPos;
        go.transform.localRotation = Quaternion.Euler(0f, 0f, rotationZ);
        go.transform.localScale = Vector3.one * scaleMul;

        for (int i = go.transform.childCount - 1; i >= 0; i--)
        {
            UnityEngine.Object.Destroy(go.transform.GetChild(i).gameObject);
        }

        var sr = go.GetComponent<SpriteRenderer>();
        if (sr != null)
        {
            sr.enabled = true;
            Color c = sr.color;
            c.a = 1f;
            sr.color = c;
            sr.sortingOrder = Math.Max(template.sortingOrder + 10, 260);
            sr.sortingLayerID = template.sortingLayerID;
        }

        return go;
    }

    /// <summary>Match arrow draw order to qty TMP so carets aren't behind the confirm panel.</summary>
    private void MatchArrowDrawOrderToQty()
    {
        if (_hudQty == null)
        {
            return;
        }

        var qtyMr = _hudQty.GetComponent<MeshRenderer>();
        if (qtyMr == null)
        {
            return;
        }

        foreach (GameObject? arrow in new[] { _hudArrowUp, _hudArrowDown })
        {
            if (arrow == null)
            {
                continue;
            }

            foreach (MeshRenderer mr in arrow.GetComponentsInChildren<MeshRenderer>(true))
            {
                mr.enabled = true;
                mr.sortingLayerID = qtyMr.sortingLayerID;
                mr.sortingOrder = qtyMr.sortingOrder + 5;
            }

            foreach (SpriteRenderer sr in arrow.GetComponentsInChildren<SpriteRenderer>(true))
            {
                sr.enabled = true;
                sr.sortingLayerID = qtyMr.sortingLayerID;
                sr.sortingOrder = qtyMr.sortingOrder + 5;
            }
        }
    }

    private void ForceArrowRenderersOn()
    {
        foreach (GameObject? arrow in new[] { _hudArrowUp, _hudArrowDown })
        {
            if (arrow == null)
            {
                continue;
            }

            if (!arrow.activeSelf)
            {
                arrow.SetActive(true);
            }

            foreach (MeshRenderer mr in arrow.GetComponentsInChildren<MeshRenderer>(true))
            {
                mr.enabled = true;
            }

            foreach (SpriteRenderer sr in arrow.GetComponentsInChildren<SpriteRenderer>(true))
            {
                sr.enabled = true;
            }
        }
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

    private static bool ContainsIgnore(string haystack, string needle) =>
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private System.Collections.IEnumerator EnsureHudSoon()
    {
        yield return null;
        if (!_active || !_inShop)
        {
            yield break;
        }

        ReassertHudPoses();
        MatchArrowDrawOrderToQty();
        ForceArrowRenderersOn();
        RefreshInShopHud();
    }

    private void RefreshInShopHud()
    {
        int total = _unitCost * _quantity;
        bool qtyChanged = _quantity != _lastHudQty;
        bool totalChanged = total != _lastHudTotal;
        if (!qtyChanged && !totalChanged)
        {
            return;
        }

        _lastHudQty = _quantity;
        _lastHudTotal = total;

        if (qtyChanged)
        {
            ApplyTmp(_hudQty, _quantity.ToString(CultureInfo.InvariantCulture));
        }

        if (totalChanged)
        {
            ApplyTmp(_hudCost, total.ToString(CultureInfo.InvariantCulture));
        }

        bool upOn = _quantity < _max;
        bool downOn = _quantity > _min;
        if (upOn != _lastArrowUpOn)
        {
            _lastArrowUpOn = upOn;
            SetArrowVisible(_hudArrowUp, upOn, _arrowUpBaseLocalScale);
        }

        if (downOn != _lastArrowDownOn)
        {
            _lastArrowDownOn = downOn;
            SetArrowVisible(_hudArrowDown, downOn, _arrowDownBaseLocalScale);
        }

        if (_hudCurrencyIcon != null)
        {
            _hudCurrencyIcon.enabled = true;
            if (!_hudCurrencyIcon.gameObject.activeSelf)
            {
                _hudCurrencyIcon.gameObject.SetActive(true);
            }
        }
    }

    private static void SetArrowVisible(GameObject? arrow, bool fullyVisible, Vector3 baseLocalScale)
    {
        if (arrow == null)
        {
            return;
        }

        // Don't tint tk2d atlas materials (no _Color) — shrink slightly when at range end.
        arrow.transform.localScale = baseLocalScale * (fullyVisible ? 1f : 0.55f);

        foreach (MeshRenderer mr in arrow.GetComponentsInChildren<MeshRenderer>(true))
        {
            mr.enabled = true;
        }

        foreach (SpriteRenderer sr in arrow.GetComponentsInChildren<SpriteRenderer>(true))
        {
            sr.enabled = true;
        }
    }

    private void ReassertHudPoses()
    {
        if (_hudQty != null)
        {
            SetLocalPose(_hudQty.transform, _qtyLocal, Vector3.one);
        }

        if (_hudCost != null)
        {
            SetLocalPose(_hudCost.transform, _costLocal, Vector3.one);
        }

        if (_hudCurrencyIcon != null)
        {
            SetLocalPose(_hudCurrencyIcon.transform, _currencyLocal, _hudCurrencyIcon.transform.localScale);
        }

        if (_hudArrowUp != null)
        {
            float vis = _lastArrowUpOn ? 1f : 0.55f;
            _hudArrowUp.transform.localPosition = _arrowUpLocal;
            _hudArrowUp.transform.localRotation = _arrowUpLocalRot;
            _hudArrowUp.transform.localScale = _arrowUpBaseLocalScale * vis;
        }

        if (_hudArrowDown != null)
        {
            float vis = _lastArrowDownOn ? 1f : 0.55f;
            _hudArrowDown.transform.localPosition = _arrowDownLocal;
            _hudArrowDown.transform.localRotation = _arrowDownLocalRot;
            _hudArrowDown.transform.localScale = _arrowDownBaseLocalScale * vis;
        }
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
                tmp.ForceMeshUpdate(true);
            }

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

    private static void SetLocalPose(Transform t, Vector3 localPos, Vector3 localScale)
    {
        t.localPosition = localPos;
        t.localRotation = Quaternion.identity;
        t.localScale = localScale;
        if (t is RectTransform rt)
        {
            rt.anchoredPosition3D = localPos;
        }
    }

    private static TextMeshPro CloneTmpLocal(
        TextMeshPro template,
        Transform parent,
        string name,
        Vector3 localPos,
        string text,
        float fontSize,
        TextAlignmentOptions alignment = TextAlignmentOptions.Center)
    {
        // Unparented Instantiate + SetParent(false) so we don't inherit the template's world pose.
        var go = UnityEngine.Object.Instantiate(template.gameObject);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.SetActive(true);
        SetLocalPose(go.transform, localPos, Vector3.one);

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

    private static SpriteRenderer CloneSpriteLocal(
        SpriteRenderer template,
        Transform parent,
        string name,
        Vector3 localPos,
        float scaleMul)
    {
        var go = UnityEngine.Object.Instantiate(template.gameObject);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.SetActive(true);
        SetLocalPose(go.transform, localPos, template.transform.localScale * scaleMul);

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

        // Wipe our TMP meshes before destroy so digits can't linger.
        ClearTmpVisual(_hudQty);
        ClearTmpVisual(_hudCost);

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
        _chromeToKeepOff.Clear();
        Patches.ShopConfirmShowPatches.LockConfirmChrome = false;
        _itemArtSprite = null;
        _itemArtName = null;
        _itemArtSpriteSr = null;
        _itemArtNameTmp = null;
        _itemArtNameMr = null;
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
                            || (go.name == "MerchantStacker_QtyHud"
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

        // Recalc affordability occasionally — not every frame (was hitching).
        _maxRecalcTimer -= Time.unscaledDeltaTime;
        if (_maxRecalcTimer <= 0f)
        {
            _maxRecalcTimer = 0.25f;
            int affordable = Eligibility.GetAffordableCount(_unitCost, _currency);
            int room = _item != null ? Eligibility.GetRoomUntilCap(_item) : _max;
            int newMax = Math.Max(1, Math.Min(Math.Max(1, affordable), Math.Max(1, room)));
            if (newMax != _max)
            {
                _max = newMax;
                if (_quantity > _max)
                {
                    _quantity = _max;
                }

                if (_inShop)
                {
                    _lastHudQty = int.MinValue;
                    RefreshInShopHud();
                }
                else
                {
                    RefreshDisplay();
                }
            }
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
