using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Outbox;
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
        public DbSet<LegalDocument> LegalDocuments { get; set; }
        public DbSet<BackofficeOutboxItem> BackofficeOutboxItems { get; set; }
        public DbSet<BackofficeWebhookInboxItem> BackofficeWebhookInboxItems { get; set; }
        public DbSet<BackofficeState> BackofficeStates { get; set; }
        public DbSet<ConsentRecord> ConsentRecords { get; set; }
        public DbSet<ApiOperation> ApiOperations { get; set; }
        public DbSet<OperatorSwitch> OperatorSwitches { get; set; }
        public DbSet<AdminAuditEntry> AdminAuditEntries { get; set; }
        public DbSet<AuthAttemptCounter> AuthAttemptCounters { get; set; }

        /// <summary>
        /// Outbox items added through this context and not yet announced to the dispatcher;
        /// see <see cref="OutboxSignalInterceptor"/>.
        /// </summary>
        internal OutboxPendingSignals OutboxPendingSignals { get; } = new();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Wakes the outbox dispatcher once enqueued items are committed. Stateless and
            // shared, so it does not change the options' identity between contexts.
            optionsBuilder.AddInterceptors(OutboxSignalInterceptor.Instance);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            if (Database.IsNpgsql())
            {
                modelBuilder.HasDefaultSchema("dotnet_signing");
            }

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

            // One user per Stripe customer (#82): the customer drives the billing portal,
            // invoices and the auto-recharge charge. Blank values are not a customer.
            modelBuilder.Entity<User>()
                .HasIndex(u => u.StripeCustomerId)
                .HasDatabaseName(DotNetSigningServer.Services.Billing.StripeCustomerBinding.UniqueIndexName)
                .IsUnique()
                .HasFilter("\"StripeCustomerId\" IS NOT NULL AND btrim(\"StripeCustomerId\") <> ''");

            // One row per (slug, locale, version); the lookup then picks the
            // newest non-draft row whose EffectiveFrom has already passed.
            modelBuilder.Entity<LegalDocument>()
                .HasIndex(d => new { d.Slug, d.Locale, d.Version })
                .IsUnique();

            // Equality columns first, the range column last — matches how
            // LegalDocumentService filters and orders.
            modelBuilder.Entity<LegalDocument>()
                .HasIndex(d => new { d.Slug, d.Locale, d.IsDraft, d.EffectiveFrom });

            // Existing rows become 'manual' through the column default.
            modelBuilder.Entity<LegalDocument>()
                .Property(d => d.Source)
                .HasDefaultValue(LegalDocumentSources.Manual);

            modelBuilder.Entity<LegalDocument>()
                .Property(d => d.ContentHash)
                .IsFixedLength();

            modelBuilder.Entity<ApiToken>()
                .HasIndex(t => t.TokenHash);

            modelBuilder.Entity<ApiToken>()
                .HasIndex(t => t.TokenPrefix);

            modelBuilder.Entity<ApiToken>()
                .HasOne(t => t.User)
                .WithMany(u => u.ApiTokens)
                .HasForeignKey(t => t.UserId);

            modelBuilder.Entity<SigningData>()
                .Property(s => s.UserId)
                .HasColumnType("uuid");

            modelBuilder.Entity<Document>()
                .HasOne(d => d.User)
                .WithMany(u => u.Documents)
                .HasForeignKey(d => d.UserId);

            modelBuilder.Entity<UsageRecord>()
                .HasIndex(u => new { u.UserId, u.CreatedAt });

            modelBuilder.Entity<UsageRecord>()
                .HasIndex(u => new { u.UserId, u.Status, u.CreatedAt });

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

            // The dispatcher's claim filters by status and due time; cleanup and the
            // admin overview go by age.
            modelBuilder.Entity<BackofficeOutboxItem>()
                .HasIndex(i => new { i.Status, i.NextAttemptAt });

            modelBuilder.Entity<BackofficeOutboxItem>()
                .HasIndex(i => i.CreatedAt);

            // Dedupe of webhook deliveries and polled events (INSERT … ON CONFLICT DO NOTHING).
            modelBuilder.Entity<BackofficeWebhookInboxItem>()
                .HasIndex(i => i.WebhookId)
                .IsUnique();

            // The processor reads unprocessed items that are due; cleanup goes by ProcessedAt.
            modelBuilder.Entity<BackofficeWebhookInboxItem>()
                .HasIndex(i => i.NextAttemptAt)
                .HasFilter("\"ProcessedAt\" IS NULL");

            modelBuilder.Entity<BackofficeWebhookInboxItem>()
                .HasIndex(i => i.ProcessedAt);

            // The re-consent gate reads a user's newest record per document.
            modelBuilder.Entity<ConsentRecord>()
                .HasIndex(c => new { c.UserId, c.Document, c.OccurredAt });

            // Reconciliation and the backfill go by time.
            modelBuilder.Entity<ConsentRecord>()
                .HasIndex(c => c.OccurredAt);

            modelBuilder.Entity<ConsentRecord>()
                .Property(c => c.ContentHash)
                .IsFixedLength();

            // The caller's Idempotency-Key is unique per account (#77).
            modelBuilder.Entity<ApiOperation>()
                .HasIndex(o => new { o.UserId, o.OperationId })
                .IsUnique();

            // One operation per presign: only one /api/sign can ever consume it.
            modelBuilder.Entity<ApiOperation>()
                .HasIndex(o => o.PresignId)
                .IsUnique()
                .HasFilter("\"PresignId\" IS NOT NULL");

            // The maintenance sweep: stale leases, result purge, row retention.
            modelBuilder.Entity<ApiOperation>()
                .HasIndex(o => new { o.State, o.LeaseUntil });

            modelBuilder.Entity<ApiOperation>()
                .HasIndex(o => o.UpdatedAt);

            // Admin audit (#80): read per target account, newest first.
            modelBuilder.Entity<AdminAuditEntry>()
                .HasIndex(a => new { a.TargetUserId, a.CreatedAt });

            modelBuilder.Entity<AdminAuditEntry>()
                .Property(a => a.Change)
                .HasColumnType(Database.IsNpgsql() ? "jsonb" : null);

            modelBuilder.Entity<AuthAttemptCounter>()
                .HasIndex(c => c.WindowStart);
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            ApplySecurityRules();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            ApplySecurityRules();
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        /// <summary>
        /// Two rules no write path may forget (#80):
        /// <list type="bullet">
        /// <item>A change of <c>IsActive</c> or <c>IsAdmin</c> moves the session stamp
        /// (<c>UpdatedAt</c>), whoever makes it — the user, an admin, a background job. The cookie
        /// check also reads both flags directly, so even a change made in SQL takes effect.</item>
        /// <item>Admin audit rows are append-only.</item>
        /// </list>
        /// </summary>
        private void ApplySecurityRules()
        {
            foreach (var entry in ChangeTracker.Entries<User>())
            {
                if (entry.State != EntityState.Modified) continue;
                if (entry.Property(u => u.IsActive).IsModified || entry.Property(u => u.IsAdmin).IsModified)
                {
                    // Whole microseconds (what PostgreSQL keeps), and strictly later than before: two
                    // writes within one microsecond must still give a new stamp.
                    var now = DateTimeOffset.UtcNow;
                    var stamp = now.AddTicks(-(now.Ticks % 10));
                    var original = entry.Property(u => u.UpdatedAt).OriginalValue;
                    if (stamp <= original)
                    {
                        stamp = original.AddTicks(10 - (original.Ticks % 10));
                    }
                    entry.Entity.UpdatedAt = stamp;
                }
            }

            foreach (var entry in ChangeTracker.Entries<AdminAuditEntry>())
            {
                if (entry.State is EntityState.Modified or EntityState.Deleted)
                {
                    throw new InvalidOperationException("Admin audit entries are append-only.");
                }
            }
        }
    }
}
