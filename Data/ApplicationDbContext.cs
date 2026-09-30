using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ShopifyProductApp.Models;

namespace ShopifyProductApp.Data
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }

        public DbSet<GeneralSetting> GeneralSettings { get; set; }
        public DbSet<ProcessedOrder> ProcessedOrders { get; set; } // ← Bunu ekle
        public DbSet<StockSyncLog> StockSyncLogs { get; set; }
        public DbSet<PriceSyncLog> PriceSyncLogs { get; set; }
        public DbSet<CustomerSyncLog> CustomerSyncLogs { get; set; }
        public DbSet<PendingOperation> PendingOperations { get; set; } // bekleyen işlem havuzu (token yokken aktarılamayan siparişler)

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // GeneralSettings konfigürasyonu
            modelBuilder.Entity<GeneralSetting>(entity =>
            {
                entity.HasIndex(e => e.Key).IsUnique();
                entity.HasIndex(e => e.Category);
            });

            // ← ProcessedOrder konfigürasyonu ekle
            modelBuilder.Entity<ProcessedOrder>(entity =>
            {
                entity.HasKey(e => e.ShopifyOrderId); // Primary Key
                entity.HasIndex(e => e.ProcessedAt);   // Temizleme için index
            });

            // StockSyncLog konfigürasyonu
            modelBuilder.Entity<StockSyncLog>(entity =>
            {
                entity.HasIndex(e => e.ProductCode);
                entity.HasIndex(e => e.UpdatedAt);
                entity.Property(e => e.Price).HasColumnType("decimal(18,2)");
            });

            // PriceSyncLog konfigürasyonu
            modelBuilder.Entity<PriceSyncLog>(entity =>
            {
                entity.HasIndex(e => e.ProductCode);
                entity.HasIndex(e => e.UpdatedAt);
                entity.Property(e => e.OldPrice).HasColumnType("decimal(18,2)");
                entity.Property(e => e.NewPrice).HasColumnType("decimal(18,2)");
            });

            // PendingOperation (bekleyen işlem havuzu) konfigürasyonu
            modelBuilder.Entity<PendingOperation>(entity =>
            {
                entity.HasIndex(e => e.ShopifyOrderId).IsUnique(); // bir sipariş havuzda bir kez
                entity.HasIndex(e => e.Status);
                entity.HasIndex(e => e.CreatedAt);
            });

            // CustomerSyncLog konfigürasyonu
            modelBuilder.Entity<CustomerSyncLog>(entity =>
            {
                entity.HasIndex(e => e.Email);
                entity.HasIndex(e => e.ExactCustomerId); // upsert anahtarı (email'den önce gelir)
                entity.HasIndex(e => e.UpdatedAt);
            });
        }
    }
}