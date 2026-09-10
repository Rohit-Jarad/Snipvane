using Microsoft.EntityFrameworkCore;
using Snipvane.Models;

namespace Snipvane.Data;

/// <summary>
/// Provider-agnostic model. SQL Server is wired in Program.cs via UseSqlServer;
/// swapping to Npgsql or Azure SQL later should not require changing these entities.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Video> Videos => Set<Video>();
    public DbSet<Clip> Clips => Set<Clip>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Video>(entity =>
        {
            entity.HasKey(v => v.Id);
            entity.Property(v => v.OriginalFileName).HasMaxLength(512);
            entity.Property(v => v.StoredFilePath).HasMaxLength(1024);
            entity.Property(v => v.AudioFilePath).HasMaxLength(1024);
            entity.Property(v => v.ErrorMessage).HasMaxLength(4000);
            entity.Property(v => v.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(v => v.Status);
            entity.HasIndex(v => v.CreatedAt);
            entity.HasMany(v => v.Clips)
                .WithOne(c => c.Video)
                .HasForeignKey(c => c.VideoId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Clip>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Title).HasMaxLength(256);
            entity.Property(c => c.Reason).HasMaxLength(2000);
            entity.Property(c => c.FilePath).HasMaxLength(1024);
            entity.HasIndex(c => c.VideoId);
            entity.HasIndex(c => new { c.VideoId, c.SortOrder });
        });
    }
}
