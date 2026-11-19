namespace ShopifyProductApp.Services;

/// <summary>Exact sipariş satırlarına ürünün kendi KDV kodu yerine yazılacak KDV kodu.</summary>
public sealed record OrderVatOverride(string VatCode, double VatPercentage, string Reason);

/// <summary>
/// Shopify'da KDV'siz (tax_exempt) geçen bir siparişin Exact'a da KDV'siz gitmesini sağlar.
///
/// Sorun: sipariş satırlarına ürünün SalesVatCode'u (çoğunlukla "2" = %21) açıkça gönderiliyordu;
/// Exact satırdaki kodu kullandığı için cari kartında "5 - BTW Verlegd" olsa bile sipariş %21 KDV ile açılıyordu.
///
/// <list type="bullet">
///   <item>bol.com siparişi veya tax_exempt değil      → null (ürünün kendi kodu kullanılır)</item>
///   <item>tax_exempt + NL                              → null (NL içi satış KDV'lidir, muaf olmamalı; ürün kodu korunur)</item>
///   <item>tax_exempt + AB ülkesi (BE, DE, FR…)        → AB içi reverse charge kodu (varsayılan "5"), %0</item>
///   <item>tax_exempt + AB dışı (GB, CH, NO…)          → ihracat kodu (varsayılan "6"), %0</item>
/// </list>
/// Ülke: fatura adresi → teslimat adresi → müşterinin varsayılan adresi.
/// </summary>
public static class OrderVatRules
{
    public const string DefaultEuReverseChargeVatCode = "5"; // BTW Verlegd Verkoop binnen EU
    public const string DefaultExportVatCode = "6";          // BTW Verlegd Verkoop buiten EU

    private static readonly HashSet<string> EuCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        "AT", "BE", "BG", "CY", "CZ", "DE", "DK", "EE", "ES", "FI", "FR", "GR", "EL", "HR", "HU",
        "IE", "IT", "LT", "LU", "LV", "MT", "NL", "PL", "PT", "RO", "SE", "SI", "SK"
    };

    public static OrderVatOverride? Resolve(
        ShopifyOrder order,
        bool isBolOrder,
        string? euReverseChargeVatCode = null,
        string? exportVatCode = null)
    {
        if (order == null || isBolOrder || order.TaxExempt != true)
            return null;

        var country = ResolveCountry(order);
        if (country == null || country == "NL")
            return null;

        return EuCountries.Contains(country)
            ? new OrderVatOverride(
                string.IsNullOrWhiteSpace(euReverseChargeVatCode) ? DefaultEuReverseChargeVatCode : euReverseChargeVatCode.Trim(),
                0d,
                $"Shopify tax_exempt, AB içi ({country}) → reverse charge")
            : new OrderVatOverride(
                string.IsNullOrWhiteSpace(exportVatCode) ? DefaultExportVatCode : exportVatCode.Trim(),
                0d,
                $"Shopify tax_exempt, AB dışı ({country}) → ihracat");
    }

    public static string? ResolveCountry(ShopifyOrder order)
    {
        var code = new[]
            {
                order.BillingAddress?.CountryCode,
                order.ShippingAddress?.CountryCode,
                order.Customer?.DefaultAddress?.CountryCode
            }
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

        return code?.Trim().ToUpperInvariant();
    }
}
