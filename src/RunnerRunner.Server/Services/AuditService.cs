using Shiny.DocumentDb;
using RunnerRunner.Core.Models;
using System.Security.Claims;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Simple audit logging service that persists events to the document store.
/// </summary>
public class AuditService
{
    private readonly IDocumentStore _store;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuditService> _logger;

    public AuditService(
        IDocumentStore store,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AuditService> logger)
    {
        _store = store;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task LogAsync(string action, string entityType, string? entityId = null, string? details = null)
    {
        var entry = new AuditLogEntry
        {
            Action = action,
            EntityType = entityType,
            EntityId = Guid.TryParse(entityId, out var parsedEntityId) ? parsedEntityId : null,
            Details = details,
            UserName = GetCurrentUserName(),
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

    private string? GetCurrentUserName()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
            return null;

        return user.Identity.Name
            ?? user.FindFirstValue(ClaimTypes.Email)
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
