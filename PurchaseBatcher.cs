using System;

namespace MerchantStacker;

internal static class PurchaseBatcher
{
    /// <summary>
    /// True while we are applying a multi-buy so nested hooks do not re-open the picker.
    /// </summary>
    internal static bool IsBatching { get; private set; }

    internal static void BuyShopItem(ShopItemStats stats, int quantity, int subItemIndex, Action? onComplete)
    {
        if (stats == null || stats.Item == null)
        {
            onComplete?.Invoke();
            return;
        }

        IsBatching = true;
        try
        {
            int bought = 0;
            for (int i = 0; i < quantity; i++)
            {
                if (!stats.CanBuy() || stats.IsAtMax() || !stats.Item.IsAvailable)
                {
                    break;
                }

                stats.SetPurchased(null, subItemIndex);
                bought++;
            }

            MerchantStackerPlugin.Log.LogInfo($"Bought {bought}x {stats.Item.DisplayName}");
        }
        finally
        {
            IsBatching = false;
        }

        onComplete?.Invoke();
    }

    internal static void BuyShopItem(ShopItem item, int quantity, int subItemIndex, Action? onComplete)
    {
        if (item == null)
        {
            onComplete?.Invoke();
            return;
        }

        IsBatching = true;
        try
        {
            int bought = 0;
            for (int i = 0; i < quantity; i++)
            {
                if (!item.IsAvailable || item.IsAtMax())
                {
                    break;
                }

                bool canAfford = item.CurrencyType switch
                {
                    CurrencyType.Money => PlayerData.instance.geo >= item.Cost,
                    CurrencyType.Shard => PlayerData.instance.ShellShards >= item.Cost,
                    _ => false,
                };
                if (!canAfford)
                {
                    break;
                }

                item.SetPurchased(null, subItemIndex);
                bought++;
            }

            MerchantStackerPlugin.Log.LogInfo($"Bought {bought}x {item.DisplayName}");
        }
        finally
        {
            IsBatching = false;
        }

        onComplete?.Invoke();
    }
}
