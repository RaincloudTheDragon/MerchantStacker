namespace MerchantStacker;

internal static class Eligibility
{
    /// <summary>
    /// Infinite-stock collectable listing (maps / uniques / bool flags excluded).
    /// Ignores affordability and stack room — used to decide if MS may touch a shop at all.
    /// </summary>
    public static bool IsStackableShopOffer(ShopItem? item)
    {
        if (item == null)
        {
            return false;
        }

        // One-shot purchases (unique items, playerData bools).
        if (item.IsAvailableNotInfinite)
        {
            return false;
        }

        // Cartographer wares (maps, pins, quill) and Bellhome furnishings are never bulk.
        // Shakra sells only these, so MS must stay entirely out of her shop.
        if (!IsBulkTypeFlags(item.GetTypeFlags()))
        {
            return false;
        }

        if (item.Cost <= 0)
        {
            return false;
        }

        return item.Item is CollectableItem;
    }

    private static bool IsBulkTypeFlags(ShopItem.TypeFlags flags) =>
        flags == ShopItem.TypeFlags.None || flags == ShopItem.TypeFlags.Item;

    /// <summary>
    /// True when this merchant stock sells any infinite stackable wares.
    /// Map-only shops (Shakra) must never be touched by MS chrome hooks.
    /// </summary>
    public static bool StockHasStackableOffers(ShopMenuStock? stock)
    {
        if (stock == null)
        {
            return false;
        }

        try
        {
            ShopMenuStock source = stock.MasterList != null ? stock.MasterList : stock;
            foreach (ShopItem item in source.EnumerateStock())
            {
                if (IsStackableShopOffer(item))
                {
                    return true;
                }
            }

            // Child Item List may have empty stock[] and share Master's spawned rows.
            int count = stock.GetItemCount();
            for (int i = 0; i < count; i++)
            {
                var stats = stock.GetItemGameObject(i)?.GetComponent<ShopItemStats>();
                if (IsStackableShopOffer(stats?.Item))
                {
                    return true;
                }
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }

    /// <summary>
    /// Infinite-stock shop items that still have room under their stack ceiling.
    /// </summary>
    public static bool IsBulkEligible(ShopItem? item)
    {
        if (item == null || !item.IsAvailable)
        {
            return false;
        }

        if (!IsStackableShopOffer(item))
        {
            return false;
        }

        if (item.Item is not CollectableItem collectable)
        {
            return false;
        }

        return collectable.CanGetMore() && !collectable.IsAtMax();
    }

    /// <summary>
    /// True when we should replace Yes/No with qty (stackable + can buy at least two).
    /// Max of 1 keeps vanilla Yes/No — a qty pad that cannot step is pointless and was a
    /// softlock source (cancel → re-confirm left Yes/No inactive under the confirm group).
    /// </summary>
    public static bool ShouldOfferBulkQty(ShopItem? item)
    {
        if (!IsBulkEligible(item) || item == null)
        {
            return false;
        }

        return GetMaxQuantity(item) >= 2;
    }

    public static int GetMaxQuantity(ShopItem item)
    {
        if (item.Item is not CollectableItem collectable)
        {
            return 0;
        }

        int room = GetRoomUntilCap(collectable);
        int affordable = GetAffordableCount(item);
        return System.Math.Max(0, System.Math.Min(room, affordable));
    }

    public static int GetRoomUntilCap(CollectableItem item)
    {
        if (!item.CanGetMore() || item.IsAtMax())
        {
            return 0;
        }

        // Probe remaining room without relying on private cap fields.
        // customMaxAmount / consumable cap are reflected by IsAtMax after hypothetical adds.
        int current = item.CollectedAmount;
        int cap = TryGetCap(item);
        if (cap > 0)
        {
            return System.Math.Max(0, cap - current);
        }

        // Fallback: allow up to 99 if uncapped-looking but still CanGetMore.
        return 99;
    }

    public static int GetAffordableCount(ShopItem item)
    {
        int cost = item.Cost;
        if (cost <= 0)
        {
            return 0;
        }

        return item.CurrencyType switch
        {
            CurrencyType.Money => PlayerData.instance.geo / cost,
            CurrencyType.Shard => PlayerData.instance.ShellShards / cost,
            _ => 0,
        };
    }

    public static int GetAffordableCount(int unitCost, CurrencyType currency)
    {
        if (unitCost <= 0)
        {
            return 0;
        }

        return currency switch
        {
            CurrencyType.Money => PlayerData.instance.geo / unitCost,
            CurrencyType.Shard => PlayerData.instance.ShellShards / unitCost,
            _ => 0,
        };
    }

    private static int TryGetCap(CollectableItem item)
    {
        try
        {
            var field = typeof(CollectableItem).GetField(
                "customMaxAmount",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field != null)
            {
                int custom = (int)field.GetValue(item)!;
                if (custom > 0)
                {
                    return custom;
                }
            }
        }
        catch
        {
            // ignored
        }

        // Vanilla consumable stack ceiling used by rosary strings / shard pouches.
        return 20;
    }
}
