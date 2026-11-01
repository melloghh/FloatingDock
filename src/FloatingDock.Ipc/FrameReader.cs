using System.Text;

namespace FloatingDock.Ipc;

/// <summary>Splits a byte stream into newline-terminated UTF-8 frames of bounded size.</summary>
internal sealed class FrameReader(Stream stream, int maxFrameBytes)
{
    private byte[] _buf = new byte[16 * 1024];
    private int _start, _end;

    /// <summary>The next non-empty frame, or null when the peer closed the stream.</summary>
    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            int nl = Array.IndexOf(_buf, (byte)'\n', _start, _end - _start);
            if (nl >= 0)
            {
                var line = Encoding.UTF8.GetString(_buf, _start, nl - _start).TrimEnd('\r');
                _start = nl + 1;
                if (_start == _end) _start = _end = 0;
                if (line.Length == 0) continue;
                return line;
            }
            if (_end - _start > maxFrameBytes) throw new InvalidDataException("Frame is larger than " + maxFrameBytes + " bytes.");
            if (_end == _buf.Length)
            {
                if (_start > 0)
                {
                    Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
                    _end -= _start;
                    _start = 0;
                }
                else Array.Resize(ref _buf, _buf.Length * 2);
            }
            int n = await stream.ReadAsync(_buf.AsMemory(_end), ct).ConfigureAwait(false);
            if (n == 0) return null;
            _end += n;
        }
    }
}
