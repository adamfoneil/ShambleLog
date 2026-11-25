using System.Threading.Channels;

namespace ShambleLog.Core;

public sealed class LogEntryQueue
{
    private readonly Channel<LogEntry> _channel = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false
    });

    private readonly AsyncLocal<bool> _suppressLogging = new();
    private int _closedWarningWritten;

    internal bool SuppressLogging
    {
        get => _suppressLogging.Value;
        set => _suppressLogging.Value = value;
    }

    internal void Enqueue(LogEntry entry)
    {
        if (!_channel.Writer.TryWrite(entry) && Interlocked.Exchange(ref _closedWarningWritten, 1) == 0)
            Console.Error.WriteLine("ShambleLog: the logging queue is closed; log entries cannot be persisted.");
    }

    internal ChannelReader<LogEntry> Reader => _channel.Reader;

    internal void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);
}
