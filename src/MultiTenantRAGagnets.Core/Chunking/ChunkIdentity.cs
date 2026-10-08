using System.Security.Cryptography;
using System.Text;

namespace MultiTenantRAGagnets.Core.Chunking;

/// <summary>
/// Deterministic chunk ids: a name-based UUID of (document id, version, chunk index).
/// Lets the eval manifest state expected chunk ids BEFORE ingestion, and lets re-ingesting
/// the same document version be idempotent.
/// </summary>
public static class ChunkIdentity
{
    public static Guid For(Guid documentId, int version, int chunkIndex)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"chunk|{documentId:D}|{version}|{chunkIndex}"));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50); // version 5-style
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes);
    }
}
