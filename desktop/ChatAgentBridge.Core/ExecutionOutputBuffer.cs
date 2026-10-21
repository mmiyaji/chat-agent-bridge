using System.Text;

namespace ChatAgentBridge.Core;

public sealed record ExecutionOutputSnapshot(string Text, bool IsTruncated, long Version);

/// <summary>A bounded, thread-safe tail for transient output. Never writes to disk.</summary>
public sealed class ExecutionOutputBuffer
{
    public const int DefaultCapacity = 64_000;
    private readonly object _gate = new();
    private readonly int _capacity;
    private char[]? _characters;
    private int _position;
    private int _length;
    private bool _truncated;
    private long _version;

    public ExecutionOutputBuffer(int capacity = DefaultCapacity)
    {
        if (capacity is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Append(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            _characters ??= new char[_capacity];
            if (text.Length >= _capacity)
            {
                _truncated |= _length > 0 || text.Length > _capacity;
                text.AsSpan(text.Length - _capacity).CopyTo(_characters);
                _position = 0;
                _length = _capacity;
            }
            else
            {
                _truncated |= text.Length > _capacity - _length;
                var first = Math.Min(text.Length, _capacity - _position);
                text.AsSpan(0, first).CopyTo(_characters.AsSpan(_position));
                text.AsSpan(first).CopyTo(_characters);
                _position = (_position + text.Length) % _capacity;
                _length = Math.Min(_capacity, _length + text.Length);
            }
            _version++;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_characters is not null) Array.Clear(_characters);
            _position = _length = 0;
            _truncated = false;
            _version++;
        }
    }

    public ExecutionOutputSnapshot? SnapshotIfChanged(long version)
    {
        lock (_gate)
        {
            if (version == _version) return null;
            if (_length == 0) return new("", _truncated, _version);
            var start = (_position - _length + _capacity) % _capacity;
            var first = Math.Min(_length, _capacity - start);
            var builder = new StringBuilder(_length);
            builder.Append(_characters!, start, first);
            if (first < _length) builder.Append(_characters!, 0, _length - first);
            // Do not render a broken surrogate pair when the bounded tail cuts one in half.
            if (builder.Length > 0 && char.IsLowSurrogate(builder[0])) builder.Remove(0, 1);
            return new(builder.ToString(), _truncated, _version);
        }
    }
}
