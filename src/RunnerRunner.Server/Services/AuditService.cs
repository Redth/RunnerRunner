using Shiny.DocumentDb;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Simple audit logging service that persists events to the document store.
/// </summary>
public class AuditService
{
    private readonly IDocumentStore _store;
    private readonly ILogger<AuditService> _logger;

    public AuditService(IDocumentStore store, ILogger<AuditService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task LogAsync(string action, string entityType, string? entityId = null, string? details = null)
    {
        var entry = new AuditLogEntry
        {
            Action = action,
            EntityType = entityType,
            EntityId = entityId != null ? Guid.Parse(entityId) : null,
            Details = details,
            Timestamp = DateTime.UtcNow
        };

        await _store.Insert(entry);
        _logger.LogInformation("Audit: {Action} {EntityType} {EntityId} - {Details}",
            action, entityType, entityId, details);
    }

    public async Task<List<AuditLogEntry>> GetRecentAsync(int count = 50)
    {
        return (await _store.Query<AuditLogEntry>()
            .OrderByDescending(e => e.Timestamp)
            .Paginate(0, count)
            .ToList()).ToList();
    }
}
