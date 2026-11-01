using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>
/// Integration tests for <see cref="EmailDeletionService"/>: every deleted email gets its own
/// Deletion audit row, and audit rows are only kept when the delete itself succeeds.
/// The service resolves the test's shared context from every scope, so all writes happen
/// inside the test transaction and are rolled back afterwards (locked emails could not be
/// cleaned up otherwise, the compliance trigger refuses to delete them).
/// </summary>
[Collection(TestDbFixture.CollectionName)]
public class EmailDeletionServiceTests
{
    private readonly TestDbFixture _fixture;

    public EmailDeletionServiceTests(TestDbFixture fixture) => _fixture = fixture;

    private static EmailDeletionService CreateService(MailArchiverDbContext sharedContext)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sharedContext);
        services.AddSingleton(Options.Create(new DeletionPolicyOptions()));
        var provider = services.BuildServiceProvider();

        return new EmailDeletionService(
            NullLogger<EmailDeletionService>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static async Task<MailAccount> SeedAccountAsync(MailArchiverDbContext ctx)
    {
        var account = new MailAccount
        {
            Name = $"del-{Guid.NewGuid():N}".Substring(0, 25),
            EmailAddress = $"{Guid.NewGuid():N}@test.local",
            Provider = ProviderType.IMPORT,
            IsEnabled = false,
            LastSync = DateTime.UtcNow
        };
        ctx.MailAccounts.Add(account);
        await ctx.SaveChangesAsync();
        return account;
    }

    private static ArchivedEmail NewEmail(int accountId, string subject, bool isLocked = false) => new()
    {
        MailAccountId = accountId,
        MessageId = $"<{Guid.NewGuid():N}@test.local>",
        Subject = subject,
        Body = "b", HtmlBody = string.Empty,
        From = "from@test.local", To = "to@test.local", Cc = string.Empty, Bcc = string.Empty,
        FolderName = "INBOX",
        IsLocked = isLocked
    };

    /// <summary>
    /// Runs the background loop until the queued job finishes. Only the in-memory job is
    /// polled: the service shares the test connection, which allows one command at a time.
    /// </summary>
    private static async Task<EmailDeletionJob> RunJobAsync(EmailDeletionService service, EmailDeletionJob job)
    {
        await service.StartAsync(CancellationToken.None);
        try
        {
            service.QueueJob(job);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!job.IsCompleted && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
            return job;
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DeleteJob_WritesOneDeletionAuditRowPerEmail()
    {
        var username = $"del-user-{Guid.NewGuid():N}";
        await using var scope = await _fixture.CreateTransactionalContextAsync();
        var ctx = scope.Context;
        try
        {
            var account = await SeedAccountAsync(ctx);
            var longSubject = new string('s', 300);
            var toDelete = new[]
            {
                NewEmail(account.Id, "first"),
                NewEmail(account.Id, "second"),
                NewEmail(account.Id, longSubject)
            };
            var keep = NewEmail(account.Id, "keep");
            ctx.ArchivedEmails.AddRange(toDelete);
            ctx.ArchivedEmails.Add(keep);
            await ctx.SaveChangesAsync();
            var deleteIds = toDelete.Select(e => e.Id).ToList();

            var job = await RunJobAsync(CreateService(ctx),
                new EmailDeletionJob { EmailIds = deleteIds, UserId = username });

            Assert.Equal(EmailDeletionJobStatus.Completed, job.Status);
            Assert.Equal(3, job.DeletedEmails);

            ctx.ChangeTracker.Clear();
            var logs = await ctx.AccessLogs.AsNoTracking()
                .Where(l => l.Username == username)
                .ToListAsync();
            Assert.Equal(3, logs.Count);
            Assert.All(logs, l =>
            {
                Assert.Equal(AccessLogType.Deletion, l.Type);
                Assert.Equal(account.Id, l.MailAccountId);
                Assert.Equal("from@test.local", l.EmailFrom);
            });
            Assert.Equal(deleteIds.OrderBy(id => id), logs.Select(l => l.EmailId!.Value).OrderBy(id => id));
            Assert.Equal(255, logs.Single(l => l.EmailId == toDelete[2].Id).EmailSubject!.Length);

            Assert.False(await ctx.ArchivedEmails.AnyAsync(e => deleteIds.Contains(e.Id)));
            Assert.True(await ctx.ArchivedEmails.AnyAsync(e => e.Id == keep.Id));
        }
        finally { await scope.RollbackAsync(); }
    }

    [Fact]
    public async Task DeleteJob_WhenDeleteFails_WritesNoAuditRows()
    {
        var username = $"del-user-{Guid.NewGuid():N}";
        await using var scope = await _fixture.CreateTransactionalContextAsync();
        var ctx = scope.Context;
        try
        {
            var account = await SeedAccountAsync(ctx);
            var unlocked = NewEmail(account.Id, "unlocked");
            var locked = NewEmail(account.Id, "locked", isLocked: true);
            ctx.ArchivedEmails.AddRange(unlocked, locked);
            await ctx.SaveChangesAsync();
            var ids = new List<int> { unlocked.Id, locked.Id };

            var job = await RunJobAsync(CreateService(ctx),
                new EmailDeletionJob { EmailIds = ids, UserId = username });

            // The compliance trigger refuses to delete the locked email, which fails the batch
            Assert.Equal(EmailDeletionJobStatus.Failed, job.Status);

            ctx.ChangeTracker.Clear();
            Assert.Equal(2, await ctx.ArchivedEmails.CountAsync(e => ids.Contains(e.Id)));
            Assert.False(await ctx.AccessLogs.AnyAsync(l => l.Username == username));
        }
        finally { await scope.RollbackAsync(); }
    }
}
