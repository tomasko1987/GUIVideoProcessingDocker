using System.Collections.Concurrent;
using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;

namespace GUIVideoProcessing.Web.Services;

public sealed class InfluxWriterService : BackgroundService
{
    // ========================= TYPY =========================

    public sealed record Reading(DateTime TimestampUtc, int? LeftDigit, int? RightDigit, string Value, string Source);

    // ========================= FIELDY =========================

    private readonly ILogger<InfluxWriterService> _logger;
    private readonly IConfiguration _config;

    private readonly ConcurrentQueue<Reading> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);

    // Sledovanie stavu pripojenia — pre logovanie prechodov connected/disconnected
    private bool _connected = false;

    // ========================= KONŠTRUKTOR =========================

    public InfluxWriterService(ILogger<InfluxWriterService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    // ========================= VEREJNÉ API =========================

    public void Enqueue(Reading reading)
    {
        if (!_config.GetValue<bool>("InfluxDB:Enabled"))
            return;

        _queue.Enqueue(reading);
        _signal.Release();
    }

    // ========================= BACKGROUND SLUČKA =========================

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue<bool>("InfluxDB:Enabled"))
        {
            _logger.LogInformation("InfluxWriterService: InfluxDB:Enabled=false, writer not started.");
            return;
        }

        int delaySec = _config.GetValue<int>("InfluxDB:ReconnectDelaySeconds");
        if (delaySec < 1) delaySec = 10;

        string url    = _config["InfluxDB:Url"]    ?? "http://localhost:8086";
        string token  = _config["InfluxDB:Token"]  ?? "";
        string org    = _config["InfluxDB:Org"]    ?? "";
        string bucket = _config["InfluxDB:Bucket"] ?? "";

        _logger.LogInformation("InfluxWriterService: connecting to {Url}, org={Org}, bucket={Bucket}.", url, org, bucket);

        using var client = InfluxDBClientFactory.Create(url, token.ToCharArray());

        // Overenie dostupnosti InfluxDB pri štarte
        await PingAsync(client, url);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WriteLoopAsync(client, org, bucket, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("InfluxWriterService: stopped — application shutting down.");
                return;
            }
            catch (Exception ex)
            {
                if (_connected)
                {
                    _connected = false;
                    _logger.LogWarning("InfluxWriterService: disconnected. Reason: {Reason}. Retry in {Sec}s.",
                        ex.Message, delaySec);
                }
                else
                {
                    _logger.LogWarning("InfluxWriterService: write failed. Reason: {Reason}. Retry in {Sec}s.",
                        ex.Message, delaySec);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySec), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("InfluxWriterService: stopped — application shutting down.");
                return;
            }

            // Ping pred ďalším pokusom o zápis
            await PingAsync(client, url);
        }
    }

    // ========================= ZÁPIS DO DB =========================

    private async Task WriteLoopAsync(InfluxDBClient client, string org, string bucket, CancellationToken token)
    {
        var writeApi = client.GetWriteApiAsync();

        while (!token.IsCancellationRequested)
        {
            await _signal.WaitAsync(token);

            while (_queue.TryDequeue(out var r))
            {
                var point = PointData.Measurement("readings")
                    .Tag("source",        r.Source)
                    .Field("left_digit",  r.LeftDigit)
                    .Field("right_digit", r.RightDigit)
                    .Field("value",       r.Value)
                    .Timestamp(r.TimestampUtc, WritePrecision.Ms);

                await writeApi.WritePointAsync(point, bucket, org, token);

                // Prvý úspešný zápis po štarte alebo po výpadku
                if (!_connected)
                {
                    _connected = true;
                    _logger.LogInformation("InfluxWriterService: connected and writing to {Bucket}.", bucket);
                }
            }
        }
    }

    // ========================= PING =========================

    private async Task PingAsync(InfluxDBClient client, string url)
    {
        try
        {
            bool ok = await client.PingAsync();
            if (ok)
                _logger.LogInformation("InfluxWriterService: ping {Url} OK.", url);
            else
                _logger.LogWarning("InfluxWriterService: ping {Url} failed — server unreachable.", url);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("InfluxWriterService: ping {Url} error — {Reason}.", url, ex.Message);
        }
    }

    // ========================= DISPOSE =========================

    public override void Dispose()
    {
        _signal.Dispose();
        base.Dispose();
    }
}
