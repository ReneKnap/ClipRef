using ClipRef;

namespace ClipRefTests;

/// <summary>
/// Tests the read side: the <see cref="ClipboardSaver.Decide(ClipboardSnapshot)"/> bridge
/// (a raw clipboard read classified via the already-tested precedence) and the
/// <see cref="IClipboardReader"/> seam wiring through <see cref="InMemoryClipboardReader"/>.
/// The snapshot carries the three raw reads; <c>hasImage</c> derives from
/// <c>ImagePng is not null</c>, not from the image being non-empty.
/// </summary>
public class ClipboardReadTests
{
    [Fact]
    public void FileWinsOverTextAndImage()
    {
        var snapshot = new ClipboardSnapshot(@"C:\logs\report.pdf", "some text", new byte[] { 1, 2, 3 });
        Assert.Equal<SaveDecision>(new SaveDecision.CopyFile(@"C:\logs\report.pdf"), ClipboardSaver.Decide(snapshot));
    }

    [Fact]
    public void TextSavedWhenNoFile()
    {
        var snapshot = new ClipboardSnapshot(null, "hello world", null);
        Assert.Equal<SaveDecision>(new SaveDecision.SaveText("hello world"), ClipboardSaver.Decide(snapshot));
    }

    [Fact]
    public void TextWinsOverImage()
    {
        var snapshot = new ClipboardSnapshot(null, "hello world", new byte[] { 0x89, 0x50 });
        Assert.Equal<SaveDecision>(new SaveDecision.SaveText("hello world"), ClipboardSaver.Decide(snapshot));
    }

    [Fact]
    public void ImageSavedWhenOnlyImage()
    {
        var snapshot = new ClipboardSnapshot(null, null, new byte[] { 0x89, 0x50 });
        Assert.Equal<SaveDecision>(new SaveDecision.SaveImage(), ClipboardSaver.Decide(snapshot));
    }

    [Fact]
    public void EmptyClipboardIgnored()
    {
        Assert.Equal<SaveDecision>(new SaveDecision.Ignore(), ClipboardSaver.Decide(new ClipboardSnapshot(null, null, null)));
        Assert.Equal<SaveDecision>(new SaveDecision.Ignore(), ClipboardSaver.Decide(new ClipboardSnapshot(null, "", null)));
    }

    [Fact]
    public void NonNullEmptyImageStillCountsAsImage()
    {
        // hasImage is `ImagePng is not null`, so a non-null but empty array still counts as an
        // image present — pins the is-not-null semantics rather than a length check.
        var snapshot = new ClipboardSnapshot(null, null, Array.Empty<byte>());
        Assert.Equal<SaveDecision>(new SaveDecision.SaveImage(), ClipboardSaver.Decide(snapshot));
    }

    [Fact]
    public void NullImageIsNotAnImage()
    {
        var snapshot = new ClipboardSnapshot(null, null, null);
        Assert.Equal<SaveDecision>(new SaveDecision.Ignore(), ClipboardSaver.Decide(snapshot));
    }

    [Theory]
    [InlineData(@"§C:\logs\clip.txt")]
    [InlineData(@"§\\server\share\clip.txt")]
    [InlineData(@"@C:\logs\clip.txt")]              // legacy prefix stays guarded
    [InlineData(@"@\\server\share\clip.txt")]
    public void ReferenceTextIsIgnored(string text)
    {
        Assert.Equal<SaveDecision>(new SaveDecision.Ignore(), ClipboardSaver.Decide(new ClipboardSnapshot(null, text, null)));
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData(@"§C:\has space\clip.txt")] // a prefixed token with whitespace is not a reference
    [InlineData(@"@C:\has space\clip.txt")]
    [InlineData("§relative/path")]          // not a Windows absolute path
    [InlineData("@relative/path")]
    public void NonReferenceTextSaved(string text)
    {
        Assert.Equal<SaveDecision>(new SaveDecision.SaveText(text), ClipboardSaver.Decide(new ClipboardSnapshot(null, text, null)));
    }

    [Fact]
    public void ReaderSnapshotFlowsIntoDecide()
    {
        var reader = new InMemoryClipboardReader(new ClipboardSnapshot(@"C:\a\b.txt", null, null));
        Assert.Equal<SaveDecision>(new SaveDecision.CopyFile(@"C:\a\b.txt"), ClipboardSaver.Decide(reader.Read()));
    }
}
