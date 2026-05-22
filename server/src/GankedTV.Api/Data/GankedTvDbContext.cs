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
    public DbSet<Follow> Follows => Set<Follow>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<ClipTag> ClipTags => Set<ClipTag>();

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
            // Hash and algo must be set together. CredentialAuthService maintains this
            // invariant by construction; the DB-level CHECK guards against manual UPDATEs
            // or future bugs that would otherwise produce un-verifiable rows.
            e.ToTable(t => t.HasCheckConstraint(
                "ck_users_password_hash_algo_paired",
                "(password_hash IS NULL AND password_algo IS NULL) "
                + "OR (password_hash IS NOT NULL AND password_algo IS NOT NULL)"));
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
