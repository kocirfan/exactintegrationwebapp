using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using ExactOnline.Converters;

namespace ExactOnline.Models
{
    public class ExactOnlineAccountResponse
    {
        public List<Account> Accounts { get; set; }
    }

    public class Account
    {
        // Metadata
        public AccountMetadata __metadata { get; set; }
        
        // Core Identity Fields
        public Guid ID { get; set; }
        public string Code { get; set; }
        public string Name { get; set; }
        public string SearchCode { get; set; }
        public string Status { get; set; }
        [JsonConverter(typeof(ExactOnlineDateTimeConverter))]
        public DateTime? StatusSince { get; set; }
        public string Type { get; set; }
        public int Division { get; set; }
        
        // Contact Information
        public string Email { get; set; }
        public string Phone { get; set; }
        public string PhoneExtension { get; set; }
        public string Fax { get; set; }
        public string Website { get; set; }
        
        // Address Information
        public string AddressLine1 { get; set; }
        public string AddressLine2 { get; set; }
        public string AddressLine3 { get; set; }
        public string Postcode { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public string StateName { get; set; }
        public string Country { get; set; }
        public string CountryName { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string AddressSource { get; set; }
        
        // Company Information
        public string ChamberOfCommerce { get; set; }
        public string VATNumber { get; set; }
        public string TradeName { get; set; }
        public Guid? CompanySize { get; set; }
        // public DateTime? EstablishedDate { get; set; }
        public string DunsNumber { get; set; }
        public string GlnNumber { get; set; }
        public string OINNumber { get; set; }
        public string RSIN { get; set; }
        public string BSN { get; set; }
        public string BRIN { get; set; }
        public string EORINumber { get; set; }
        public string UniqueTaxpayerReference { get; set; }
        
        // Account Manager
        public Guid? AccountManager { get; set; }
        public string AccountManagerFullName { get; set; }
        public int? AccountManagerHID { get; set; }
        
        // Accountant
        public Guid? Accountant { get; set; }
        
        // Activity and Classification
        [JsonConverter(typeof(FlexibleGuidConverter))]
        public Guid? ActivitySector { get; set; }
        [JsonConverter(typeof(FlexibleGuidConverter))]
        public Guid? ActivitySubSector { get; set; }
        [JsonConverter(typeof(FlexibleGuidConverter))]
        public Guid? Classification { get; set; }
        public string ClassificationDescription { get; set; }
        [JsonConverter(typeof(FlexibleGuidConverter))]
        public Guid? Classification1 { get; set; }
        [JsonConverter(typeof(FlexibleGuidConverter))]
        public Guid? Classification2 { get; set; }
        [JsonConverter(typeof(FlexibleGuidConverter))]
        public Guid? Classification3 { get; set; }
        [JsonConverter(typeof(FlexibleGuidConverter))]
        public Guid? Classification4 { get; set; }
        [JsonConverter(typeof(FlexibleGuidConverter))]
        public Guid? Classification5 { get; set; }
        [JsonConverter(typeof(FlexibleGuidConverter))]
        public Guid? Classification6 { get; set; }
        [JsonConverter(typeof(FlexibleGuidConverter))]
        public Guid? Classification7 { get; set; }
        [JsonConverter(typeof(FlexibleGuidConverter))]
        public Guid? Classification8 { get; set; }
        
        // Financial Information
        public string Currency { get; set; }
        public string PurchaseCurrency { get; set; }
        public string PurchaseCurrencyDescription { get; set; }
        public string SalesCurrency { get; set; }
        public string SalesCurrencyDescription { get; set; }
        public double CreditLinePurchase { get; set; }
        public double CreditLineSales { get; set; }
        public double DiscountPurchase { get; set; }
        public double DiscountSales { get; set; }
        public double CostPaid { get; set; }
        
        // GL Accounts
        public Guid? GLAccountPurchase { get; set; }
        public Guid? GLAccountSales { get; set; }
        public Guid? GLAP { get; set; }
        public Guid? GLAR { get; set; }
        
        // Payment Conditions
        public string PaymentConditionPurchase { get; set; }
        public string PaymentConditionPurchaseDescription { get; set; }
        public string PaymentConditionSales { get; set; }
        public string PaymentConditionSalesDescription { get; set; }
        
        // VAT Information
        public string PurchaseVATCode { get; set; }
        public string PurchaseVATCodeDescription { get; set; }
        public string SalesVATCode { get; set; }
        public string SalesVATCodeDescription { get; set; }
        [JsonConverter(typeof(FlexibleBooleanConverter))]
        public bool HasWithholdingTaxSales { get; set; }
        public string VATLiability { get; set; }
        public string PayAsYouEarn { get; set; }
        
        // Sales Tax
        public Guid? SalesTaxSchedule { get; set; }
        public string SalesTaxScheduleCode { get; set; }
        public string SalesTaxScheduleDescription { get; set; }
        
        // Flags and Booleans
        [JsonConverter(typeof(FlexibleBooleanConverter))]
        public bool Blocked { get; set; }
        [JsonConverter(typeof(FlexibleBooleanConverter))]
        public bool CanDropShip { get; set; }
        [JsonConverter(typeof(FlexibleBooleanConverter))]
        public bool EnableSalesPaymentLink { get; set; }
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int IsAccountant { get; set; }
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int IsAgency { get; set; }
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int IsAnonymised { get; set; }
        [JsonConverter(typeof(FlexibleBooleanConverter))]
        public bool IsBank { get; set; }
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int IsCompetitor { get; set; }
        [JsonConverter(typeof(FlexibleNullableBooleanConverter))]
        public bool? IsExtraDuty { get; set; }
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int IsMailing { get; set; }
        [JsonConverter(typeof(FlexibleBooleanConverter))]
        public bool IsMember { get; set; }
        [JsonConverter(typeof(FlexibleBooleanConverter))]
        public bool IsPilot { get; set; }
        [JsonConverter(typeof(FlexibleBooleanConverter))]
        public bool IsPurchase { get; set; }
        [JsonConverter(typeof(FlexibleBooleanConverter))]
        public bool IsReseller { get; set; }
        [JsonConverter(typeof(FlexibleBooleanConverter))]
        public bool IsSales { get; set; }
        [JsonConverter(typeof(FlexibleBooleanConverter))]
        public bool IsSupplier { get; set; }
        [JsonConverter(typeof(FlexibleBooleanConverter))]
        public bool RecepientOfCommissions { get; set; }
        [JsonConverter(typeof(FlexibleBooleanConverter))]
        public bool ShowRemarkForSales { get; set; }
        
        // Invoice Settings
        public Guid? InvoiceAccount { get; set; }
        public string InvoiceAccountCode { get; set; }
        public string InvoiceAccountName { get; set; }
        public int InvoiceAttachmentType { get; set; }
        public int InvoicingMethod { get; set; }
        public int AutomaticProcessProposedEntry { get; set; }
        public int SeparateInvPerProject { get; set; }
        public int SeparateInvPerSubscription { get; set; }
        
        // Shipping and Delivery
        public int PurchaseLeadDays { get; set; }
        public int ShippingLeadDays { get; set; }
        public Guid? ShippingMethod { get; set; }
        public int? DeliveryAdvice { get; set; }
        
        // Incoterms
        public string IncotermAddressPurchase { get; set; }
        public string IncotermCodePurchase { get; set; }
        public int? IncotermVersionPurchase { get; set; }
        public string IncotermAddressSales { get; set; }
        public string IncotermCodeSales { get; set; }
        public int? IncotermVersionSales { get; set; }
        
        // IntraStat
        public string IntraStatArea { get; set; }
        public string IntraStatDeliveryTerm { get; set; }
        public string IntraStatSystem { get; set; }
        public string IntraStatTransactionA { get; set; }
        public string IntraStatTransactionB { get; set; }
        public string IntraStatTransportMethod { get; set; }
        
        // Language
        public string Language { get; set; }
        public string LanguageDescription { get; set; }
        
        // Lead Information
        public Guid? LeadSource { get; set; }
        public Guid? LeadPurpose { get; set; }
        
        // Logo
        public string LogoFileName { get; set; }
        public string LogoThumbnailUrl { get; set; }
        public string LogoUrl { get; set; }
        public byte[] Logo { get; set; }
        
        // Main Contact
        public Guid? MainContact { get; set; }
        
        // Parent and Business Relationships
        public Guid? Parent { get; set; }
        public Guid? Reseller { get; set; }
        public string ResellerCode { get; set; }
        public string ResellerName { get; set; }
        public string BusinessType { get; set; }
        
        // Price List
        public Guid? PriceList { get; set; }
        
        // Cost Center
        public string Costcenter { get; set; }
        public string CostcenterDescription { get; set; }
        
        // Consolidation
        public int ConsolidationScenario { get; set; }
        
        // Dates
        [JsonConverter(typeof(ExactOnlineDateTimeConverter))]
        public DateTime? CustomerSince { get; set; }
        [JsonConverter(typeof(ExactOnlineDateTimeConverter))]
        public DateTime? StartDate { get; set; }
        [JsonConverter(typeof(ExactOnlineDateTimeConverter))]
        public DateTime? EndDate { get; set; }
        [JsonConverter(typeof(ExactOnlineDateTimeConverter))]
        public DateTime? ControlledDate { get; set; }
        [JsonConverter(typeof(ExactOnlineDateTimeRequiredConverter))]
        public DateTime Created { get; set; }
        [JsonConverter(typeof(ExactOnlineDateTimeRequiredConverter))]
        public DateTime Modified { get; set; }
        [JsonConverter(typeof(ExactOnlineDateTimeConverter))]
        public DateTime? EstablishedDate { get; set; }
        
        // Creator and Modifier
        public Guid Creator { get; set; }
        public string CreatorFullName { get; set; }
        public Guid Modifier { get; set; }
        public string ModifierFullName { get; set; }
        
        // Datev (German accounting software)
        public string DatevCreditorCode { get; set; }
        public string DatevDebtorCode { get; set; }
        [JsonConverter(typeof(FlexibleBooleanConverter))]
        public bool IgnoreDatevWarningMessage { get; set; }
        
        // Additional Fields
        public string Remarks { get; set; }
        public int SecurityLevel { get; set; }
        public string CodeAtSupplier { get; set; }
        public string CustomField { get; set; }
        public Guid? Document { get; set; }
        public string Source { get; set; }
        
        // Peppol (E-invoicing)
        public string PeppolIdentifierType { get; set; }
        public string PeppolIdentifier { get; set; }
        
        // Bank Accounts (Deferred/Related Data)
        public BankAccountsDeferred BankAccounts { get; set; }
    }

    public class AccountMetadata
    {
        public string uri { get; set; }
        public string type { get; set; }
    }

    public class BankAccountsDeferred
    {
        public DeferredUri __deferred { get; set; }
    }

    public class DeferredUri
    {
        public string uri { get; set; }
    }
}