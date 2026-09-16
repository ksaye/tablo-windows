using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TabloWindows.Models;

namespace TabloWindows.Services;

/// <summary>
/// Talks to the Tablo cloud (lighthousetv.ewscloud.com) to authenticate, then to the
/// local Gen-4 device on port 8887 with HMAC-MD5 signed requests. Protocol verified
/// live against a Tablo 4G QUAD (firmware 2.2.58).
/// </summary>
public sealed class TabloClient
{
    private const string LighthouseHost = "https://lighthousetv.ewscloud.com";
    // Shared client keys the Gen-4 apps sign with. Not per-account secrets — they are the
    // app's identity; the per-account gate is the Lighthouse token added on top.
    private const string HashKey = "6l8jU5N43cEilqItmT3U2M2PFM3qPziilXqau9ys";
    private const string DeviceKey = "ljpg6ZkwShVv8aI12E2LP55Ep8vq1uYDPvX0DdTB";
    private const string DeviceUserAgent = "Tablo-FAST/1.7.0 (Mobile; iPhone; iOS 18.4)";
    private const string CloudUserAgent = "Tablo-FAST/2.0.0 (Mobile; iPhone; iOS 16.6)";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Bypass any system HTTP proxy: Tablo devices are always LAN-local, and the cloud is
    // reachable directly on a normal home network. A configured proxy would route the LAN
    // device IP to the proxy and fail (cloud login works, every device call 000s).
    private static HttpClient NoProxyClient(int timeoutSeconds) =>
        new(new SocketsHttpHandler { UseProxy = false, Proxy = null })
        { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

    private readonly HttpClient _http = NoProxyClient(30);
    // Short-timeout client just for probing device reachability at startup.
    private static readonly HttpClient _probe = NoProxyClient(6);

    private string? _cloudAuthorization;   // "Bearer xxx"
    private string? _lighthouse;           // per device+profile token

    public AccountResponse? Account { get; private set; }
    public TabloDevice? Device { get; private set; }
    public ServerInfo? ServerInfo { get; private set; }

    private string DeviceBase => Device?.Url.TrimEnd('/')
        ?? throw new InvalidOperationException("No device selected.");

    /// <summary>
    /// Is this device actually reachable on the LAN? On port 8887 /server/info answers
    /// unauthenticated, so any HTTP response means the box is online. Used to skip dead
    /// devices that are still listed on the account (e.g. returned/defective units).
    /// </summary>
    public static async Task<ServerInfo?> ProbeAsync(TabloDevice device, CancellationToken ct = default)
    {
        var url = device.Url.TrimEnd('/') + "/server/info";
        // Retry a few times: some endpoint-security products refuse an unknown app's first
        // connection while they evaluate it, so a single failure doesn't mean "offline".
        for (int i = 0; i < 4; i++)
        {
            try
            {
                // The User-Agent is not optional. /server/info needs no authentication, but
                // firmware 2.2.58 answers 403 to a request that carries no User-Agent at all —
                // and HttpClient sends none by default. Without this the probe reports every
                // device as offline and the app says "No Tablo responding" on a healthy LAN.
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", DeviceUserAgent);
                req.Headers.Accept.ParseAdd("*/*");
                using var res = await _probe.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                    return JsonSerializer.Deserialize<ServerInfo>(await res.Content.ReadAsStringAsync(ct), Json);
            }
            catch { /* connection refused / timeout / DNS — try again */ }
            if (i < 3) await Task.Delay(350, ct);
        }
        return null;
    }

    // ---------------------------------------------------------------- auth

    /// <summary>Log in and load the account (profiles + devices). Does not pick a device.</summary>
    public async Task LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { password, email });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{LighthouseHost}/api/v2/login/")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("User-Agent", CloudUserAgent);
        req.Headers.Accept.ParseAdd("*/*");

        var login = await SendJsonAsync<LoginResponse>(req, ct);
        if (login is null || login.Code is not null || string.IsNullOrEmpty(login.AccessToken))
            throw new TabloAuthException(login?.Message ?? "Login was not accepted.");

