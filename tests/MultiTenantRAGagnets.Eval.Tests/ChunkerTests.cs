using MultiTenantRAGagnets.Core.Chunking;

namespace MultiTenantRAGagnets.Eval.Tests;

public class ChunkerTests
{
    private static string Words(int count) =>
        string.Join(' ', Enumerable.Range(0, count).Select(i => $"word{i:D4}"));

    [Fact]
    public void Parameters_are_the_recorded_constants()
    {
        Assert.Equal(500, ChunkingParameters.ChunkSize);
        Assert.Equal(100, ChunkingParameters.Overlap);
        Assert.Contains("size=500", ChunkingParameters.Describe());
        Assert.Contains("overlap=100", ChunkingParameters.Describe());
    }

    [Fact]
    public void Empty_and_whitespace_give_no_chunks()
    {
        Assert.Empty(Chunker.Chunk(""));
        Assert.Empty(Chunker.Chunk("   \n\n  "));
    }

    [Fact]
    public void Short_text_is_one_chunk_equal_to_the_input()
    {
        var chunks = Chunker.Chunk("hello world");
        var only = Assert.Single(chunks);
        Assert.Equal("hello world", only.Text);
        Assert.Equal(0, only.Start);
    }

    [Fact]
    public void Text_of_exactly_chunk_size_is_one_chunk()
    {
        var text = new string('a', ChunkingParameters.ChunkSize);
        Assert.Single(Chunker.Chunk(text));
    }

    [Fact]
    public void Text_just_over_chunk_size_is_two_chunks()
    {
        var text = new string('a', ChunkingParameters.ChunkSize + 1);
        Assert.Equal(2, Chunker.Chunk(text).Count);
    }

    [Fact]
    public void No_chunk_exceeds_the_size_and_each_equals_its_source_slice()
    {
        var text = Words(400);
        foreach (var c in Chunker.Chunk(text))
        {
            Assert.True(c.Length <= ChunkingParameters.ChunkSize);
            Assert.Equal(text.Substring(c.Start, c.Length), c.Text);
        }
    }

    [Fact]
    public void Consecutive_chunks_overlap_by_exactly_the_overlap_constant()
    {
        var text = Words(400);
        var chunks = Chunker.Chunk(text);
        Assert.True(chunks.Count > 3);
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.Equal(ChunkingParameters.Overlap, chunks[i - 1].End - chunks[i].Start);
        }
    }

    [Fact]
    public void Chunks_cover_the_whole_text_with_no_gap()
    {
        var text = Words(300);
        var chunks = Chunker.Chunk(text);
        Assert.Equal(0, chunks[0].Start);
        Assert.Equal(text.Length, chunks[^1].End);
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.True(chunks[i].Start <= chunks[i - 1].End);
            Assert.True(chunks[i].Start > chunks[i - 1].Start);
        }
    }

    [Fact]
    public void Chunks_end_on_whitespace_when_the_text_has_spaces()
    {
        var text = Words(300);
        var chunks = Chunker.Chunk(text);
        foreach (var c in chunks.Take(chunks.Count - 1))
        {
            Assert.True(char.IsWhiteSpace(text[c.End - 1]), $"chunk {c.Index} did not end on whitespace");
        }
    }

    [Fact]
    public void Unbroken_text_still_makes_progress()
    {
        var text = new string('x', 5000);
        var chunks = Chunker.Chunk(text);
        Assert.True(chunks.Count > 10);
        Assert.Equal(ChunkingParameters.ChunkSize, chunks[0].Length);
        Assert.Equal(text.Length, chunks[^1].End);
    }

    [Fact]
    public void Chunk_indexes_are_sequential_and_chunking_is_deterministic()
    {
        var text = Words(250);
        var a = Chunker.Chunk(text);
        var b = Chunker.Chunk(text);
        Assert.Equal(a.Select(c => c.Index), Enumerable.Range(0, a.Count));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Chunk_ids_are_deterministic_and_distinct()
    {
        var doc = Guid.Parse("11111111-1111-5111-8111-111111111111");
        Assert.Equal(ChunkIdentity.For(doc, 1, 0), ChunkIdentity.For(doc, 1, 0));
        Assert.NotEqual(ChunkIdentity.For(doc, 1, 0), ChunkIdentity.For(doc, 1, 1));
        Assert.NotEqual(ChunkIdentity.For(doc, 1, 0), ChunkIdentity.For(doc, 2, 0));
    }
}
