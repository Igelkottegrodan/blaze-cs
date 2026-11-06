using System.Text;

namespace BlazeCs;

/// <summary>
/// The daemon's console output, in a file of its own that never grows past <see cref="MaxBytes"/>: a write
/// that would pass it first drops the older half. The daemon opens the file itself, with writers and
/// deleters allowed (the fixture check reads it while the daemon runs), because a launcher's redirect
/// handle on Windows allows neither a second writer nor a truncation.
/// </summary>
sealed class DaemonLog : TextWriter
{
    public const int MaxBytes = 1 << 20;

    readonly FileStream _file;
    readonly object _lock = new();

    public DaemonLog(string path)
    {
        _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        _file.Seek(0, SeekOrigin.End);
    }

    static readonly UTF8Encoding Utf8 = new(false);

    public override Encoding Encoding => Utf8;

    public override void Write(char value) => Append(value.ToString());

    public override void Write(string? value) => Append(value);

    public override void Write(char[] buffer, int index, int count) => Append(new string(buffer, index, count));

    public override void WriteLine(string? value) => Append(value + Environment.NewLine);

    void Append(string? text)
    {
        var bytes = Utf8.GetBytes(text ?? "");
        lock (_lock)
        {
            // Half the cap is kept, so a message of up to the other half fits after it.
            if (bytes.Length > MaxBytes / 2)
                bytes = bytes[^(MaxBytes / 2)..];
            if (_file.Position + bytes.Length > MaxBytes)
            {
                var kept = new byte[Math.Min(_file.Length, MaxBytes / 2)];
                _file.Position = _file.Length - kept.Length;
                _file.ReadExactly(kept);
                _file.SetLength(0);
                _file.Position = 0;
                _file.Write(kept);
            }
            _file.Write(bytes);
        }
    }
}
