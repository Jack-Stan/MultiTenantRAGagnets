using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MultiTenantRAGagnets.Core.Audit;

namespace MultiTenantRAGagnets.Core.Tests;

public class QueryHasherTests
{
    private static HmacQueryHasher Hasher(string key = "test-only-hash-key-0123456789abcdef") =>
        new(Options.Create(new AuditOptions { HashKey = key }));

    [Fact]
    public void Hash_is_deterministic_64_lowercase_hex_and_does_not_contain_the_text()
    {
        const string q = "what is the salary band for engineers?";
        var h = Hasher().Hash(q);

        Assert.Equal(h, Hasher().Hash(q));
        Assert.Matches("^[0-9a-f]{64}$", h);
        Assert.DoesNotContain("salary", h);
    }

    [Fact]
    public void Different_queries_give_different_hashes()
    {
        Assert.NotEqual(Hasher().Hash("alpha"), Hasher().Hash("beta"));
    }

    [Fact]
    public void Hash_is_not_a_plain_sha256_so_a_dictionary_of_guesses_cannot_confirm_a_query_without_the_key()
    {
        const string q = "what is the salary band for engineers?";
        var plain = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(q)));

        Assert.NotEqual(plain, Hasher().Hash(q));
        Assert.NotEqual(Hasher("another-test-only-key-0123456789abcd").Hash(q), Hasher().Hash(q));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void Missing_or_short_key_fails_fast(string? key)
    {
        Assert.Throws<InvalidOperationException>(() => Hasher(key!));
    }
}
