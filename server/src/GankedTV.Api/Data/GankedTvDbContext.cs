using GankedTV.Api.Data.Entities;
using GankedTV.Api.Validation;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Data;

public class GankedTvDbContext(DbContextOptions<GankedTvDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Game> Games => Set<Game>();
    public DbSet<Clip> Clips => Set<Clip>();
    public DbSet<Like> Likes => Set<Like>();
    public DbSet<ClipView> ClipViews => Set<ClipView>();
    public DbSet<Follow> Follows => Set<Follow>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<ClipTag> ClipTags => Set<ClipTag>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ClipStreamJob> ClipStreamJobs => Set<ClipStreamJob>();
    public DbSet<Report> Reports => Set<Report>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(u => u.Username).HasMaxLength(30);
            e.Property(u => u.Email).HasMaxLength(255);
            e.Property(u => u.DiscordId).HasMaxLength(50);
            e.Property(u => u.GoogleId).HasMaxLength(50);
            e.Property(u => u.PasswordHash).HasMaxLength(255);
            e.Property(u => u.PasswordAlgo).HasMaxLength(32);
            e.Property(u => u.Role).HasMaxLength(16).HasDefaultValue(UserRoles.User);
            e.Property(u => u.BannedReason).HasMaxLength(500);
            // Hash and algo must be set together. CredentialAuthService maintains this
            // invariant by construction; the DB-level CHECK guards against manual UPDATEs
            // or future bugs that would otherwise produce un-verifiable rows.
            e.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "ck_users_password_hash_algo_paired",
                    "(password_hash IS NULL AND password_algo IS NULL) "
                    + "OR (password_hash IS NOT NULL AND password_algo IS NOT NULL)");
                t.HasCheckConstraint(
                    "ck_users_role",
                    "role IN ('user','moderator','admin')");
            });
            e.Property(u => u.Bio).HasMaxLength(500);
            e.Property(u => u.CreatedAt).HasDefaultValueSql("now()");
            e.Property(u => u.UpdatedAt).HasDefaultValueSql("now()");
            e.HasIndex(u => u.Username).IsUnique().HasDatabaseName("idx_users_username");
            e.HasIndex(u => u.Email).IsUnique().HasDatabaseName("idx_users_email");
            e.HasIndex(u => u.DiscordId).IsUnique().HasDatabaseName("idx_users_discord_id");
            e.HasIndex(u => u.GoogleId).IsUnique().HasDatabaseName("idx_users_google_id");
        });

        modelBuilder.Entity<Game>(e =>
        {
            e.HasKey(g => g.Id);
            e.Property(g => g.Name).HasMaxLength(255);
            e.Property(g => g.Slug).HasMaxLength(255);
            e.Property(g => g.Tag).HasMaxLength(16);
            e.Property(g => g.CoverImageId).HasMaxLength(64);
            e.Property(g => g.IgdbManaged).HasDefaultValue(false);
            e.HasIndex(g => g.Slug).IsUnique().HasDatabaseName("idx_games_slug");
            e.HasIndex(g => g.Name).HasDatabaseName("idx_games_name");
            // Full-text search vector, stored generated column. EF emits ALTER TABLE …
            // GENERATED ALWAYS AS … STORED so the value is maintained by Postgres on every
            // INSERT/UPDATE of name. Companion GIN index makes `@@` lookups index-driven.
            e.Property(g => g.SearchVector)
                .HasComputedColumnSql(
                    "to_tsvector('simple', coalesce(name, ''))",
                    stored: true);
            e.HasIndex(g => g.SearchVector)
                .HasMethod("GIN")
                .HasDatabaseName("idx_games_search_vector");
            e.HasData(
                new Game { Id = 1, Name = "League of Legends", Slug = "league-of-legends", Tag = "LOL" },
                new Game { Id = 2, Name = "Valorant", Slug = "valorant", Tag = "VALORANT" },
                new Game { Id = 3, Name = "Counter-Strike 2", Slug = "cs2", Tag = "CS2" },
                new Game { Id = 4, Name = "Fortnite", Slug = "fortnite", Tag = "FN" },
                new Game { Id = 5, Name = "Apex Legends", Slug = "apex-legends", Tag = "APEX" },
                new Game { Id = 6, Name = "Rocket League", Slug = "rocket-league", Tag = "RL" },
                new Game { Id = 7, Name = "Overwatch 2", Slug = "overwatch-2", Tag = "OW2" },
                new Game { Id = 8, Name = "Dota 2", Slug = "dota-2", Tag = "DOTA2" },
                new Game { Id = 9, Name = "Marvel Rivals", Slug = "marvel-rivals", Tag = "RIVALS" });
        });

        modelBuilder.Entity<Clip>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(c => c.Title).HasMaxLength(255);
            e.Property(c => c.Status).HasMaxLength(20).HasDefaultValue("processing");
            e.Property(c => c.Visibility).HasMaxLength(20).HasDefaultValue("public");
            e.Property(c => c.ViewCount).HasDefaultValue(0);
            e.Property(c => c.LikeCount).HasDefaultValue(0);
            e.Property(c => c.ProcessingAttempts).HasDefaultValue(0);
            e.Property(c => c.CreatedAt).HasDefaultValueSql("now()");
            e.Property(c => c.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne(c => c.User)
                .WithMany(u => u.Clips)
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(c => c.Game)
                .WithMany()
                .HasForeignKey(c => c.GameId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(c => c.UserId).HasDatabaseName("idx_clips_user_id");
            e.HasIndex(c => c.GameId).HasDatabaseName("idx_clips_game_id");
            e.HasIndex(c => c.CreatedAt).IsDescending().HasDatabaseName("idx_clips_created_at");
            e.HasIndex(c => c.Status).HasFilter("status = 'ready'").HasDatabaseName("idx_clips_status");
            // Drives the orphan-clip sweep query (status = 'draft' AND created_at < cutoff).
            // Composite key so EF distinguishes it from idx_clips_created_at; partial filter
            // keeps the index tiny — only a transient minority of rows are ever 'draft'.
            e.HasIndex(c => new { c.Status, c.CreatedAt })
                .HasFilter("status = 'draft'")
                .HasDatabaseName("idx_clips_draft_created_at");
            // Drives the media-job worker's claim query. UpdatedAt orders rows so the
            // oldest stuck row is picked first; the partial filter keeps the index
            // size bounded by the in-flight queue, not the whole clips table.
            e.HasIndex(c => new { c.Status, c.UpdatedAt })
                .HasFilter("status = 'processing'")
                .HasDatabaseName("idx_clips_processing_updated_at");
            // NOTE: the sibling partial index idx_clips_transcoding_updated_at (drives the
            // compress stage's claim query) is created via raw SQL in the
            // AddVideoCompressionAndStreamJobs migration — EF keys indexes by column set, so a
            // second HasIndex on { Status, UpdatedAt } would overwrite the 'processing' one
            // rather than add a second partial index. It is intentionally not modeled here.
            e.HasIndex(c => c.ShareCode).IsUnique().HasDatabaseName("idx_clips_share_code");
            e.Property(c => c.ShareCode).HasMaxLength(12);
            // Title gets weight 'A' so exact title matches outrank description-only hits;
            // description gets weight 'B'. Both go through `to_tsvector('simple', …)`, which
            // skips stemming/stopwords — fine for short, noun-heavy clip titles ("ace clutch",
            // game names, player tags). Stored generated → maintained by Postgres on every write.
            e.Property(c => c.SearchVector)
                .HasComputedColumnSql(
                    "setweight(to_tsvector('simple', coalesce(title, '')), 'A') || "
                    + "setweight(to_tsvector('simple', coalesce(description, '')), 'B')",
                    stored: true);
            e.HasIndex(c => c.SearchVector)
                .HasMethod("GIN")
                .HasDatabaseName("idx_clips_search_vector");
        });

        modelBuilder.Entity<ClipStreamJob>(e =>
        {
            // One JIT rendition job per clip — the clip id is the natural key, so concurrent
            // /stream requests for the same clip touch one row instead of stacking duplicates.
            e.HasKey(j => j.ClipId);
            e.Property(j => j.Status).HasMaxLength(20).HasDefaultValue(ClipStreamJobStatuses.Pending);
            e.Property(j => j.ProcessingAttempts).HasDefaultValue(0);
            e.Property(j => j.CreatedAt).HasDefaultValueSql("now()");
            e.Property(j => j.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne(j => j.Clip)
                .WithMany()
                .HasForeignKey(j => j.ClipId)
                .OnDelete(DeleteBehavior.Cascade);

            // Drives the JIT worker's claim query (pending rows, oldest first). Partial filter
            // keeps it scoped to the in-flight queue.
            e.HasIndex(j => new { j.Status, j.UpdatedAt })
                .HasFilter("status = 'pending'")
                .HasDatabaseName("idx_clip_stream_jobs_pending_updated_at");
        });

        modelBuilder.Entity<Like>(e =>
        {
            e.HasKey(l => new { l.UserId, l.ClipId });
            e.Property(l => l.CreatedAt).HasDefaultValueSql("now()");

            e.HasOne(l => l.User)
                .WithMany(u => u.Likes)
                .HasForeignKey(l => l.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(l => l.Clip)
                .WithMany(c => c.Likes)
                .HasForeignKey(l => l.ClipId)
                .OnDelete(DeleteBehavior.Cascade);

            // Backs the time-window like aggregation used by the trending feed
            // (likes_in_window per clip). PK is (user_id, clip_id), so without this
            // a 24h-window scan would table-scan likes.
            e.HasIndex(l => new { l.CreatedAt, l.ClipId })
                .IsDescending(true, false)
                .HasDatabaseName("idx_likes_created_at_clip_id");
        });

        modelBuilder.Entity<ClipView>(e =>
        {
            e.HasKey(v => v.Id);
            e.Property(v => v.Id).UseIdentityByDefaultColumn();
            e.Property(v => v.CreatedAt).HasDefaultValueSql("now()");

            e.HasOne(v => v.Clip)
                .WithMany()
                .HasForeignKey(v => v.ClipId)
                .OnDelete(DeleteBehavior.Cascade);

            // Time-window scan (views_in_window across all clips).
            e.HasIndex(v => v.CreatedAt)
                .IsDescending()
                .HasDatabaseName("idx_clip_views_created_at");
            // Per-clip aggregation (views_in_window for a specific clip).
            e.HasIndex(v => new { v.ClipId, v.CreatedAt })
                .IsDescending(false, true)
                .HasDatabaseName("idx_clip_views_clip_id_created_at");
        });

        modelBuilder.Entity<Tag>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Slug).HasMaxLength(24);
            e.Property(t => t.Name).HasMaxLength(24);
            e.Property(t => t.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(t => t.Slug).IsUnique().HasDatabaseName("idx_tags_slug");
        });

        modelBuilder.Entity<ClipTag>(e =>
        {
            e.HasKey(ct => new { ct.ClipId, ct.TagId });

            e.HasOne(ct => ct.Clip)
                .WithMany(c => c.ClipTags)
                .HasForeignKey(ct => ct.ClipId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(ct => ct.Tag)
                .WithMany(t => t.ClipTags)
                .HasForeignKey(ct => ct.TagId)
                .OnDelete(DeleteBehavior.Cascade);

            // Supports both the autocomplete (counting clips per tag) and the
            // GET /tags/{slug}/clips query (filtering by tag id then joining clips).
            e.HasIndex(ct => new { ct.TagId, ct.ClipId }).HasDatabaseName("idx_clip_tags_tag");
        });

        modelBuilder.Entity<Follow>(e =>
        {
            e.HasKey(f => new { f.FollowerId, f.FolloweeId });
            e.Property(f => f.CreatedAt).HasDefaultValueSql("now()");

            e.HasOne(f => f.Follower)
                .WithMany(u => u.Following)
                .HasForeignKey(f => f.FollowerId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(f => f.Followee)
                .WithMany(u => u.Followers)
                .HasForeignKey(f => f.FolloweeId)
                .OnDelete(DeleteBehavior.Cascade);

            // DB-level guarantee that prevents a self-follow even if the endpoint check
            // is bypassed (admin SQL, future bug). Mirrors the issue spec.
            e.ToTable(t => t.HasCheckConstraint(
                "ck_follows_no_self",
                "follower_id <> followee_id"));

            // Backs the followers list query (FolloweeId filter, CreatedAt desc keyset).
            e.HasIndex(f => new { f.FolloweeId, f.CreatedAt })
                .IsDescending(false, true)
                .HasDatabaseName("idx_follows_followee_created_at");
            // Backs the following list query (FollowerId filter) and the per-user feed
            // EXISTS check (FollowerId + FolloweeId is already covered by the PK, but the
            // PK is ordered FollowerId-first which makes the EXISTS lookup index-only).
            e.HasIndex(f => new { f.FollowerId, f.CreatedAt })
                .IsDescending(false, true)
                .HasDatabaseName("idx_follows_follower_created_at");
        });

        modelBuilder.Entity<Comment>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(c => c.Body).HasMaxLength(CommentValidationLimits.MaxBodyLength);
            e.Property(c => c.CreatedAt).HasDefaultValueSql("now()");
            e.Property(c => c.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne(c => c.Clip)
                .WithMany()
                .HasForeignKey(c => c.ClipId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(c => c.User)
                .WithMany(u => u.Comments)
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Self-referencing parent → replies. Cascade only fires on a real (hard) delete of
            // a parent row; the app soft-deletes (DeletedAt) so threads stay intact in practice.
            e.HasOne(c => c.Parent)
                .WithMany(c => c.Replies)
                .HasForeignKey(c => c.ParentId)
                .OnDelete(DeleteBehavior.Cascade);

            // Drives the top-level listing for a clip (ordered by created_at) and reply lookups.
            e.HasIndex(c => new { c.ClipId, c.CreatedAt }).HasDatabaseName("idx_comments_clip_id");
            e.HasIndex(c => new { c.ParentId, c.CreatedAt }).HasDatabaseName("idx_comments_parent_id");
            // EF auto-generates an index on the UserId FK; name it explicitly to match the
            // `idx_*` convention used elsewhere (otherwise it lands as `ix_comments_user_id`).
            e.HasIndex(c => c.UserId).HasDatabaseName("idx_comments_user_id");
        });

        modelBuilder.Entity<Notification>(e =>
        {
            e.HasKey(n => n.Id);
            e.Property(n => n.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(n => n.Type).HasMaxLength(20);
            e.Property(n => n.CreatedAt).HasDefaultValueSql("now()");

            // Two FKs to the same `users` table (recipient + actor): use parameterless WithMany()
            // so we don't have to carry two collections on User just for cascade configuration.
            e.HasOne(n => n.Recipient)
                .WithMany()
                .HasForeignKey(n => n.RecipientId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(n => n.Actor)
                .WithMany()
                .HasForeignKey(n => n.ActorId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting a clip clears any notifications anchored to it; same for a comment. Both
            // FKs are nullable since a `follow` notification has neither.
            e.HasOne(n => n.Clip)
                .WithMany()
                .HasForeignKey(n => n.ClipId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(n => n.Comment)
                .WithMany()
                .HasForeignKey(n => n.CommentId)
                .OnDelete(DeleteBehavior.Cascade);

            // Defence in depth: the service layer filters self-actions, but the DB-level CHECK
            // guards against a buggy future call site (mirrors ck_follows_no_self).
            e.ToTable(t => t.HasCheckConstraint(
                "ck_notifications_no_self",
                "actor_id <> recipient_id"));

            // Drives the recipient's listing (RecipientId filter, CreatedAt desc keyset).
            e.HasIndex(n => new { n.RecipientId, n.CreatedAt })
                .IsDescending(false, true)
                .HasDatabaseName("idx_notifications_recipient");

            // Partial index — only unread rows, so the unread-count probe stays bounded by the
            // tail of in-flight notifications even as the table grows.
            e.HasIndex(n => n.RecipientId)
                .HasFilter("read_at IS NULL")
                .HasDatabaseName("idx_notifications_unread");
        });

        modelBuilder.Entity<Report>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(r => r.TargetType).HasMaxLength(16);
            e.Property(r => r.Reason).HasMaxLength(32);
            e.Property(r => r.Note).HasMaxLength(2000);
            e.Property(r => r.Status).HasMaxLength(16).HasDefaultValue(ReportStatuses.Open);
            e.Property(r => r.CreatedAt).HasDefaultValueSql("now()");

            e.HasOne(r => r.Reporter)
                .WithMany()
                .HasForeignKey(r => r.ReporterId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(r => r.ResolvedByUser)
                .WithMany()
                .HasForeignKey(r => r.ResolvedBy)
                .OnDelete(DeleteBehavior.SetNull);

            // Enum-style domain values guarded at the DB level so a buggy future call site
            // can't write a status/type/reason that crashes hydration.
            e.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "ck_reports_target_type",
                    "target_type IN ('clip','comment','user')");
                t.HasCheckConstraint(
                    "ck_reports_status",
                    "status IN ('open','resolved','dismissed')");
                t.HasCheckConstraint(
                    "ck_reports_reason",
                    "reason IN ('spam','harassment','hate','nsfw','violence','wrong_game','other')");
            });

            // Drives the admin queue (status filter, newest first).
            e.HasIndex(r => new { r.Status, r.CreatedAt })
                .IsDescending(false, true)
                .HasDatabaseName("idx_reports_status_created_at");
            // Drives ResolveForTargetAsync (close all open reports for a moderated target).
            e.HasIndex(r => new { r.TargetType, r.TargetId })
                .HasDatabaseName("idx_reports_target");
            // Race-safe duplicate guard: only one open report per (reporter, target). Partial
            // filter on status='open' so the same user can still file a fresh report after a
            // prior one has been resolved/dismissed.
            e.HasIndex(r => new { r.ReporterId, r.TargetType, r.TargetId })
                .IsUnique()
                .HasFilter("status = 'open'")
                .HasDatabaseName("idx_reports_open_unique");
        });

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(t => t.TokenHash).IsRequired();
            e.Property(t => t.FamilyId).IsRequired();
            e.Property(t => t.CreatedAt).HasDefaultValueSql("now()");

            e.HasOne(t => t.User)
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(t => t.UserId).HasDatabaseName("idx_refresh_tokens_user_id");
            e.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("idx_refresh_tokens_token_hash");
            e.HasIndex(t => t.FamilyId).HasDatabaseName("idx_refresh_tokens_family_id");
        });
    }
}
