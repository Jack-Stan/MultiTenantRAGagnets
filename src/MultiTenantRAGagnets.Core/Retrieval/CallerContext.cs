namespace MultiTenantRAGagnets.Core.Retrieval;

/// <summary>
/// Who is asking. Built ONLY from a verified JWT, never from request input
/// (db/README.md: "The tenant/role values come from the verified JWT only").
/// </summary>
public sealed record CallerContext(Guid TenantId, Guid UserId, string Role)
{
    public void Validate()
    {
        if (TenantId == Guid.Empty) throw new ArgumentException("TenantId must not be empty.", nameof(TenantId));
        if (UserId == Guid.Empty) throw new ArgumentException("UserId must not be empty.", nameof(UserId));
        if (string.IsNullOrWhiteSpace(Role)) throw new ArgumentException("Role must not be empty.", nameof(Role));
    }
}

/// <summary>
/// Per-request holder for the authenticated caller, so request-scoped services that implement
/// context-free interfaces (e.g. <c>IChunkWriter</c>) still know whose tenant/role to set.
/// Registered as scoped; the API sets it once, after the JWT is verified.
/// </summary>
public sealed class CallerAccessor
{
    private CallerContext? _current;

    public CallerContext Current =>
        _current ?? throw new InvalidOperationException("No authenticated caller has been set for this request.");

    public void Set(CallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Validate();
        _current = caller;
    }
}
