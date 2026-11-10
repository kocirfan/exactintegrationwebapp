using Microsoft.EntityFrameworkCore;

namespace ShopifyProductApp.Data
{
    /// <summary>
    /// OrderFulfillmentSyncs tablosu: Exact'ta teslim edilen (Status=21 Complete) siparişlerin
    /// Shopify'da fulfilled yapılma takibi. Her Shopify siparişi için tek satır.
    ///
    /// EnsureCreated mevcut DB'ye yeni tablo eklemediği için (bkz. PendingOperationsSchema)
    /// tablo açılışta idempotent SQL ile oluşturulur; varsa dokunulmaz. EF modeline eklenmedi,
    /// servis ham SQL ile okur/yazar.
    /// </summary>
    public static class OrderFulfillmentSyncSchema
    {
        public const string CreateTableSql = @"
IF OBJECT_ID(N'[OrderFulfillmentSyncs]', N'U') IS NULL
BEGIN
    CREATE TABLE [OrderFulfillmentSyncs] (
        [ShopifyOrderId] bigint NOT NULL,
        [ShopifyOrderNumber] bigint NULL,
        [ShopifyOrderName] nvarchar(50) NULL,
        [ExactOrderGuid] uniqueidentifier NOT NULL,
        [ExactOrderNumber] nvarchar(50) NULL,
        [Status] nvarchar(30) NOT NULL,
        [ExactStatus] int NULL,
        [Reason] nvarchar(500) NULL,
        [LastError] nvarchar(2000) NULL,
        [AttemptCount] int NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [LastCheckedAt] datetime2 NULL,
        [CompletedAt] datetime2 NULL,
        CONSTRAINT [PK_OrderFulfillmentSyncs] PRIMARY KEY ([ShopifyOrderId])
    );
    CREATE INDEX [IX_OrderFulfillmentSyncs_Status] ON [OrderFulfillmentSyncs] ([Status]);
    CREATE INDEX [IX_OrderFulfillmentSyncs_CreatedAt] ON [OrderFulfillmentSyncs] ([CreatedAt]);
END";

        /// <summary>Tablo yoksa oluşturur; varsa dokunmaz.</summary>
        public static Task EnsureTableAsync(ApplicationDbContext dbContext) =>
            dbContext.Database.ExecuteSqlRawAsync(CreateTableSql);
    }
}
