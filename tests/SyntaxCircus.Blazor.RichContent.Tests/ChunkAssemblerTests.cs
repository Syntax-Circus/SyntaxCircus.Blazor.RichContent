using SyntaxCircus.Blazor.RichContent.Editing.Surfaces;

namespace SyntaxCircus.Blazor.RichContent.Tests;

public sealed class ChunkAssemblerTests
{
    private const int Cap = 2 * 1024 * 1024;

    [Fact]
    public void Chunks_SinglePart_IsReturnedAtOnce() => new ChunkAssembler().Add(1, 0, 1, "abc").ShouldBe("abc");

    [Fact]
    public void Chunks_AreJoinedWhenTheLastArrives()
    {
        var assembler = new ChunkAssembler();

        assembler.Add(1, 0, 3, "a").ShouldBeNull();
        assembler.Add(1, 1, 3, "b").ShouldBeNull();
        assembler.Add(1, 2, 3, "c").ShouldBe("abc");
        assembler.Add(2, 0, 1, "d").ShouldBe("d");
    }

    [Fact]
    public void Chunks_ResendOfACompletedSequence_IsIgnored()
    {
        var assembler = new ChunkAssembler();
        assembler.Add(5, 0, 2, "a");
        assembler.Add(5, 1, 2, "b");

        assembler.Add(5, 0, 2, "a").ShouldBeNull();
        assembler.Add(5, 1, 2, "b").ShouldBeNull();
        assembler.Add(4, 0, 1, "older").ShouldBeNull();
        assembler.Add(6, 0, 1, "next").ShouldBe("next");
    }

    [Fact]
    public void Chunks_IgnoredResend_DoesNotDisturbThePendingPayload()
    {
        var assembler = new ChunkAssembler();
        assembler.Add(1, 0, 1, "done");
        assembler.Add(2, 0, 2, "a");

        assembler.Add(1, 0, 1, "done").ShouldBeNull();

        assembler.Add(2, 1, 2, "b").ShouldBe("ab");
    }

    [Fact]
    public void Chunks_IndexZero_Restarts()
    {
        var assembler = new ChunkAssembler();
        assembler.Add(1, 0, 3, "lost");

        assembler.Add(1, 0, 2, "a").ShouldBeNull();
        assembler.Add(1, 1, 2, "b").ShouldBe("ab");

        assembler.Add(2, 0, 3, "abandoned");
        assembler.Add(3, 0, 2, "c").ShouldBeNull();
        assembler.Add(3, 1, 2, "d").ShouldBe("cd");
    }

    [Fact]
    public void Chunks_GapOrOversize_Rejected()
    {
        var assembler = new ChunkAssembler();

        // A gap, a repeat, another sequence, another count, and a start that never arrived.
        assembler.Add(1, 0, 3, "a");
        Should.Throw<FormatException>(() => assembler.Add(1, 2, 3, "c"));
        Should.Throw<FormatException>(() => assembler.Add(1, 1, 3, "b"));
        assembler.Add(2, 0, 3, "a");
        assembler.Add(2, 1, 3, "b");
        Should.Throw<FormatException>(() => assembler.Add(2, 1, 3, "b"));
        assembler.Add(3, 0, 2, "a");
        Should.Throw<FormatException>(() => assembler.Add(4, 1, 2, "b"));
        assembler.Add(5, 0, 2, "a");
        Should.Throw<FormatException>(() => assembler.Add(5, 1, 3, "b"));
        Should.Throw<FormatException>(() => assembler.Add(6, 1, 2, "b"));

        Should.Throw<FormatException>(() => assembler.Add(7, 0, 1, new string('x', Cap + 1)));
        assembler.Add(8, 0, 2, new string('x', Cap));
        Should.Throw<FormatException>(() => assembler.Add(8, 1, 2, "x"));

        // A rejection does not wedge the assembler.
        assembler.Add(9, 0, 1, "ok").ShouldBe("ok");
    }

    [Fact]
    public void Chunks_PayloadAtTheCap_IsAccepted()
    {
        var assembler = new ChunkAssembler();
        assembler.Add(1, 0, 2, new string('x', Cap - 1));

        assembler.Add(1, 1, 2, "x")!.Length.ShouldBe(Cap);
    }

    [Fact]
    public void Chunks_CapCountsUtf8Bytes() =>
        Should.Throw<FormatException>(() => new ChunkAssembler().Add(1, 0, 1, new string('一', (Cap / 3) + 1)));

    [Fact]
    public void Chunks_RejectedSequence_CanBeSentAgainFromTheStart()
    {
        var assembler = new ChunkAssembler();
        assembler.Add(1, 0, 2, "a");
        Should.Throw<FormatException>(() => assembler.Add(1, 5, 2, "b"));

        assembler.Add(1, 0, 2, "a").ShouldBeNull();
        assembler.Add(1, 1, 2, "b").ShouldBe("ab");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(-1, 1)]
    [InlineData(1, 1)]
    public void Chunks_ImpossibleIndexOrCount_Rejected(int index, int count) =>
        Should.Throw<FormatException>(() => new ChunkAssembler().Add(1, index, count, "a"));

    [Fact]
    public void Chunks_NullChunk_Rejected() =>
        Should.Throw<FormatException>(() => new ChunkAssembler().Add(1, 0, 1, null!));
}
