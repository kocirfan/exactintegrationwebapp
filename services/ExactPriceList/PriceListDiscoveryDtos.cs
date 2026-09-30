namespace ShopifyProductApp.Services.ExactPriceList;

/// <summary>
/// Exact'ta bir müşteriye özel ürün fiyatı (logistics/SalesItemPrices, Account alanı dolu).
/// Müşteri fiyatlarının Exact'taki gerçek kaynağı burasıdır — SalesPriceLists değil.
/// </summary>
public class CustomerItemPriceDto
{
    public string ID { get; set; }

    /// <summary>Exact müşteri (Account) GUID'i.</summary>
    public string AccountId { get; set; }
    public string AccountName { get; set; }

    public string ItemId { get; set; }

    /// <summary>Ürün SKU'su — Shopify variant eşlemesinde anahtar.</summary>
    public string ItemCode { get; set; }
    public string ItemDescription { get; set; }

    public decimal Price { get; set; }
    public string Currency { get; set; }

    /// <summary>Kademeli fiyatta minimum adet. 1'den büyükse Shopify sabit fiyatına çevrilemez.</summary>
    public double Quantity { get; set; }

    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }

    /// <summary>Bugün itibarıyla geçerli mi? (StartDate geçmiş, EndDate gelmemiş)</summary>
    public bool IsActiveOn(DateTime moment)
    {
        if (StartDate.HasValue && StartDate.Value > moment) return false;
        if (EndDate.HasValue && EndDate.Value < moment) return false;
        return true;
    }
}

/// <summary>
/// Tek bir müşterinin tüm özel fiyatları — Shopify'da bir katalog/price list'e karşılık gelir.
/// </summary>
public class CustomerPriceGroupDto
{
    public string AccountId { get; set; }
    public string AccountName { get; set; }

    public List<CustomerItemPriceDto> Prices { get; set; } = new();

    public int ActivePriceCount { get; set; }
    public int FuturePriceCount { get; set; }
    public int ExpiredPriceCount { get; set; }

    /// <summary>Adet kademesi olan (Quantity > 1) satırlar — sabit fiyata çevrilemez.</summary>
    public int QuantityTieredCount { get; set; }

    // --- Müşteri tipi (B2B company kurulabilir mi?) ---

    /// <summary>Exact'taki VAT numarası. Yoksa müşteri muhtemelen şahıstır (B2C).</summary>
    public string VatNumber { get; set; }

    /// <summary>Ticaret sicil numarası (KvK). Doluysa tüzel kişi göstergesi.</summary>
    public string ChamberOfCommerce { get; set; }

    /// <summary>
    /// B2B company olarak kurulabilir mi? VAT veya KvK numarasından biri varsa evet.
    /// Hiçbiri yoksa şahıs kabul edilir ve katalog kurulmaz — normal fiyattan alışveriş yapar.
    /// </summary>
    public bool LooksLikeBusiness =>
        !string.IsNullOrWhiteSpace(VatNumber) || !string.IsNullOrWhiteSpace(ChamberOfCommerce);
}

/// <summary>
/// Keşif taramasının sonucu. Hiçbir yere yazılmaz, yalnızca raporlanır.
/// </summary>
public class CustomerPriceDiscoveryReport
{
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Account alanı dolu toplam fiyat satırı sayısı.</summary>
    public int TotalCustomerPriceLines { get; set; }

    /// <summary>Özel fiyatı olan farklı müşteri sayısı = kurulacak Shopify katalog sayısı.</summary>
    public int DistinctCustomerCount { get; set; }

    /// <summary>Özel fiyatı olan farklı SKU sayısı.</summary>
    public int DistinctItemCount { get; set; }

    public int ActivePriceLines { get; set; }
    public int FuturePriceLines { get; set; }
    public int ExpiredPriceLines { get; set; }
    public int QuantityTieredLines { get; set; }

    /// <summary>EUR dışı para biriminde olan satırlar — ayrı katalog gerektirir.</summary>
    public List<string> NonEuroCurrencies { get; set; } = new();

    /// <summary>Özel fiyatı normal satış fiyatının üstünde olan satırlar.</summary>
    public List<PriceAnomalyDto> PricesAboveStandard { get; set; } = new();

    /// <summary>Exact ürün listesinde karşılığı bulunamayan SKU'lar.</summary>
    public List<string> UnresolvedItemCodes { get; set; } = new();

    /// <summary>
    /// Özel fiyatı olup VAT/KvK numarası olmayan müşteriler — muhtemelen şahıs (B2C).
    /// Bunlara B2B katalog kurulamaz; elle incelenmeli.
    /// </summary>
    public List<string> CustomersWithoutBusinessId { get; set; } = new();

    public List<CustomerPriceGroupDto> Customers { get; set; } = new();
}

/// <summary>
/// Özel fiyat normal fiyattan yüksek olan satır — müşteri herkesten pahalıya alır.
/// </summary>
public class PriceAnomalyDto
{
    public string AccountName { get; set; }
    public string ItemCode { get; set; }
    public decimal CustomerPrice { get; set; }
    public decimal StandardSalesPrice { get; set; }
}
