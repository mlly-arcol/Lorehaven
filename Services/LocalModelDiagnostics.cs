using System.Text;

namespace NativeTavern.Services;

internal sealed class LocalModelDiagnostics
{
    internal const int Capacity = 8192;
    private readonly StringBuilder _text = new();
    private readonly object _gate = new();

    public void Append(string text)
    {
        lock (_gate)
        {
            _text.Append(text);
            if (_text.Length > Capacity) _text.Remove(0, _text.Length - Capacity);
        }
    }

    public string Snapshot()
    {
        lock (_gate) return _text.ToString();
    }
}
