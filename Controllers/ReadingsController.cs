using Microsoft.AspNetCore.Mvc;
using InfluxDB.Client;

namespace GUIVideoProcessing.Web.Controllers;

[ApiController]
public class ReadingsController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly ILogger<ReadingsController> _logger;

    public ReadingsController(IConfiguration config, ILogger<ReadingsController> logger)
    {
        _config = config;
        _logger = logger;
    }

    [HttpGet("/api/readings")]
    public async Task<IActionResult> GetReadings([FromQuery] int hours = 1, CancellationToken ct = default)
    {
        if (hours < 1 || hours > 24) hours = 1;

        if (!_config.GetValue<bool>("InfluxDB:Enabled"))
            return Ok(new { items = Array.Empty<object>(), error = (string?)null });

        string url    = _config["InfluxDB:Url"]    ?? "http://localhost:8086";
        string token  = _config["InfluxDB:Token"]  ?? "";
        string org    = _config["InfluxDB:Org"]    ?? "";
        string bucket = _config["InfluxDB:Bucket"] ?? "";

        var flux = $"""
            from(bucket: "{bucket}")
              |> range(start: -{hours}h)
              |> filter(fn: (r) => r._measurement == "readings" and r._field == "value")
              |> sort(columns: ["_time"])
            """;

        var items = new List<object>();
        try
        {
            using var client = InfluxDBClientFactory.Create(url, token.ToCharArray());
            var queryApi = client.GetQueryApi();
            var tables = await queryApi.QueryAsync(flux, org, ct);

            foreach (var table in tables)
                foreach (var record in table.Records)
                {
                    var time = record.GetTime();
                    if (time == null) continue;
                    items.Add(new
                    {
                        timestamp = time.Value.ToDateTimeUtc().ToString("o"),
                        value     = record.GetValue()?.ToString() ?? ""
                    });
                }

            return Ok(new { items, error = (string?)null });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("ReadingsController: InfluxDB error: {Msg}", ex.Message);
            return Ok(new { items = Array.Empty<object>(), error = "Databáza nie je dostupná" });
        }
    }
}
