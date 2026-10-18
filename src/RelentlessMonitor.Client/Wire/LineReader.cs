using System.Text;

namespace RelentlessMonitor.Wire;

/// <summary>
/// Reads newline-delimited lines with a length cap, so a peer that never sends a newline can't make
/// the reader buffer without bound. An over-long line is skipped up to its newline and reported.
/// </summary>
public sealed class LineReader
{
    private readonly StreamReader _reader;
    private readonly int _maxChars;
    private readonly char[] _buf = new char[8192];
    private int _pos, _len;

    public LineReader(Stream stream, int maxChars = Protocol.MaxLineChars)
    {
        _reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        _maxChars = maxChars;
    }

    /// <summary>Next line; <c>Line</c> is null at end of stream. <c>TooLong</c> means a line was dropped.</summary>
    public async Task<(string? Line, bool TooLong)> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        bool tooLong = false;
        while (true)
        {
            if (_pos == _len)
            {
                _len = await _reader.ReadAsync(_buf.AsMemory(), ct).ConfigureAwait(false);
                _pos = 0;
                if (_len == 0)
                    return (sb.Length > 0 && !tooLong ? sb.ToString() : null, tooLong);
            }
            int nl = Array.IndexOf(_buf, '\n', _pos, _len - _pos);
            int end = nl < 0 ? _len : nl;
            if (!tooLong)
            {
                if (sb.Length + (end - _pos) > _maxChars) { tooLong = true; sb.Clear(); }
                else sb.Append(_buf, _pos, end - _pos);
            }
            _pos = nl < 0 ? _len : nl + 1;
            if (nl >= 0)
            {
                if (tooLong) return ("", true);
                if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                return (sb.ToString(), false);
            }
        }
    }
}
