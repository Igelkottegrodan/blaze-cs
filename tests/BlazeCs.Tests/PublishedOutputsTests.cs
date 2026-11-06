using Xunit;

namespace BlazeCs.Tests;

/// <summary>
/// Whether an output file already holds the bytes about to be written (<see cref="PublishedOutputs"/>): from
/// what the daemon remembers while the file's stamp is unchanged, from the disk once something else touched it.
/// </summary>
public sealed class PublishedOutputsTests : IDisposable
{
    readonly string _directory = Directory.CreateTempSubdirectory("blaze-cs-published-").FullName;
    readonly PublishedOutputs _published = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    string File(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Rewrites a file the way another process would: new bytes, a later stamp.</summary>
    static void Touch(string path, byte[] bytes)
    {
        var stamp = System.IO.File.GetLastWriteTimeUtc(path);
        System.IO.File.WriteAllBytes(path, bytes);
        System.IO.File.SetLastWriteTimeUtc(path, stamp.AddSeconds(1));
    }

    [Fact]
    public void A_missing_file_holds_nothing() =>
        Assert.False(_published.Holds(Path.Combine(_directory, "missing.dll"), [1, 2, 3]));

    [Fact]
    public void A_written_file_holds_its_bytes_and_no_others()
    {
        var path = File("a.dll", [1, 2, 3]);
        _published.Wrote(path, OutputContent.Of([1, 2, 3]));

        Assert.True(_published.Holds(path, [1, 2, 3]));
        Assert.False(_published.Holds(path, [1, 2, 4]));
        Assert.False(_published.Holds(path, [1, 2, 3, 4]));
        Assert.True(_published.HoldsLast(path));
    }

    [Fact]
    public void A_file_never_written_is_compared_on_disk_and_then_remembered()
    {
        var path = File("a.dll", [1, 2, 3]);

        Assert.False(_published.HoldsLast(path));
        Assert.False(_published.Holds(path, [1, 2, 4]));
        Assert.True(_published.Holds(path, [1, 2, 3]));
        Assert.True(_published.HoldsLast(path));
    }

    [Fact]
    public void A_file_touched_with_the_same_bytes_still_holds_them()
    {
        var path = File("a.dll", [1, 2, 3]);
        _published.Wrote(path, OutputContent.Of([1, 2, 3]));
        Touch(path, [1, 2, 3]);

        Assert.True(_published.HoldsLast(path));
        Assert.True(_published.Holds(path, [1, 2, 3]));
    }

    [Fact]
    public void A_file_rewritten_by_something_else_no_longer_holds_what_the_daemon_wrote()
    {
        var path = File("a.dll", [1, 2, 3]);
        _published.Wrote(path, OutputContent.Of([1, 2, 3]));
        Touch(path, [9, 9, 9]);

        Assert.False(_published.HoldsLast(path));
        Assert.False(_published.Holds(path, [1, 2, 3]));
        Assert.True(_published.Holds(path, [9, 9, 9]));
    }

    [Fact]
    public void A_forgotten_file_is_compared_on_disk_again()
    {
        var path = File("a.dll", [1, 2, 3]);
        _published.Wrote(path, OutputContent.Of([1, 2, 3]));
        _published.Forget(path);

        Assert.False(_published.HoldsLast(path));
        Assert.True(_published.Holds(path, [1, 2, 3]));
    }

    [Fact]
    public void Contents_are_equal_exactly_when_their_bytes_are()
    {
        Assert.Equal(OutputContent.Of([1, 2, 3]), OutputContent.Of([1, 2, 3]));
        Assert.NotEqual(OutputContent.Of([1, 2, 3]), OutputContent.Of([1, 2, 4]));
        Assert.NotEqual(OutputContent.Of([]), OutputContent.Of([0]));
    }
}
