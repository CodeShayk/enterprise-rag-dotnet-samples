using System.Text;

namespace Contoso.Knowledge.Generation;

/// <summary>
/// Reads the string values of one property out of JSON while it streams in, at any depth, so the text of each claim
/// reaches the user as the model writes it, before the claim's source and quote are complete. Consecutive values are
/// separated by a space. <c>Utf8JsonReader</c> can't do this: it returns a string only once its closing quote arrives.
/// </summary>
public sealed class StreamingJsonField(string name)
{
    private readonly Stack<(bool IsObject, string? Key)> _containers = new();
    private readonly StringBuilder _key = new();
    private bool _expectKey, _inString, _isKey, _isTarget, _escape, _any;
    private int _hexLeft, _hex;

    /// <summary>Takes the next piece of the stream and returns the text of the property's values that it completes.</summary>
    public string Push(string chunk)
    {
        var output = new StringBuilder();
        foreach (var c in chunk) Step(c, output);
        return output.ToString();
    }

    private void Step(char c, StringBuilder output)
    {
        if (_inString)
        {
            if (_hexLeft > 0)
            {
                _hex = _hex * 16 + Convert.ToInt32(c.ToString(), 16);
                if (--_hexLeft == 0) Emit((char)_hex, output);
            }
            else if (_escape)
            {
                _escape = false;
                if (c == 'u') (_hexLeft, _hex) = (4, 0);
                else Emit(c switch { 'n' => '\n', 't' => '\t', 'r' => '\r', 'b' => '\b', 'f' => '\f', _ => c }, output);
            }
            else if (c == '\\') _escape = true;
            else if (c == '"')
            {
                _inString = false;
                if (_isKey && _containers.TryPop(out var current)) _containers.Push(current with { Key = _key.ToString() });
            }
            else Emit(c, output);
            return;
        }

        switch (c)
        {
            case '{':
                _containers.Push((true, null));
                _expectKey = true;
                break;
            case '[':
                _containers.Push((false, null));
                _expectKey = false;
                break;
            case '}' or ']':
                _containers.TryPop(out _);
                break;
            case ',':
                _expectKey = _containers.TryPeek(out var container) && container.IsObject;
                break;
            case ':':
                _expectKey = false;
                break;
            case '"':
                // Inside an object, a string is a key where a key is expected and a value after the colon.
                _inString = true;
                var inObject = _containers.TryPeek(out var top) && top.IsObject;
                _isKey = inObject && _expectKey;
                _isTarget = inObject && !_expectKey && top.Key == name;
                if (_isKey) _key.Clear();
                if (_isTarget && _any) output.Append(' ');
                _any |= _isTarget;
                break;
        }
    }

    private void Emit(char c, StringBuilder output)
    {
        if (_isKey) _key.Append(c);
        else if (_isTarget) output.Append(c);
    }
}
