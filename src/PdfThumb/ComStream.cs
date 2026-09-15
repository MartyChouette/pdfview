using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace PdfThumb;

/// How the shell hands a handler the file's bytes. This is the one the shell
/// prefers: handlers that take a stream can run in its isolated thumbnail host,
/// handlers that only take a path cannot.
[ComImport]
[Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithStream
{
    void Initialize(IStream stream, uint mode);
}

/// A seekable .NET Stream over a COM IStream, so the PDF can be read where it
/// lies instead of being copied into memory first.
internal sealed class ComStream : Stream
{
    readonly IStream _stream;
    readonly long _length;

    public ComStream(IStream stream)
    {
        _stream = stream;
        _stream.Stat(out var stat, 1 /* STATFLAG_NONAME */);
        _length = stat.cbSize;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => Seek(0, SeekOrigin.Current);
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return 0;

        var read = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            if (offset == 0)
            {
                _stream.Read(buffer, count, read);
                return Marshal.ReadInt32(read);
            }

            // IStream always fills from the start of the array it is given.
            var scratch = new byte[count];
            _stream.Read(scratch, count, read);
            var got = Marshal.ReadInt32(read);
            Buffer.BlockCopy(scratch, 0, buffer, offset, got);
            return got;
        }
        finally
        {
            Marshal.FreeCoTaskMem(read);
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = Marshal.AllocCoTaskMem(sizeof(long));
        try
        {
            _stream.Seek(offset, (int)origin, position);
            return Marshal.ReadInt64(position);
        }
        finally
        {
            Marshal.FreeCoTaskMem(position);
        }
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
