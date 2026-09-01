namespace ShopifyProductApp.Services;

/// <summary>
/// Shopify müşterisine gönderilen <c>tax_exempt</c> değerinin kuralı (AB KDV kuralları).
///
/// <list type="bullet">
///   <item>VAT numarası yok                         → KDV tahsil et       (tax_exempt = false)</item>
///   <item>Hollanda VAT numarası (NL...)            → KDV tahsil et       (tax_exempt = false)</item>
///   <item>NL dışı her VAT numarası (BE, DE, GB..)   → KDV muaf            (tax_exempt = true)
///         AB içi: reverse charge; AB dışı: ihracat. AB dışı B2B müşteriler eskiden de muaftı, bu davranış korunur.</item>
/// </list>
///
/// VAT numarasında ülke ön eki yoksa (örn. "0123456789") ülke olarak müşterinin adres ülkesi kullanılır.
/// Hiç rakam içermeyen değerler (örn. "N/A", "-") VAT numarası olarak kabul edilmez.
/// </summary>
public static class VatTaxRules
{
    /// <summary>
    /// Müşterinin Shopify'da KDV'den muaf (tax_exempt = true) olup olmayacağını döndürür.
    /// </summary>
    /// <param name="vatNumber">Exact'taki VAT numarası (boş olabilir).</param>
    /// <param name="addressCountryCode">Müşterinin adres ülke kodu (ISO-2); VAT numarasında ön ek yoksa kullanılır.</param>
    public static bool ShouldBeTaxExempt(string vatNumber, string addressCountryCode)
    {
        var vatCountry = ResolveVatCountry(vatNumber, addressCountryCode);

        if (vatCountry == null)
            return false; // VAT numarası yok → KDV tahsil et

        if (vatCountry == "NL")
            return false; // Hollanda VAT numarası → KDV tahsil et

        return true; // NL dışı her VAT numarası → muaf (AB içi reverse charge / AB dışı ihracat)
    }

    /// <summary>
    /// VAT numarasının ait olduğu ülke kodunu döndürür; geçerli bir VAT numarası yoksa <c>null</c>.
    /// Ön ek varsa ("NL123456789B01" → "NL") o kullanılır, yoksa adres ülkesine düşülür.
    /// </summary>
    public static string ResolveVatCountry(string vatNumber, string addressCountryCode)
    {
        var normalized = NormalizeVatNumber(vatNumber);
        if (normalized == null)
            return null;

        if (normalized.Length >= 2 && char.IsLetter(normalized[0]) && char.IsLetter(normalized[1]))
            return normalized.Substring(0, 2);

        var fallback = addressCountryCode?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(fallback) ? null : fallback;
    }

    /// <summary>
    /// Boşluk, nokta ve tire karakterlerini temizleyip büyük harfe çevirir.
    /// Boş ya da hiç rakam içermeyen değerler için <c>null</c> döner.
    /// </summary>
    public static string NormalizeVatNumber(string vatNumber)
    {
        if (string.IsNullOrWhiteSpace(vatNumber))
            return null;

        var cleaned = new string(vatNumber
            .Where(c => !char.IsWhiteSpace(c) && c != '.' && c != '-')
            .ToArray()).ToUpperInvariant();

        return cleaned.Any(char.IsDigit) ? cleaned : null;
    }
}
