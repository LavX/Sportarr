using System.Diagnostics;
using System.Collections.Concurrent;

namespace Sportarr.Api.Services;

/// <summary>
/// Service for streaming IPTV via FFmpeg transcoding.
/// Converts MPEG-TS and other formats to HLS for browser playback.
/// Similar approach to Dispatcharr.
/// </summary>
public class FFmpegStreamService : IDisposable
{
    private const int HlsPlaylistSize = 10;
    private static readonly TimeSpan ViewerLeaseTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ViewerLeaseSweepInterval = TimeSpan.FromSeconds(15);
    private readonly ILogger<FFmpegStreamService> _logger;
    private readonly ConcurrentDictionary<string, StreamSession> _sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _channelLocks = new();
    private readonly string _hlsOutputPath;
    private bool _disposed;

    public FFmpegStreamService(ILogger<FFmpegStreamService> logger)
    {
        _logger = logger;

        // Create HLS output directory in temp
        _hlsOutputPath = Path.Combine(Path.GetTempPath(), "sportarr-hls");
        if (!Directory.Exists(_hlsOutputPath))
        {
            Directory.CreateDirectory(_hlsOutputPath);
        }
        else
        {
            // Dispose deletes this tree on graceful shutdown, so anything
            // here at construction is an orphan from a crashed/killed
            // process (this is a startup singleton - no sessions can be
            // live yet). Without this sweep, crash leftovers accumulate in
            // temp forever on installs that never shut down cleanly.
            foreach (var stale in Directory.GetDirectories(_hlsOutputPath))
            {
                try
                {
                    Directory.Delete(stale, true);
                    _logger.LogInformation("[Stream] Removed orphaned HLS session directory from previous run: {Path}", stale);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Stream] Could not remove orphaned HLS directory {Path}", stale);
                }
            }
        }
    }

    /// <summary>
    /// Start streaming a channel via FFmpeg HLS. Stream copy is the default;
    /// normalization is enabled explicitly for sources that need it.
    /// </summary>
    public async Task<StreamResult> StartStreamAsync(
        string channelId,
        string streamUrl,
        string? userAgent = null,
        bool normalize = false)
    {
        var channelLock = _channelLocks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
        await channelLock.WaitAsync();
        try
        {
            return await StartStreamLockedAsync(channelId, streamUrl, userAgent, normalize);
        }
        finally
        {
            channelLock.Release();
        }
    }

    private async Task<StreamResult> StartStreamLockedAsync(
        string channelId,
        string streamUrl,
        string? userAgent,
        bool normalize)
    {
        _sessions.TryGetValue(channelId, out var existingSession);
        if (existingSession is { IsActive: true } && existingSession.Normalize == normalize)
        {
            _logger.LogDebug("[Stream] Reusing existing session for channel {ChannelId}", channelId);
            return new StreamResult
            {
                Success = true,
                SessionId = existingSession.SessionId,
                LeaseId = existingSession.ViewerLeases.Acquire(),
                PlaylistUrl = $"/api/v1/stream/{existingSession.SessionId}/playlist.m3u8"
            };
        }

        if (existingSession is { IsActive: true } && existingSession.ViewerLeases.Count > 0)
        {
            return new StreamResult
            {
                Success = false,
                Error = "This channel is already being watched with a different playback mode."
            };
        }

        if (existingSession is { IsActive: false })
        {
            ((ICollection<KeyValuePair<string, StreamSession>>)_sessions)
                .Remove(new KeyValuePair<string, StreamSession>(channelId, existingSession));
            await StopSessionAsync(existingSession);
            existingSession = null;
        }

        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var sessionPath = Path.Combine(_hlsOutputPath, sessionId);
        StreamSession? candidate = null;

        try
        {
            Directory.CreateDirectory(sessionPath);

            var ffmpegPath = GetFFmpegPath();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                try { Directory.Delete(sessionPath, true); } catch { }
                return new StreamResult
                {
                    Success = false,
                    Error = "FFmpeg not found. Please install FFmpeg."
                };
            }

            if (normalize && !SupportsH264Encoder(ffmpegPath))
            {
                try { Directory.Delete(sessionPath, true); } catch { }
                return new StreamResult
                {
                    Success = false,
                    Error = "FFmpeg is available, but the libx264 encoder is not installed. Disable HLS normalization or install an FFmpeg build with libx264."
                };
            }

            // Build FFmpeg arguments for HLS output
            var playlistPath = Path.Combine(sessionPath, "playlist.m3u8");
            var arguments = BuildHlsArguments(streamUrl, playlistPath, userAgent, normalize);

            _logger.LogInformation("[Stream] Starting FFmpeg for channel {ChannelId}: {Args}", channelId, string.Join(" ", arguments));

            var processInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            // Pass each token as a discrete argv element. Building a single Arguments string
            // and embedding the (attacker-influenceable) stream URL / user-agent in quotes
            // allowed an embedded quote to break out and inject arbitrary ffmpeg options.
            foreach (var arg in arguments)
            {
                processInfo.ArgumentList.Add(arg);
            }

            var process = new Process { StartInfo = processInfo };
            process.Start();
            var stderrTask = DrainStandardErrorAsync(process, channelId);

            candidate = new StreamSession
            {
                SessionId = sessionId,
                ChannelId = channelId,
                StreamUrl = streamUrl,
                Normalize = normalize,
                Process = process,
                StandardErrorTask = stderrTask,
                OutputPath = sessionPath,
                PlaylistPath = playlistPath,
                StartTime = DateTime.UtcNow,
                ViewerLeases = new StreamViewerLeaseSet(ViewerLeaseTimeout)
            };
            var leaseId = candidate.ViewerLeases.Acquire();

            // Wait for playlist to be created (with timeout)
            var waitStart = DateTime.UtcNow;
            while (!File.Exists(playlistPath) && (DateTime.UtcNow - waitStart).TotalSeconds < 10)
            {
                if (process.HasExited)
                {
                    var stderr = await stderrTask;
                    _logger.LogError("[Stream] FFmpeg exited early: {Error}", stderr);
                    await StopSessionAsync(candidate);
                    candidate = null;
                    return new StreamResult
                    {
                        Success = false,
                        Error = $"FFmpeg failed to start: {stderr.Substring(0, Math.Min(500, stderr.Length))}"
                    };
                }
                await Task.Delay(200);
            }

            if (!File.Exists(playlistPath))
            {
                // Reporting success here handed back a playlist URL that does
                // not exist, so the player just retried a 404 while FFmpeg went
                // on running and holding an upstream connection. Stopping also
                // removes the session directory, which otherwise piled up one
                // empty folder per failed attempt.
                _logger.LogWarning("[Stream] Playlist not created within timeout for channel {ChannelId}", channelId);
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    var stderr = await stderrTask;
                    _logger.LogWarning(
                        "[Stream] FFmpeg output before playlist timeout: {Error}",
                        stderr[..Math.Min(500, stderr.Length)]);
                }
                catch { }
                await StopSessionAsync(candidate);
                candidate = null;

                return new StreamResult
                {
                    Success = false,
                    Error = "The stream produced no playlist within ten seconds. The channel may be offline or the source may be refusing the connection."
                };
            }

            _sessions[channelId] = candidate;
            candidate.MonitorTask = MonitorStreamAsync(candidate);
            candidate.LeaseMonitorTask = MonitorViewerLeasesAsync(candidate);

            if (existingSession != null)
            {
                await StopSessionAsync(existingSession);
            }

            return new StreamResult
            {
                Success = true,
                SessionId = sessionId,
                LeaseId = leaseId,
                PlaylistUrl = $"/api/v1/stream/{sessionId}/playlist.m3u8"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Stream] Failed to start stream for channel {ChannelId}", channelId);

            if (candidate != null)
            {
                await StopSessionAsync(candidate);
            }
            else
            {
                try { Directory.Delete(sessionPath, true); } catch { }
            }

            return new StreamResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Stop a streaming session
    /// </summary>
    public Task StopStreamAsync(string channelId) => StopStreamAsync(channelId, expectedSessionId: null);

    /// <summary>
    /// Stop the channel's stream. With a session id, only that session.
    /// </summary>
    /// <remarks>
    /// The failure paths in the start flow pass their own session id. A blind
    /// removal there let a start that lost its race tear down the healthy
    /// stream another request had just put up for the same channel.
    /// </remarks>
    public async Task StopStreamAsync(string channelId, string? expectedSessionId)
        => await StopStreamAsync(channelId, expectedSessionId, leaseId: null);

    /// <summary>
    /// Release one viewer. The shared process stops after the final viewer leaves.
    /// A missing lease keeps the force-stop behavior used by service maintenance.
    /// </summary>
    public async Task StopStreamAsync(string channelId, string? expectedSessionId, string? leaseId)
    {
        if (!_sessions.ContainsKey(channelId))
        {
            return;
        }

        var channelLock = _channelLocks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
        await channelLock.WaitAsync();
        try
        {
            if (!_sessions.TryGetValue(channelId, out var session)
                || (expectedSessionId != null && session.SessionId != expectedSessionId))
            {
                return;
            }

            if (leaseId != null && session.ViewerLeases.Release(leaseId) > 0)
            {
                _logger.LogDebug(
                    "[Stream] Keeping shared stream for channel {ChannelId}; {ViewerCount} viewers remain",
                    channelId,
                    session.ViewerLeases.Count);
                return;
            }

            if (!((ICollection<KeyValuePair<string, StreamSession>>)_sessions)
                    .Remove(new KeyValuePair<string, StreamSession>(channelId, session)))
            {
                return;
            }

            await StopSessionAsync(session);
        }
        finally
        {
            channelLock.Release();
        }
    }

    public bool RefreshViewerLease(string channelId, string sessionId, string leaseId)
    {
        return _sessions.TryGetValue(channelId, out var session)
            && session.SessionId == sessionId
            && session.ViewerLeases.Refresh(leaseId);
    }

    private async Task StopSessionAsync(StreamSession session)
    {
        try
        {
            session.LeaseMonitorCancellation.Cancel();
            _logger.LogInformation("[Stream] Stopping stream for channel {ChannelId}", session.ChannelId);

            if (!session.Process.HasExited)
            {
                // Try graceful shutdown first
                try
                {
                    session.Process.CloseMainWindow();
                    if (!session.Process.WaitForExit(3000))
                    {
                        session.Process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    try { session.Process.Kill(entireProcessTree: true); } catch { }
                }
            }

            if (session.MonitorTask != null)
            {
                await session.MonitorTask;
            }
            else
            {
                await session.StandardErrorTask;
            }

            session.Process.Dispose();

            // Clean up session files
            await Task.Delay(500); // Give filesystem time to release files
            try
            {
                if (Directory.Exists(session.OutputPath))
                {
                    Directory.Delete(session.OutputPath, true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Stream] Failed to clean up session files for {ChannelId}", session.ChannelId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Stream] Error stopping stream for channel {ChannelId}", session.ChannelId);
        }
    }

    /// <summary>
    /// Get the path to an HLS file for a session
    /// </summary>
    public string? GetHlsFilePath(string sessionId, string filename)
    {
        var session = _sessions.Values.FirstOrDefault(s => s.SessionId == sessionId);
        if (session == null) return null;

        // The route serving this is anonymous by necessity, because HLS.js
        // fetches segments itself and cannot send the API key. Path.Combine
        // will happily build a path outside the session folder from a name
        // that carries a separator, so the name has to be a plain one and the
        // result has to land inside the folder.
        if (string.IsNullOrEmpty(filename) ||
            filename.Contains("..", StringComparison.Ordinal) ||
            filename.Contains('/') || filename.Contains('\\') ||
            Path.IsPathRooted(filename))
        {
            _logger.LogWarning("[HLSStream] Refusing segment name {Filename} for session {SessionId}", filename, sessionId);
            return null;
        }

        var sessionRoot = Path.GetFullPath(session.OutputPath)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var filePath = Path.GetFullPath(Path.Combine(session.OutputPath, filename));

        if (!filePath.StartsWith(sessionRoot, StringComparison.Ordinal))
        {
            _logger.LogWarning("[HLSStream] Refusing segment {Filename}: it resolves outside the session folder", filename);
            return null;
        }

        return File.Exists(filePath) ? filePath : null;
    }

    /// <summary>
    /// Check if a session is active
    /// </summary>
    public bool IsSessionActive(string sessionId)
    {
        var session = _sessions.Values.FirstOrDefault(s => s.SessionId == sessionId);
        return session?.IsActive ?? false;
    }

    /// <summary>
    /// Get all active sessions
    /// </summary>
    public List<StreamSessionInfo> GetActiveSessions()
    {
        return _sessions.Values
            .Where(s => s.IsActive)
            .Select(s => new StreamSessionInfo
            {
                SessionId = s.SessionId,
                ChannelId = s.ChannelId,
                Normalize = s.Normalize,
                ViewerCount = s.ViewerLeases.Count,
                StartTime = s.StartTime,
                DurationSeconds = (int)(DateTime.UtcNow - s.StartTime).TotalSeconds
            })
            .ToList();
    }

    // Returns ffmpeg arguments as discrete argv tokens (one element per token, values NOT
    // quoted) for ProcessStartInfo.ArgumentList. .NET quotes/escapes each element, so the
    // stream URL and user-agent cannot inject extra ffmpeg options.
    private List<string> BuildHlsArguments(
        string streamUrl,
        string playlistPath,
        string? userAgent,
        bool normalize)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-y"  // Overwrite output
        };

        // User agent
        args.Add("-user_agent");
        args.Add(string.IsNullOrEmpty(userAgent) ? "VLC/3.0.18 LibVLC/3.0.18" : userAgent);

        // Connection options for live streams
        args.Add("-reconnect"); args.Add("1");
        args.Add("-reconnect_streamed"); args.Add("1");
        args.Add("-reconnect_delay_max"); args.Add("5");
        args.Add("-timeout"); args.Add("10000000"); // 10 second timeout in microseconds

        // Input
        args.Add("-i"); args.Add(streamUrl);

        if (normalize)
        {
            // Normalize the live feed instead of copying its compressed video. Some
            // IPTV relays begin with incomplete H.264 access units (for example,
            // without the PPS needed to decode them). VLC can recover from that,
            // but stream-copy HLS preserves the damaged cadence and can produce
            // long, irregular segments that browser players cannot consume
            // smoothly. Selecting the first A/V pair also keeps non-media streams
            // such as subtitles or data tracks out of the MPEG-TS output.
            args.Add("-map"); args.Add("0:v:0");
            args.Add("-map"); args.Add("0:a:0?");
            args.Add("-c:v"); args.Add("libx264");
            args.Add("-preset"); args.Add("ultrafast");
            args.Add("-tune"); args.Add("zerolatency");
            args.Add("-g"); args.Add("120"); // Maximum GOP for 59.94fps sources
            args.Add("-keyint_min"); args.Add("1");
            args.Add("-sc_threshold"); args.Add("0");
            args.Add("-force_key_frames"); args.Add("expr:gte(t,n_forced*2)");
            args.Add("-pix_fmt"); args.Add("yuv420p");
            args.Add("-c:a"); args.Add("aac");
        }
        else
        {
            args.Add("-c"); args.Add("copy");
        }

        // HLS output options
        args.Add("-f"); args.Add("hls");
        args.Add("-hls_time"); args.Add("2");           // 2 second segments
        args.Add("-hls_list_size"); args.Add(HlsPlaylistSize.ToString()); // Keep enough segments to absorb live-source jitter
        args.Add("-hls_flags"); args.Add("delete_segments+append_list+omit_endlist");
        args.Add("-hls_segment_type"); args.Add("mpegts");
        args.Add("-hls_segment_filename");
        args.Add(Path.Combine(Path.GetDirectoryName(playlistPath)!, "segment%03d.ts"));

        // Output playlist
        args.Add(playlistPath);

        return args;
    }

    private async Task MonitorStreamAsync(StreamSession session)
    {
        try
        {
            await session.Process.WaitForExitAsync();
            await session.StandardErrorTask;

            _logger.LogInformation("[Stream] FFmpeg exited for channel {ChannelId} with code {ExitCode}",
                session.ChannelId, session.Process.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Stream] Error monitoring stream for channel {ChannelId}", session.ChannelId);
        }
    }

    private async Task MonitorViewerLeasesAsync(StreamSession session)
    {
        while (!session.LeaseMonitorCancellation.IsCancellationRequested && session.IsActive)
        {
            try
            {
                await Task.Delay(ViewerLeaseSweepInterval, session.LeaseMonitorCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var removed = session.ViewerLeases.RemoveExpired();
            if (removed > 0)
            {
                _logger.LogDebug(
                    "[Stream] Expired {LeaseCount} abandoned viewer leases for channel {ChannelId}",
                    removed,
                    session.ChannelId);
            }

            if (session.ViewerLeases.Count > 0)
            {
                continue;
            }

            var channelLock = _channelLocks.GetOrAdd(session.ChannelId, _ => new SemaphoreSlim(1, 1));
            await channelLock.WaitAsync();
            try
            {
                if (!_sessions.TryGetValue(session.ChannelId, out var current)
                    || !ReferenceEquals(current, session))
                {
                    return;
                }

                if (session.ViewerLeases.Count > 0)
                {
                    continue;
                }

                ((ICollection<KeyValuePair<string, StreamSession>>)_sessions)
                    .Remove(new KeyValuePair<string, StreamSession>(session.ChannelId, session));
            }
            finally
            {
                channelLock.Release();
            }

            await StopSessionAsync(session);
            return;
        }
    }

    private async Task<string> DrainStandardErrorAsync(Process process, string channelId)
    {
        var captured = new System.Text.StringBuilder(500);
        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            if (captured.Length < 500)
            {
                var remaining = 500 - captured.Length;
                captured.AppendLine(line[..Math.Min(remaining, line.Length)]);
            }

            if (line.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("[Stream] Channel {ChannelId}: {Message}", channelId, line);
            }
        }

        return captured.ToString();
    }

    private string? GetFFmpegPath()
    {
        var possiblePaths = new[]
        {
            "ffmpeg",
            "/usr/bin/ffmpeg",
            "/usr/local/bin/ffmpeg",
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")
        };

        foreach (var path in possiblePaths)
        {
            if ((path == "ffmpeg" || File.Exists(path)) && IsFfmpegAvailable(path))
            {
                return path;
            }
        }

        return null;
    }

    private static bool IsFfmpegAvailable(string executablePath)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            processInfo.ArgumentList.Add("-hide_banner");
            processInfo.ArgumentList.Add("-version");

            using var process = Process.Start(processInfo);
            if (process == null || !process.WaitForExit(5000))
            {
                try { process?.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool SupportsH264Encoder(string executablePath)
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        processInfo.ArgumentList.Add("-hide_banner");
        processInfo.ArgumentList.Add("-encoders");
        return SupportsH264Encoder(processInfo);
    }

    private static bool SupportsH264Encoder(ProcessStartInfo processInfo)
    {
        using var process = Process.Start(processInfo);
        if (process == null)
            return false;

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return false;
        }

        Task.WaitAll(outputTask, errorTask);
        return process.ExitCode == 0
            && HasH264Encoder($"{outputTask.Result}\n{errorTask.Result}");
    }

    private static bool HasH264Encoder(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Contains(" libx264 ", StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop all streams
        foreach (var channelId in _sessions.Keys.ToList())
        {
            StopStreamAsync(channelId).GetAwaiter().GetResult();
        }

        foreach (var channelLock in _channelLocks.Values)
        {
            channelLock.Dispose();
        }
        _channelLocks.Clear();

        // Clean up HLS directory
        try
        {
            if (Directory.Exists(_hlsOutputPath))
            {
                Directory.Delete(_hlsOutputPath, true);
            }
        }
        catch { }
    }
}

/// <summary>
/// Represents an active streaming session
/// </summary>
internal class StreamSession
{
    public required string SessionId { get; set; }
    public required string ChannelId { get; set; }
    public required string StreamUrl { get; set; }
    public bool Normalize { get; set; }
    public required Process Process { get; set; }
    public required Task<string> StandardErrorTask { get; set; }
    public Task? MonitorTask { get; set; }
    public Task? LeaseMonitorTask { get; set; }
    public CancellationTokenSource LeaseMonitorCancellation { get; } = new();
    public required string OutputPath { get; set; }
    public required string PlaylistPath { get; set; }
    public DateTime StartTime { get; set; }
    public StreamViewerLeaseSet ViewerLeases { get; init; } = new();

    public bool IsActive => !Process.HasExited;
}

/// <summary>
/// Result of starting a stream
/// </summary>
public class StreamResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? SessionId { get; set; }
    public string? LeaseId { get; set; }
    public string? PlaylistUrl { get; set; }
}

