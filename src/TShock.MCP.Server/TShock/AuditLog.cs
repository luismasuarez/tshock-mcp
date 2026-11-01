using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace TShock.McpServer.TShock;

/// <summary>
/// Append-only audit trail for every TShock REST call. Only non-sensitive
/// metadata is recorded: never query values, the token, player names or bodies.
/// When <see cref="TShockOptions.AuditLogPath"/> is unset the entries go to the
/// standard logger instead.
/// </summary>
public sealed class AuditLog
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    private readonly TShockOptions _options;
    private readonly ILogger _logger;
    private readonly object _sync = new();

    public AuditLog(TShockOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Appends one JSONL entry (or logs it) and never throws: auditing must not
    /// break a tool call.
    /// </summary>
    public void Write(
        string operation,
        string path,
        int statusCode,
        bool ok,
        long elapsedMilliseconds,
        string? errorCode,
        string method = "GET")
    {
        var entry = new AuditEntry
        {
            Timestamp = DateTime.UtcNow,
            Operation = operation,
            Method = method,
            Path = StripQuery(path),
            StatusCode = statusCode,
            Ok = ok,
            ElapsedMilliseconds = elapsedMilliseconds,
            ErrorCode = errorCode
        };

        var logPath = _options.AuditLogPath;
        if (string.IsNullOrWhiteSpace(logPath))
        {
            _logger.LogInformation(
                "tshock-audit {Operation} {Method} {Path} status={StatusCode} ok={Ok} elapsedMs={ElapsedMs} errorCode={ErrorCode}",
                entry.Operation,
                entry.Method,
                entry.Path,
                entry.StatusCode,
                entry.Ok,
                entry.ElapsedMilliseconds,
                entry.ErrorCode);
            return;
        }

        try
        {
            var line = JsonSerializer.Serialize(entry, SerializerOptions);

            lock (_sync)
            {
                var fullPath = Path.GetFullPath(logPath);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Open per write (share read) so external log rotation works.
                var created = !File.Exists(fullPath);
                using (var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.WriteLine(line);
                }

                // The audit trail contains operation metadata, so keep the file
                // owner-only on POSIX. Best effort: a failure is handled below and
                // must never break the tool call.
                if (created && !OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or SecurityException)
        {
            _logger.LogWarning(ex, "Failed to write the TShock audit log entry.");
        }
    }

    private static string StripQuery(string path)
    {
        var separatorIndex = path.IndexOf('?', StringComparison.Ordinal);
        return separatorIndex >= 0 ? path[..separatorIndex] : path;
    }

    private sealed class AuditEntry
    {
        [JsonPropertyName("ts")]
        public DateTime Timestamp { get; init; }

        [JsonPropertyName("operation")]
        public string Operation { get; init; } = string.Empty;

        [JsonPropertyName("method")]
        public string Method { get; init; } = "GET";

        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;

        [JsonPropertyName("status")]
        public int StatusCode { get; init; }

        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        [JsonPropertyName("elapsedMs")]
        public long ElapsedMilliseconds { get; init; }

        [JsonPropertyName("errorCode")]
        public string? ErrorCode { get; init; }
    }
}
