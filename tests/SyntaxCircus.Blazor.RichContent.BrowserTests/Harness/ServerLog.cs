using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;

public sealed record ServerFailure(LogLevel Level, string Category, string Event, string Message, Exception? Exception)
{
    public override string ToString() => $"Server {Level} {Category} [{Event}] {Message} {Exception}";
}

/// <summary>
/// Keeps what a sample server logged as a failure: everything at <see cref="LogLevel.Error"/> and above, and the
/// events by which Blazor reports a failure in a circuit's code more quietly. Some of those the browser never
/// hears about, such as a failed disposal, or an exception from a method script invoked, whose rejected promise
/// the script handles. It also follows circuits, so a test can wait until its own have been disposed.
/// </summary>
public sealed class ServerLog : ILoggerProvider
{
    private const string ComponentsCategory = "Microsoft.AspNetCore.Components";

    // Names from the framework's own log messages. Most are logged at Debug.
    private static readonly HashSet<string> QuietFailures = new(StringComparer.Ordinal)
    {
        "BeginInvokeDotNetFailed",
        "BeginInvokeDotNetStaticFailed",
        "CircuitHandlerFailed",
        "CircuitInitializationFailed",
        "CircuitUnhandledException",
        "DispatchEventFailedToDispatchEvent",
        "DispatchEventFailedToParseEventData",
        "DisposeFailed",
        "EndInvokeDispatchException",
        "ExceptionCaughtByErrorBoundary",
        "ExceptionRenderingComponent",
        "InitializationFailed",
        "InvokeInstanceDotNetMethodException",
        "InvokeStaticDotNetMethodException",
        "OnRenderCompletedFailed",
        "ReceiveByteArrayException",
        "ReceiveJSDataChunkException",
        "UnhandledExceptionClientDisconnected",
        "UpdateRootComponentsFailed",
    };

    private readonly ConcurrentQueue<ServerFailure> _failures = new();
    private readonly ConcurrentDictionary<string, string> _circuitsByConnection = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _disposedCircuits = new(StringComparer.Ordinal);

    /// <summary>What the logging filter must let through to this provider.</summary>
    public static bool Wants(string? category, LogLevel level) =>
        level >= LogLevel.Error || (level >= LogLevel.Debug && category?.StartsWith(ComponentsCategory, StringComparison.Ordinal) == true);

    /// <summary>
    /// Returns the failures logged so far and forgets them, so each fails one test only. A test sharing the server
    /// with others may be handed a failure another one caused.
    /// </summary>
    public IReadOnlyList<ServerFailure> TakeFailures()
    {
        var taken = new List<ServerFailure>();
        while (_failures.TryDequeue(out var failure))
        {
            taken.Add(failure);
        }

        return taken;
    }

    /// <summary>True when this server has run a circuit over the SignalR connection.</summary>
    public bool Served(string connectionId) => _circuitsByConnection.ContainsKey(connectionId);

    /// <summary>True once the circuit that used the connection has been disposed, its components included.</summary>
    public bool HasDisposedCircuitOf(string connectionId) =>
        _circuitsByConnection.TryGetValue(connectionId, out var circuit) && _disposedCircuits.ContainsKey(circuit);

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
        // The host disposes its providers when it stops; what was recorded must outlive that.
    }

    private void Record(string category, LogLevel level, string name, IReadOnlyDictionary<string, string> values, string message, Exception? exception)
    {
        var framework = category.StartsWith(ComponentsCategory, StringComparison.Ordinal);
        if (framework && values.TryGetValue("CircuitId", out var circuit))
        {
            if (name == "OnConnectionUp" && values.TryGetValue("ConnectionId", out var connection))
            {
                _circuitsByConnection[connection] = circuit;
            }
            else if (name is "DisposeSucceeded" or "DisposeFailed")
            {
                _disposedCircuits[circuit] = true;
            }
        }

        if (level >= LogLevel.Error || (framework && QuietFailures.Contains(name)))
        {
            _failures.Enqueue(new(level, category, name, message, exception));
        }
    }

    private sealed class Logger(ServerLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && Wants(category, logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    values[key] = value?.ToString() ?? "";
                }
            }

            log.Record(category, logLevel, eventId.Name ?? "", values, formatter(state, exception), exception);
        }
    }
}
