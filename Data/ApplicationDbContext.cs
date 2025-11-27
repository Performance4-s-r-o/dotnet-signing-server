using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DotNetSigningServer.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

        public DbSet<SigningData> SigningData { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<ApiToken> ApiTokens { get; set; }
        public DbSet<Document> Documents { get; set; }
        public DbSet<UsageRecord> UsageRecords { get; set; }
        public DbSet<PricingPlan> PricingPlans { get; set; }
        public DbSet<Invoice> Invoices { get; set; }
        public DbSet<Payment> Payments { get; set; }
        public DbSet<WebhookEvent> WebhookEvents { get; set; }
        public DbSet<StoredPdfTemplate> StoredPdfTemplates { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            if (Database.IsSqlite())
            {
                var dateTimeOffsetConverter = new DateTimeOffsetToBinaryConverter();
                foreach (var entityType in modelBuilder.Model.GetEntityTypes())
                {
                    var properties = entityType.ClrType.GetProperties()
                        .Where(p => p.PropertyType == typeof(DateTimeOffset) || p.PropertyType == typeof(DateTimeOffset?));

                    foreach (var property in properties)
                    {
                        modelBuilder.Entity(entityType.Name)
                            .Property(property.Name)
                            .HasConversion(dateTimeOffsetConverter);
                    }
                }
            }

            modelBuilder.Entity<User>()
                .HasIndex(u => u.Email)
                .IsUnique();

            modelBuilder.Entity<ApiToken>()
                .HasIndex(t => t.TokenHash);

            modelBuilder.Entity<ApiToken>()
                .HasOne(t => t.User)
                .WithMany(u => u.ApiTokens)
                .HasForeignKey(t => t.UserId);

            modelBuilder.Entity<Document>()
                .HasOne(d => d.User)
                .WithMany(u => u.Documents)
                .HasForeignKey(d => d.UserId);

            modelBuilder.Entity<UsageRecord>()
                .HasIndex(u => new { u.UserId, u.CreatedAt });

            modelBuilder.Entity<UsageRecord>()
                .HasOne(r => r.User)
                .WithMany(u => u.UsageRecords)
                .HasForeignKey(r => r.UserId);

            modelBuilder.Entity<UsageRecord>()
                .HasOne(r => r.Document)
                .WithMany(d => d.UsageRecords)
                .HasForeignKey(r => r.DocumentId);

            modelBuilder.Entity<Invoice>()
                .HasIndex(i => i.StripeInvoiceId);

            modelBuilder.Entity<Invoice>()
                .HasOne(i => i.User)
                .WithMany(u => u.Invoices)
                .HasForeignKey(i => i.UserId);

            modelBuilder.Entity<Payment>()
                .HasIndex(p => p.StripePaymentIntentId);

            modelBuilder.Entity<Payment>()
                .HasOne(p => p.User)
                .WithMany(u => u.Payments)
                .HasForeignKey(p => p.UserId);

            modelBuilder.Entity<Payment>()
                .HasOne(p => p.Invoice)
                .WithMany(i => i.Payments)
                .HasForeignKey(p => p.InvoiceId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<WebhookEvent>()
                .HasIndex(w => w.EventId)
                .IsUnique();
        }
    }
}
