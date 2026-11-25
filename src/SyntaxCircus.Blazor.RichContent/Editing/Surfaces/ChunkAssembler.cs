using System.Text;

namespace SyntaxCircus.Blazor.RichContent.Editing.Surfaces;

/// <summary>
/// Joins a payload the browser sent as several messages. The chunks of one sequence arrive together and in order;
/// sequence numbers only increase.
/// </summary>
internal sealed class ChunkAssembler
{
    private const int MaximumBytes = 2 * 1024 * 1024;

    private readonly StringBuilder _payload = new();
    private long _completed = long.MinValue;
    private long _sequence;
    private int _count;
    private int _next;
    private int _bytes;

    /// <summary>
    /// Returns the whole payload when the last chunk of a sequence arrives, otherwise null. A sequence at or below
    /// the last completed one is a resend and is ignored. Index 0 starts again, dropping anything unfinished.
    /// </summary>
    /// <exception cref="FormatException">
    /// A chunk is missing or out of order, or the payload is over 2 MiB of UTF-8. The unfinished payload is dropped.
    /// </exception>
    public string? Add(long sequence, int index, int count, string chunk)
    {
        if (sequence <= _completed)
        {
            return null;
        }

        if (index == 0)
        {
            Clear();
            _sequence = sequence;
            _count = count;
        }

        if (chunk is null || index < 0 || index >= count || sequence != _sequence || count != _count || index != _next)
        {
            Clear();
            throw new FormatException("A chunk is missing or out of order.");
        }

        _bytes += Encoding.UTF8.GetByteCount(chunk);
        if (_bytes > MaximumBytes)
        {
            Clear();
            throw new FormatException("The payload is too large.");
        }

        _payload.Append(chunk);
        if (++_next < count)
        {
            return null;
        }

        var payload = _payload.ToString();
        _completed = sequence;
        Clear();
        return payload;
    }

    // A count of zero matches no chunk, so nothing but index 0 can follow.
    private void Clear()
    {
        _payload.Clear();
        _count = 0;
        _next = 0;
        _bytes = 0;
    }
}