        _cloudAuthorization = $"{login.TokenType} {login.AccessToken}";
        Account = await GetAccountAsync(ct)
                  ?? throw new TabloAuthException("Could not load account details.");
    }

    private async Task<AccountResponse?> GetAccountAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{LighthouseHost}/api/v2/account/");
        req.Headers.TryAddWithoutValidation("User-Agent", CloudUserAgent);
        req.Headers.TryAddWithoutValidation("Authorization", _cloudAuthorization);
        req.Headers.Accept.ParseAdd("*/*");
        return await SendJsonAsync<AccountResponse>(req, ct);
    }

    /// <summary>Select profile+device and mint the Lighthouse token, then read /server/info.</summary>
    public async Task SelectDeviceAsync(Profile profile, TabloDevice device, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { pid = profile.Identifier, sid = device.ServerId });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{LighthouseHost}/api/v2/account/select/")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("User-Agent", CloudUserAgent);
        req.Headers.TryAddWithoutValidation("Authorization", _cloudAuthorization);
        req.Headers.Accept.ParseAdd("*/*");

        var sel = await SendJsonAsync<SelectResponse>(req, ct);
        if (sel?.Token is null) throw new TabloAuthException("Account token was not returned.");

        _lighthouse = sel.Token;
        Device = device;
        ServerInfo = await DeviceGetAsync<ServerInfo>("/server/info", ct);
    }

    // -------------------------------------------------------------- signing

    private static string DeviceDate() =>
        DateTime.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture);

    private static string Md5Hex(string s)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string DeviceAuth(string method, string path, string msg, string date)
    {
        var body = string.IsNullOrEmpty(msg) ? "" : Md5Hex(msg);
        var full = $"{method}\n{path}\n{body}\n{date}";
        var key = Encoding.UTF8.GetBytes(HashKey);
        var mac = new HMACMD5(key).ComputeHash(Encoding.UTF8.GetBytes(full));
        return $"tablo:{DeviceKey}:{Convert.ToHexString(mac).ToLowerInvariant()}";
    }

    private HttpRequestMessage BuildDeviceRequest(HttpMethod method, string path, string? jsonBody)
    {
        var date = DeviceDate();
        var msg = jsonBody ?? "";
        var req = new HttpRequestMessage(method, DeviceBase + path);
        // Attach the body for any method that carries one (POST/PATCH/PUT). The auth header
        // is signed over md5(body), so the body MUST actually be sent or the device 401s.
        if (jsonBody is not null && method != HttpMethod.Get && method != HttpMethod.Delete)
            req.Content = new StringContent(msg, Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("Date", date);
        req.Headers.TryAddWithoutValidation("User-Agent", DeviceUserAgent);
        req.Headers.Accept.ParseAdd("*/*");
        req.Headers.TryAddWithoutValidation("Authorization", DeviceAuth(method.Method, path, msg, date));
        req.Headers.TryAddWithoutValidation("Lighthouse", _lighthouse);
        return req;
    }

    /// <summary>
    /// Send a signed device request, rebuilding it (fresh Date + signature) on each try, and
    /// retry transient connection failures. Some endpoint-security products refuse the FIRST
    /// connection from an unknown app while they evaluate it, then allow retries — so a single
    /// "connection refused" shouldn't fail the operation.
    /// </summary>
    private async Task<HttpResponseMessage> SendDeviceAsync(
        HttpMethod method, string path, string? jsonBody, CancellationToken ct, int attempts = 5)
    {
        for (int i = 0; ; i++)
        {
            var req = BuildDeviceRequest(method, path, jsonBody);
            try
            {
                return await _http.SendAsync(req, ct);
            }
            catch (HttpRequestException) when (i < attempts - 1)
            {
                req.Dispose();
                await Task.Delay(300, ct);
            }
            catch (TaskCanceledException) when (i < attempts - 1 && !ct.IsCancellationRequested)
            {
                req.Dispose();   // per-request timeout, not a caller cancel
                await Task.Delay(300, ct);
            }
        }
    }

    private async Task<T?> DeviceGetAsync<T>(string path, CancellationToken ct)
    {
        using var res = await SendDeviceAsync(HttpMethod.Get, path, null, ct);
        return await ReadJsonAsync<T>(res, ct);
    }

    private async Task<string> DeviceGetRawAsync(string path, CancellationToken ct)
    {
        using var res = await SendDeviceAsync(HttpMethod.Get, path, null, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    private async Task<T?> DevicePostAsync<T>(string path, string jsonBody, CancellationToken ct)
    {
        using var res = await SendDeviceAsync(HttpMethod.Post, path, jsonBody, ct);
        return await ReadJsonAsync<T>(res, ct);
    }

    // -------------------------------------------------------------- content

    /// <summary>List recording airing paths (individual recorded programs).</summary>
    public async Task<List<string>> GetRecordingAiringPathsAsync(CancellationToken ct = default) =>
        await DeviceGetAsync<List<string>>("/recordings/airings", ct) ?? new();

    /// <summary>Resolve a set of paths to their detail objects via /batch.</summary>
    public async Task<Dictionary<string, JsonElement>> BatchAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var list = paths.ToList();
        if (list.Count == 0) return new();
        var result = new Dictionary<string, JsonElement>();
        // Device caps batch size; chunk to be safe.
        foreach (var chunk in Chunk(list, 50))
        {
            var json = JsonSerializer.Serialize(chunk);
            var doc = await DevicePostAsync<JsonElement>("/batch", json, ct);
            if (doc is { ValueKind: JsonValueKind.Object } el)
                foreach (var prop in el.EnumerateObject())
                    result[prop.Name] = prop.Value.Clone();
        }
        return result;
    }

    public async Task<List<RecordingAiring>> GetRecordingsAsync(CancellationToken ct = default)
    {
        var paths = await GetRecordingAiringPathsAsync(ct);
        var batch = await BatchAsync(paths, ct);
        var recs = new List<RecordingAiring>();
        foreach (var kv in batch)
        {
            try
            {
                var r = kv.Value.Deserialize<RecordingAiring>(Json);
                if (r is not null) recs.Add(r);
            }
            catch { /* skip malformed */ }
        }
        return recs
            .OrderByDescending(r => ParseDate(r.AiringDetails.Datetime))
            .ToList();
    }

    public async Task<List<GuideChannelWrap>> GetChannelsAsync(CancellationToken ct = default)
    {
        var paths = await DeviceGetAsync<List<string>>("/guide/channels", ct) ?? new();
        var batch = await BatchAsync(paths, ct);
        var chans = new List<GuideChannelWrap>();
        foreach (var kv in batch)
        {
            try
            {
                var c = kv.Value.Deserialize<GuideChannelWrap>(Json);
                if (c is not null) chans.Add(c);
            }
            catch { /* skip */ }
        }
        return chans
            .OrderBy(c => c.Channel.Major).ThenBy(c => c.Channel.Minor)
            .ToList();
    }

    // ---- FAST (free streaming) channels ----

    /// <summary>
    /// Synthetic path prefix for a FAST channel. FAST channels have no object on the device,
    /// so they get a path of their own that the rest of the app can carry around exactly like
    /// a "/guide/channels/NNN" one.
    /// </summary>
    public const string FastChannelPrefix = "/fast/channels/";

    /// <summary>Synthetic path prefix for a FAST airing (see <see cref="FastChannelPrefix"/>).</summary>
    public const string FastAiringPrefix = "/fast/airings/";

    /// <summary>True for the synthetic paths above — i.e. anything that isn't on the DVR.</summary>
    public static bool IsFast(string? path) =>
        path is not null && path.StartsWith("/fast/", StringComparison.Ordinal);

    // Channel path -> the partner CDN's HLS playlist. Filled in by GetFastChannelsAsync and
    // read by WatchAsync, which is how a FAST channel plays through the same code path as a
    // tuner channel.
    private readonly Dictionary<string, string> _fastStreams = new(StringComparer.Ordinal);

    /// <summary>
    /// The account's whole channel lineup as the cloud sees it — antenna channels AND the free
    /// streaming ones. The device's own /guide/channels only ever lists what its tuners scanned.
    /// </summary>
    public async Task<List<LineupChannel>> GetCloudLineupAsync(CancellationToken ct = default) =>
        await CloudGetAsync<List<LineupChannel>>(
            $"/api/v2/account/{_lighthouse}/guide/channels/", ct) ?? new();

    /// <summary>
    /// The FAST channels, shaped like device channels so callers can merge them straight into
    /// a channel list. Also caches each channel's stream URL for <see cref="WatchAsync"/>.
    /// </summary>
    public async Task<List<GuideChannelWrap>> GetFastChannelsAsync(CancellationToken ct = default)
    {
        var list = new List<GuideChannelWrap>();
        foreach (var c in await GetCloudLineupAsync(ct))
        {
            if (!string.Equals(c.Kind, "ott", StringComparison.OrdinalIgnoreCase) || c.Ott is null)
                continue;

            var path = FastChannelPrefix + c.Identifier;
            if (!string.IsNullOrWhiteSpace(c.Ott.StreamUrl))
                _fastStreams[path] = FillStreamMacros(c.Ott.StreamUrl!);

            list.Add(new GuideChannelWrap
            {
                Path = path,
                Channel = new Channel
                {
                    // The lineup's callSign is a slug ("welcomehome"); the name is what the
                    // official app shows, and it is what belongs in a channel column.
                    CallSign = string.IsNullOrWhiteSpace(c.Name) ? c.Ott.CallSign ?? "" : c.Name,
                    Name = c.Name,
                    Major = c.Ott.Major,
                    Minor = c.Ott.Minor,
                    Network = c.Ott.Network,
                    Logos = c.Logos
                }
            });
        }
        return list
            .OrderBy(c => c.Channel.Major).ThenBy(c => c.Channel.Minor)
            .ToList();
    }

    /// <summary>
    /// Every channel worth showing: the antenna channels the device scanned, followed by the
    /// account's FAST channels. If the cloud lineup can't be read, the antenna channels are
    /// still returned — losing the streaming channels must never cost you the DVR.
    /// </summary>
    public async Task<List<GuideChannelWrap>> GetAllChannelsAsync(CancellationToken ct = default)
    {
        var ota = await GetChannelsAsync(ct);
        try { return ota.Concat(await GetFastChannelsAsync(ct)).ToList(); }
        catch { return ota; }
    }

    /// <summary>
    /// The FAST guide: one cloud call per channel per day, shaped like device guide airings.
    ///
    /// Listings run about a fortnight out and are keyed by day, so the whole grid is
    /// channels × days requests. They go to the cloud rather than the little appliance, so a
    /// handful in flight is fine — but the guide is still cached by every caller.
    /// </summary>
    /// <param name="channels">FAST channels from <see cref="GetFastChannelsAsync"/>.</param>
    /// <param name="days">Days ahead to fetch. The cloud has roughly 14.</param>
    public async Task<List<GuideAiring>> GetFastGuideAiringsAsync(
        IReadOnlyList<GuideChannelWrap> channels, int days = 14,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // Start a day back: the cloud files an airing under the day its listing block belongs
        // to, which is not always the local day it starts in, and a programme running over
        // midnight has to appear on both.
        var dates = Enumerable.Range(-1, days + 1)
            .Select(d => DateTime.UtcNow.Date.AddDays(d).ToString("yyyy-MM-dd"))
            .ToList();

        var jobs = channels.SelectMany(c => dates.Select(d => (Channel: c, Date: d))).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<GuideAiring>();
        var done = 0;
        var sync = new object();

        using var gate = new SemaphoreSlim(8);
        var tasks = jobs.Select(async job =>
        {
            await gate.WaitAsync(ct);
            try
            {
                List<CloudAiring>? day = null;
                try
                {
                    day = await CloudGetAsync<List<CloudAiring>>(
                        $"/api/v2/account/guide/channels/{job.Channel.Path[FastChannelPrefix.Length..]}" +
                        $"/airings/{job.Date}/", ct);
                }
                catch { /* one channel-day missing beats no guide at all */ }

                lock (sync)
                {
                    foreach (var a in day ?? new())
                        if (seen.Add(a.Identifier))
                            results.Add(ToGuideAiring(a, job.Channel.Path));
                    done++;
                    progress?.Report(Math.Min(1, (double)done / jobs.Count));
                }
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);

        return results;
    }

    /// <summary>Reshape a cloud airing as a device-style guide airing.</summary>
    private static GuideAiring ToGuideAiring(CloudAiring a, string channelPath)
    {
        var isMovie = string.Equals(a.Kind, "movieAiring", StringComparison.OrdinalIgnoreCase);
        return new GuideAiring
        {
            Path = FastAiringPrefix + a.Identifier,
            AiringDetails = new AiringDetails
            {
                Datetime = a.Datetime,
                Duration = a.Duration,
                ChannelPath = channelPath,
                ShowTitle = a.Show?.Title ?? a.Title
            },
            MovieAiring = isMovie
                ? new MovieInfo
                {
                    Title = a.Show?.Title ?? a.Title,
                    Description = a.Description,
                    ReleaseYear = a.MovieAiring?.ReleaseYear
                }
                : null,
            Episode = isMovie
                ? null
                : new EpisodeInfo
                {
                    // The cloud's "title" is the episode within "show" — except on the channels
                    // that just repeat the show title, where carrying it would print the same
                    // words twice as programme and sub-heading.
                    Title = string.Equals(a.Title, a.Show?.Title, StringComparison.Ordinal) ? null : a.Title,
                    Description = a.Description,
                    Number = a.Episode?.EpisodeNumber,
                    SeasonNumber = a.Episode?.Season?.Number,
                    OrigAirDate = a.Episode?.OriginalAirDate
                },
            // Nothing on a FAST channel can be scheduled — see ScheduleAiringAsync.
            Schedule = new ScheduleInfo { State = "none" }
        };
    }

    /// <summary>
    /// FAST playlist URLs carry REPLACE_ME placeholders for the advertising parameters the
    /// official apps fill in. The stream plays with them simply blanked out.
    /// </summary>
    private static string FillStreamMacros(string url) => url.Replace("REPLACE_ME", "");

    private async Task<T?> CloudGetAsync<T>(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LighthouseHost + path);
        req.Headers.TryAddWithoutValidation("User-Agent", CloudUserAgent);
        req.Headers.TryAddWithoutValidation("Authorization", _cloudAuthorization);
        if (_lighthouse is not null) req.Headers.TryAddWithoutValidation("Lighthouse", _lighthouse);
        req.Headers.Accept.ParseAdd("*/*");
        return await SendJsonAsync<T>(req, ct);
    }

    // ---- Guide + scheduling ----

    /// <summary>
    /// Load the full guide (all airings, ~14 days) and resolve details via concurrent /batch
    /// calls (device caps each batch at 50). Reports progress 0..1. Cached per instance.
    /// </summary>
    /// <param name="stats">
    /// Optional: filled in with how much of the guide actually came back. A busy device drops
    /// batches, and a caller that caches the result needs to know it got a partial guide rather
    /// than trusting a third of the listings for the next six hours.
    /// </param>
    public async Task<List<GuideAiring>> GetGuideAiringsAsync(
        IProgress<double>? progress = null, CancellationToken ct = default,
        GuideLoadStats? stats = null)
    {
        var paths = await DeviceGetAsync<List<string>>("/guide/airings", ct) ?? new();
        var chunks = Chunk(paths, 50).ToList();
        var results = new List<GuideAiring>(paths.Count);
        var done = 0;
        var sync = new object();

        var missed = new List<List<string>>();

        // Keep concurrency LOW — the device is a small appliance, and 400 batch POSTs at four
        // at a time saturate its API port badly enough that it starts refusing our own
        // connections (measured 2026-08-17: 154 of 400 batches lost, and port 8887 refusing
        // 20/20 while the load ran, back to 20/20 three minutes after it stopped). Two in
        // flight roughly doubles the wall time of a load that happens once every six hours
        // in the background, and the guide comes back whole.
        using var gate = new SemaphoreSlim(2);

        async Task<bool> RunAsync(List<string> chunk, int attempts)
        {
            JsonElement? doc = null;
            var json = JsonSerializer.Serialize(chunk);
            // Back off properly: a device that just refused a batch is busy, and 200ms
            // later it is still busy. 0.5s, 1.5s, 3s gives it time to drain.
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                try { doc = await DevicePostAsync<JsonElement>("/batch", json, ct); break; }
                catch when (attempt < attempts - 1) { await Task.Delay(500 * ((1 << attempt) + attempt), ct); }
                catch { doc = null; }   // give up on this batch
            }

            var local = new List<GuideAiring>();
            if (doc is { ValueKind: JsonValueKind.Object } el)
                foreach (var prop in el.EnumerateObject())
                {
                    try
                    {
                        var a = prop.Value.Deserialize<GuideAiring>(Json);
                        if (a is not null) { a.Path = prop.Name; local.Add(a); }
                    }
                    catch { /* skip */ }
                }
            lock (sync)
            {
                results.AddRange(local);
                done++;
                progress?.Report(Math.Min(1, (double)done / chunks.Count));
            }
            return doc is not null;
        }

        var tasks = chunks.Select(async chunk =>
        {
            await gate.WaitAsync(ct);
            try
            {
                // Never let one bad batch abort the whole guide — a partial guide is better
                // than none, and the sweep below picks up what this pass lost.
                if (!await RunAsync(chunk, attempts: 4))
                    lock (sync) missed.Add(chunk);
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);

        // Second pass for the batches that never answered. The device refuses connections
        // while it is congested and recovers within a couple of minutes of being left alone,
        // so pause first and then go one at a time — re-running the whole guide later would
        // just repeat the congestion that lost them.
        var stillMissing = 0;
        if (missed.Count > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            foreach (var chunk in missed)
            {
                if (!await RunAsync(chunk, attempts: 3)) stillMissing++;
                await Task.Delay(250, ct);
            }
        }

        if (stats is not null)
        {
            stats.Expected = paths.Count;
            stats.Batches = chunks.Count;
            stats.RecoveredBatches = missed.Count - stillMissing;
            stats.FailedBatches = stillMissing;
        }
        return results;
    }

    /// <summary>Schedule or unschedule a single guide airing.</summary>
    public async Task ScheduleAiringAsync(string airingPath, bool scheduled, CancellationToken ct = default)
    {
        // A FAST channel is not on an antenna, so the DVR has no object to schedule and no way
        // to capture it. Fail with something a caller can show rather than a 404 from the box.
        if (IsFast(airingPath))
            throw new NotSupportedException(
                "Free streaming channels can be watched but not recorded — the DVR only records " +
                "what its tuners receive.");

        var body = JsonSerializer.Serialize(new { scheduled });
        using var res = await SendDeviceAsync(HttpMethod.Patch, airingPath, body, ct);
        if (!res.IsSuccessStatusCode)
            throw new TabloHttpException((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Set a series recording rule: "all", "new", or "none".</summary>
    public async Task SetSeriesRuleAsync(string seriesPath, string rule, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { schedule = new { rule } });
        using var res = await SendDeviceAsync(HttpMethod.Patch, seriesPath, body, ct);
        if (!res.IsSuccessStatusCode)
            throw new TabloHttpException((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Read a single series object (for its current rule).</summary>
    public async Task<GuideSeries?> GetSeriesAsync(string seriesPath, CancellationToken ct = default)
    {
        var batch = await BatchAsync(new[] { seriesPath }, ct);
        return batch.TryGetValue(seriesPath, out var el) ? el.Deserialize<GuideSeries>(Json) : null;
    }

    /// <summary>
    /// Ask the device to start a stream. Works for both recording paths and channel paths —
    /// and for a FAST channel, which streams from the channel partner's CDN and never touches
    /// the DVR or a tuner at all.
    /// </summary>
    public Task<WatchResponse?> WatchAsync(string path, CancellationToken ct = default)
    {
        if (IsFast(path))
            return Task.FromResult<WatchResponse?>(
                _fastStreams.TryGetValue(path.TrimEnd('/'), out var url)
                    ? new WatchResponse { PlaylistUrl = url }
                    // Only happens if a cached path outlived the lineup that produced it.
                    : throw new InvalidOperationException(
                        "That streaming channel is no longer in the lineup. Refresh the guide."));

        return DevicePostAsync<WatchResponse>(path.TrimEnd('/') + "/watch", "", ct);
    }

    /// <summary>Delete a recording (path like /recordings/series/episodes/NNN).</summary>
    public async Task DeleteRecordingAsync(string path, CancellationToken ct = default)
    {
        using var res = await SendDeviceAsync(HttpMethod.Delete, path, null, ct);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Mark a recording protected (Tablo's "keep"), so the DVR does not delete it to make room.
    /// </summary>
    public async Task SetProtectedAsync(string recordingPath, bool isProtected, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { user_info = new { @protected = isProtected } });
        using var res = await SendDeviceAsync(HttpMethod.Patch, recordingPath, body, ct);
        if (!res.IsSuccessStatusCode)
            throw new TabloHttpException((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
    }

    // -------------------------------------------------------------- storage

    /// <summary>
    /// Total/free bytes on the device's recording storage, read from /server/harddrives.
    ///
    /// The exact JSON shape of that endpoint isn't documented and differs across firmware,
    /// so rather than binding to a fixed model we walk the response and pick up the first
    /// plausible capacity/free numbers we find (see <see cref="ScanStorage"/>). Returns null
    /// if the endpoint is missing or nothing recognisable came back — callers must fall back
    /// to a configured capacity in that case.
    /// </summary>
    public async Task<StorageInfo?> GetStorageAsync(CancellationToken ct = default)
    {
        JsonElement doc;
        try
        {
            using var res = await SendDeviceAsync(HttpMethod.Get, "/server/harddrives", null, ct);
            if (!res.IsSuccessStatusCode) return null;
            var text = await res.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text)) return null;
            doc = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch { return null; }

        long total = 0, free = 0;
        ScanStorage(doc, ref total, ref free);
        return total > 0 ? new StorageInfo(total, free, doc) : null;
    }

    /// <summary>
    /// Recursively look for size/free numbers under any of the names firmware has been seen
    /// to use. Values are summed so a multi-drive box reports its whole pool.
    /// </summary>
    private static void ScanStorage(JsonElement el, ref long total, ref long free)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    var name = p.Name.ToLowerInvariant();
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var n))
                    {
                        if (name is "size" or "total" or "capacity" or "total_size" or "size_bytes") total += n;
                        else if (name is "free" or "available" or "remaining" or "free_size" or "free_bytes") free += n;
                    }
                    else ScanStorage(p.Value, ref total, ref free);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) ScanStorage(item, ref total, ref free);
                break;
        }
    }

    /// <summary>
    /// Signed GET returning the raw status and body. For diagnostics: when a typed call comes
    /// back empty it is the only way to see whether the device refused, answered oddly, or
    /// simply returned a shape we don't parse.
    /// </summary>
    public async Task<(int Status, string Body)> DeviceRawGetAsync(string path, CancellationToken ct = default)
    {
        try
        {
            using var res = await SendDeviceAsync(HttpMethod.Get, path, null, ct);
            return ((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            return (0, ex.Message);
        }
    }

    /// <summary>Snapshot image for a recording — served unauthenticated on 8887 at /images/{id}.</summary>
    public string SnapshotUrl(long imageId) => $"{DeviceBase}/images/{imageId}";

    // -------------------------------------------------------------- helpers

    private async Task<T?> SendJsonAsync<T>(HttpRequestMessage req, CancellationToken ct)
    {
        using var res = await _http.SendAsync(req, ct);
        return await ReadJsonAsync<T>(res, ct);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new TabloHttpException((int)res.StatusCode, text);
        if (string.IsNullOrWhiteSpace(text)) return default;
        return JsonSerializer.Deserialize<T>(text, Json);
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> src, int size)
    {
        for (int i = 0; i < src.Count; i += size)
            yield return src.GetRange(i, Math.Min(size, src.Count - i));
    }

    public static DateTime ParseDate(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d : DateTime.MinValue;
}

/// <summary>
/// How complete a guide load was. The device drops /batch calls when it is busy and the load
/// carries on regardless, so the airing count alone cannot tell a quiet night from a device
/// that answered a third of the questions.
/// </summary>
public sealed class GuideLoadStats
{
    /// <summary>Airing paths the device listed — the size of a complete guide.</summary>
    public int Expected { get; set; }
    public int Batches { get; set; }
    /// <summary>Batches that failed the first pass but came back on the later, slower sweep.</summary>
    public int RecoveredBatches { get; set; }
    /// <summary>Batches that never answered, after retries. Their airings are simply missing.</summary>
    public int FailedBatches { get; set; }
    public bool Complete => FailedBatches == 0;
}

public class TabloAuthException(string message) : Exception(message);

public sealed class TabloHttpException(int status, string body)
    : Exception($"Device/cloud returned HTTP {status}: {Trim(body)}")
{
    public int Status { get; } = status;
    private static string Trim(string b) => b.Length > 200 ? b[..200] : b;
}
