using Microsoft.EntityFrameworkCore;

namespace ShopifyProductApp.Data
{
    /// <summary>
    /// MegaDeals tablosu: kampanya (ör. Black Friday) ürünlerinin normal fiyatı ve kampanya penceresi.
    /// MegaDealCompareAtService bu tabloya göre Shopify variant'larında compareAtPrice yazar / temizler.
    /// Her (Campaign, Sku) için tek satır.
    ///
    /// EnsureCreated mevcut DB'ye yeni tablo eklemediği için (bkz. PendingOperationsSchema)
    /// tablo açılışta idempotent SQL ile oluşturulur; varsa dokunulmaz. EF modeline eklenmedi,
    /// servis ham SQL ile okur/yazar.
    /// </summary>
    public static class MegaDealSchema
    {
        public const string CreateTableSql = @"
IF OBJECT_ID(N'[MegaDeals]', N'U') IS NULL
BEGIN
    CREATE TABLE [MegaDeals] (
        [Id] int IDENTITY(1,1) NOT NULL,
        [Campaign] nvarchar(100) NOT NULL,
        [Sku] nvarchar(100) NOT NULL,
        [RegularPrice] decimal(18,2) NOT NULL,
        [DealPrice] decimal(18,2) NULL,
        [StartsAtUtc] datetime2 NOT NULL,
        [EndsAtUtc] datetime2 NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [VariantCount] int NULL,
        [ShopifyPrice] decimal(18,2) NULL,
        [ShopifyCompareAt] decimal(18,2) NULL,
        [LastAction] nvarchar(1000) NULL,
        [LastError] nvarchar(1000) NULL,
        [LastCheckedAt] datetime2 NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_MegaDeals] PRIMARY KEY ([Id])
    );
    CREATE UNIQUE INDEX [UX_MegaDeals_Campaign_Sku] ON [MegaDeals] ([Campaign], [Sku]);
    CREATE INDEX [IX_MegaDeals_Status] ON [MegaDeals] ([Status]);
END";

        /// <summary>Tablo yoksa oluşturur; varsa dokunmaz.</summary>
        public static Task EnsureTableAsync(ApplicationDbContext dbContext) =>
            dbContext.Database.ExecuteSqlRawAsync(CreateTableSql);
    }
}
