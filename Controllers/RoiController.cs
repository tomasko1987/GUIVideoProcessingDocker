using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Nodes;

namespace GUIVideoProcessing.Web.Controllers;

[ApiController]
public class RoiController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly IConfigurationRoot _configRoot;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<RoiController> _logger;

    // static = zdieľaný medzi všetkými inštanciami controllera (každý HTTP request = nová inštancia)
    // Chráni read-modify-write appsettings.json pred súbežnými požiadavkami
    private static readonly object _fileLock = new();

    public RoiController(
        IConfiguration config,
        IWebHostEnvironment env,
        ILogger<RoiController> logger)
    {
        _config     = config;
        _configRoot = (IConfigurationRoot)config;
        _env        = env;
        _logger     = logger;
    }

    /// <summary>
    /// GET /api/roi
    /// Vráti aktuálne hodnoty ROI z appsettings.json (cez IConfiguration).
    /// </summary>
    [HttpGet("/api/roi")]
    public IActionResult GetRoi()
    {
        return Ok(new
        {
            x      = _config.GetValue("Pipeline:ROIX",      52),
            y      = _config.GetValue("Pipeline:ROIY",      23),
            width  = _config.GetValue("Pipeline:ROIWidth",  13),
            height = _config.GetValue("Pipeline:ROIHeight", 19)
        });
    }

    /// <summary>
    /// POST /api/roi/move?dir=left|right|up|down
    /// Posunie ROI o 1 percentuálny bod v danom smere.
    /// Zapíše novú hodnotu do appsettings.json — IConfiguration ju načíta automaticky
    /// (reloadOnChange=true) a ImagePipelineService použije novú hodnotu pri ďalšom frame.
    /// Vráti aktualizované ROI okamžite (bez čakania na reload konfigurácie).
    /// </summary>
    [HttpPost("/api/roi/move")]
    public IActionResult MoveRoi([FromQuery] string dir)
    {
        int roiX = _config.GetValue("Pipeline:ROIX", 52);
        int roiY = _config.GetValue("Pipeline:ROIY", 23);

        switch (dir?.ToLowerInvariant())
        {
            case "left":  roiX -= 1; break;
            case "right": roiX += 1; break;
            case "up":    roiY -= 1; break;
            case "down":  roiY += 1; break;
            default:
                return BadRequest(new { error = $"Neznámy smer: '{dir}'. Použite left|right|up|down." });
        }

        // Clamp: 0..99 — ROI nesmie úplne vyjsť z frame
        roiX = Math.Clamp(roiX, 0, 99);
        roiY = Math.Clamp(roiY, 0, 99);

        string filePath = Path.Combine(_env.ContentRootPath, "appsettings.json");
        try
        {
            lock (_fileLock)
            {
                string json = System.IO.File.ReadAllText(filePath);
                var root     = JsonNode.Parse(json)!.AsObject();
                var pipeline = root["Pipeline"]!.AsObject();
                pipeline["ROIX"] = roiX;
                pipeline["ROIY"] = roiY;

                System.IO.File.WriteAllText(
                    filePath,
                    root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                _configRoot.Reload();
            }

            _logger.LogInformation("RoiController: {Dir} → ROIX={X}, ROIY={Y}", dir, roiX, roiY);
        }
        catch (Exception ex)
        {
            _logger.LogError("RoiController: Zápis appsettings.json zlyhal: {Msg}", ex.Message);
            return StatusCode(500, new { error = "Zmenu ROI sa nepodarilo uložiť." });
        }

        return Ok(new
        {
            x      = roiX,
            y      = roiY,
            width  = _config.GetValue("Pipeline:ROIWidth",  13),
            height = _config.GetValue("Pipeline:ROIHeight", 19)
        });
    }

    /// <summary>
    /// POST /api/roi/resize?dir=wider|narrower|taller|shorter
    /// Zmení šírku alebo výšku ROI o 1 percentuálny bod.
    /// </summary>
    [HttpPost("/api/roi/resize")]
    public IActionResult ResizeRoi([FromQuery] string dir)
    {
        int roiWidth  = _config.GetValue("Pipeline:ROIWidth",  13);
        int roiHeight = _config.GetValue("Pipeline:ROIHeight", 19);

        switch (dir?.ToLowerInvariant())
        {
            case "wider":    roiWidth  += 1; break;
            case "narrower": roiWidth  -= 1; break;
            case "taller":   roiHeight += 1; break;
            case "shorter":  roiHeight -= 1; break;
            default:
                return BadRequest(new { error = $"Neznámy smer: '{dir}'. Použite wider|narrower|taller|shorter." });
        }

        roiWidth  = Math.Clamp(roiWidth,  1, 99);
        roiHeight = Math.Clamp(roiHeight, 1, 99);

        string filePath = Path.Combine(_env.ContentRootPath, "appsettings.json");
        try
        {
            lock (_fileLock)
            {
                string json  = System.IO.File.ReadAllText(filePath);
                var root     = JsonNode.Parse(json)!.AsObject();
                var pipeline = root["Pipeline"]!.AsObject();
                pipeline["ROIWidth"]  = roiWidth;
                pipeline["ROIHeight"] = roiHeight;

                System.IO.File.WriteAllText(
                    filePath,
                    root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                _configRoot.Reload();
            }

            _logger.LogInformation("RoiController: {Dir} → ROIWidth={W}, ROIHeight={H}", dir, roiWidth, roiHeight);
        }
        catch (Exception ex)
        {
            _logger.LogError("RoiController: Zápis appsettings.json zlyhal: {Msg}", ex.Message);
            return StatusCode(500, new { error = "Zmenu ROI sa nepodarilo uložiť." });
        }

        return Ok(new
        {
            x      = _config.GetValue("Pipeline:ROIX",     52),
            y      = _config.GetValue("Pipeline:ROIY",     23),
            width  = roiWidth,
            height = roiHeight
        });
    }
}
