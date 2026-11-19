using SyncSentry.Security;

namespace SyncSentry.Logging;

/// <summary>Writes scrubbed lines to the console and a rolling file. Secrets never reach either.</summary>
public sealed class ScrubbingLoggerProvider(string path) : ILoggerProvider
{
    public const long RollBytes = 5 * 1024 * 1024;
    private readonly object _gate = new();

    public ILogger CreateLogger(string categoryName) => new ScrubLogger(categoryName, path, _gate);

    public void Dispose()
    {
    }

    private sealed class ScrubLogger(string category, string path, object gate) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
        {
            if (category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                || category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                || category.StartsWith("System.Net.Http", StringComparison.Ordinal))
                return logLevel >= LogLevel.Warning;
            return logLevel >= LogLevel.Information;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var text = SecretRedactor.Scrub(formatter(state, exception));
            if (exception is not null)
                text = SecretRedactor.Scrub(text + " " + exception.GetType().Name);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {logLevel} {category}: {text}";
            lock (gate)
            {
                try
                {
                    Console.WriteLine(line);
                }
                catch (IOException)
                {
                }

                try
                {
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > RollBytes)
                    {
                        var rolled = path + ".1";
                        File.Delete(rolled);
                        File.Move(path, rolled);
                    }

                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
