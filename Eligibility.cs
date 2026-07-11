namespace MerchantStacker;

internal static class Eligibility
{
    /// <summary>
    /// Infinite-stock shop items that still have room under their stack ceiling.
    /// </summary>
    public static bool IsBulkEligible(ShopItem? item)
    {
        if (item == null || !item.IsAvailable)
        {
            return false;
        }

        // One-shot purchases (maps, unique items, playerData bools).
        if (item.IsAvailableNotInfinite)
        {
            return false;
        }

        if (item.Cost <= 0)
        {
            return false;
        }

        if (item.Item is not CollectableItem collectable)
        {
            return false;
        }

        return collectable.CanGetMore() && !collectable.IsAtMax();
    }

    public static int GetMaxQuantity(ShopItem item)
    {
        if (item.Item is not CollectableItem collectable)
        {
            return 1;
        }

        int room = GetRoomUntilCap(collectable);
        int affordable = GetAffordableCount(item);
        return System.Math.Max(1, System.Math.Min(room, affordable));
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