/// <summary>
/// Public session info
/// </summary>
public class StreamSessionInfo
{
    public required string SessionId { get; set; }
    public required string ChannelId { get; set; }
    public bool Normalize { get; set; }
    public int ViewerCount { get; set; }
    public DateTime StartTime { get; set; }
    public int DurationSeconds { get; set; }
}

internal sealed class StreamViewerLeaseSet
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _leases = [];
    private readonly TimeSpan _timeout;
    private readonly Func<DateTime> _utcNow;

    public StreamViewerLeaseSet(TimeSpan? timeout = null, Func<DateTime>? utcNow = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(90);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public int Count
    {
        get
        {
            lock (_gate) return _leases.Count;
        }
    }

    public string Acquire()
    {
        lock (_gate)
        {
            string leaseId;
            do
            {
                leaseId = Guid.NewGuid().ToString("N");
            } while (!_leases.TryAdd(leaseId, _utcNow()));

            return leaseId;
        }
    }

    public int Release(string leaseId)
    {
        lock (_gate)
        {
            _leases.Remove(leaseId);
            return _leases.Count;
        }
    }

    public bool Refresh(string leaseId)
    {
        lock (_gate)
        {
            if (!_leases.ContainsKey(leaseId)) return false;
            _leases[leaseId] = _utcNow();
            return true;
        }
    }

    public int RemoveExpired()
    {
        lock (_gate)
        {
            var cutoff = _utcNow() - _timeout;
            var expired = _leases
                .Where(lease => lease.Value <= cutoff)
                .Select(lease => lease.Key)
                .ToList();
            foreach (var leaseId in expired)
            {
                _leases.Remove(leaseId);
            }
            return expired.Count;
        }
    }
}
