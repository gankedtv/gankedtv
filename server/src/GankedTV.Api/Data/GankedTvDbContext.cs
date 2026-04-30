using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Data;

public class GankedTvDbContext(DbContextOptions<GankedTvDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Game> Games => Set<Game>();
    public DbSet<Clip> Clips => Set<Clip>();
    public DbSet<Like> Likes => Set<Like>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

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
            e.HasIndex(g => g.Slug).IsUnique().HasDatabaseName("idx_games_slug");
            e.HasIndex(g => g.Name).HasDatabaseName("idx_games_name");
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
