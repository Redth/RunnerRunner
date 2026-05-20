using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace RunnerRunner.Server.Data.Auth;

public class RunnerRunnerAuthDbContext(DbContextOptions<RunnerRunnerAuthDbContext> options)
    : IdentityDbContext<RunnerRunnerUser>(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<RunnerRunnerUser>(entity =>
        {
            entity.Property(x => x.DisplayName).HasMaxLength(256);
            entity.Property(x => x.Source).HasMaxLength(32);
            entity.Property(x => x.ExternalIssuer).HasMaxLength(512);
            entity.Property(x => x.ExternalSubject).HasMaxLength(512);
            entity.HasIndex(x => new { x.ExternalIssuer, x.ExternalSubject });
        });
    }
}
