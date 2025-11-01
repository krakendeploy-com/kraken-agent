using System.Text;

namespace Kraken.Agent.Installer;

/// <summary>
///     A TextWriter that writes to both console and a file simultaneously.
///     Used for logging installation output to both the console and a log file.
/// </summary>
internal class DualWriter : TextWriter
{
    private readonly TextWriter _consoleWriter;
    private readonly StreamWriter _fileWriter;

    public DualWriter(TextWriter consoleWriter, StreamWriter fileWriter)
    {
        _consoleWriter = consoleWriter;
        _fileWriter = fileWriter;
    }

    public override Encoding Encoding => _consoleWriter.Encoding;

    public override void WriteLine(string? value)
    {
        _consoleWriter.WriteLine(value);
        _fileWriter.WriteLine(value);
        _fileWriter.Flush();
    }

    public override void Write(string? value)
    {
        _consoleWriter.Write(value);
        _fileWriter.Write(value);
        _fileWriter.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _fileWriter?.Dispose();
        base.Dispose(disposing);
    }
}