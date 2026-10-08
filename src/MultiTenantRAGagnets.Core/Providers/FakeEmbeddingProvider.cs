using System.Security.Cryptography;
using System.Text;

namespace MultiTenantRAGagnets.Core.Providers;

/// <summary>
/// Deterministic, keyless, real-shaped embedding for CI: SHA-256 of the text seeds a
/// stream of floats that is normalised to a unit vector of <see cref="Dimension"/>.
/// Same text always gives the same vector; different text gives a different one.
/// It carries no semantics, but it has the right shape, so pgvector and HNSW are
/// genuinely exercised without pulling a model.
/// </summary>
public sealed class FakeEmbeddingProvider : IEmbeddingProvider
{
    public FakeEmbeddingProvider() : this(EmbeddingDefaults.Dimension)
    {
    }

    public FakeEmbeddingProvider(int dimension)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dimension, 1);
        Dimension = dimension;
    }

    public string ModelName => "fake-embedding";

    public int Dimension { get; }

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Embed(text, Dimension));
    }

    public static float[] Embed(string text, int dimension = EmbeddingDefaults.Dimension)
    {
        ArgumentNullException.ThrowIfNull(text);

        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var vector = new float[dimension];
        double sumSquares = 0;

        // Counter-mode expansion: block n is SHA256(seed || n), 8 components per
        // 32-byte block (4 bytes each), mapped to [-1, 1].
        var buffer = new byte[seed.Length + sizeof(int)];
        seed.CopyTo(buffer, 0);
        var written = 0;
        for (var block = 0; written < dimension; block++)
        {
            BitConverter.TryWriteBytes(buffer.AsSpan(seed.Length), block);
            var digest = SHA256.HashData(buffer);
            for (var offset = 0; offset + 4 <= digest.Length && written < dimension; offset += 4)
            {
                var raw = BitConverter.ToUInt32(digest, offset);
                var value = (raw / (double)uint.MaxValue) * 2.0 - 1.0;
                vector[written++] = (float)value;
                sumSquares += value * value;
            }
        }

        var norm = Math.Sqrt(sumSquares);
        if (norm == 0)
        {
            vector[0] = 1f; // astronomically unlikely; keeps the output a unit vector
            return vector;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }

        return vector;
    }
}
