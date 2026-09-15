namespace PdfView;

/// A read-only window onto part of a file, so a range request streams straight
/// from disk instead of being buffered in memory.
sealed class RangeStream : Stream
{
    readonly FileStream _file;
    long _remaining;

    public RangeStream(string path, long start, long length)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        _file.Seek(start, SeekOrigin.Begin);
        _remaining = length;
        Length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => Length - _remaining;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0) return 0;
        int want = (int)Math.Min(count, _remaining);
        int got = _file.Read(buffer, offset, want);
        _remaining -= got;
        return got;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _file.Dispose();
        base.Dispose(disposing);
    }
}
