namespace ShopifyProductApp.Services;

/// <summary>
/// Exact hesabından Shopify müşterisine yazılacak <c>tags</c> değerini üretir.
///
/// <list type="bullet">
///   <item><c>ClassificationDescription</c> doluysa tag olarak eklenir (indirim grubu).</item>
///   <item><c>betaling-factuur</c> (faturayla ödeme) yalnızca geçerli bir VAT numarası olan müşterilere eklenir.
///         VAT numarasının ülkesi fark etmez (NL dahil). Geçerlilik kuralı için bkz. <see cref="VatTaxRules.NormalizeVatNumber"/>.</item>
/// </list>
///
/// Not: Shopify REST API'de <c>tags</c> alanı mevcut listeyi <b>değiştirir</b>; bu yüzden güncellemede
/// VAT numarası olmayan bir müşterinin daha önce aldığı <c>betaling-factuur</c> tag'i kaldırılır.
/// </summary>
public static class CustomerTagRules
{
    /// <summary>Faturayla ödeme seçeneğini açan Shopify müşteri tag'i.</summary>
    public const string InvoicePaymentTag = "betaling-factuur";

    /// <summary>
    /// Shopify'a gönderilecek virgülle ayrılmış tag listesini döndürür.
    /// </summary>
    /// <param name="classificationDescription">Exact'taki sınıflandırma açıklaması (boş olabilir).</param>
    /// <param name="vatNumber">Exact'taki VAT numarası (boş olabilir).</param>
    public static string Build(string classificationDescription, string vatNumber)
    {
        var tags = new List<string>();

        if (!string.IsNullOrWhiteSpace(classificationDescription))
            tags.Add(classificationDescription.Trim());

        if (HasVatNumber(vatNumber))
            tags.Add(InvoicePaymentTag);

        return string.Join(",", tags);
    }

    /// <summary>Geçerli (rakam içeren) bir VAT numarası var mı?</summary>
    public static bool HasVatNumber(string vatNumber)
        => VatTaxRules.NormalizeVatNumber(vatNumber) != null;
}
