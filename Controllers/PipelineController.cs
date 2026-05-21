using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Nodes;

namespace GUIVideoProcessing.Web.Controllers;

[ApiController]
public class PipelineController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly IConfigurationRoot _configRoot;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PipelineController> _logger;

    private static readonly object _fileLock = new();

    public PipelineController(
        IConfiguration config,
        IWebHostEnvironment env,
        ILogger<PipelineController> logger)
    {
        _config     = config;
        _configRoot = (IConfigurationRoot)config;
        _env        = env;
        _logger     = logger;
    }

    /// <summary>
    /// GET /api/pipeline/bilateral
    /// Vráti aktuálne parametre Bilateral filtra z appsettings.json.
    /// </summary>
    [HttpGet("/api/pipeline/bilateral")]
    public IActionResult GetBilateral() => Ok(new
    {
        d          = _config.GetValue("Pipeline:BilateralD",          5),
        sigmaColor = _config.GetValue("Pipeline:BilateralSigmaColor", 40),
        sigmaSpace = _config.GetValue("Pipeline:BilateralSigmaSpace", 40)
    });

    /// <summary>
    /// POST /api/pipeline/bilateral
    /// Uloží nové parametre Bilateral filtra do appsettings.json.
    /// IConfiguration (reloadOnChange=true) ich načíta automaticky — bez reštartu.
    /// </summary>
    [HttpPost("/api/pipeline/bilateral")]
    public IActionResult SetBilateral([FromBody] BilateralRequest req)
    {
        // Validácia — rovnaké rozsahy ako WinForm NumericUpDown
        // record má init-only properties → používame lokálne premenné
        int d          = Math.Clamp(req.D,          1,   31);
        int sigmaColor = Math.Clamp(req.SigmaColor, 1,  200);
        int sigmaSpace = Math.Clamp(req.SigmaSpace, 1,  200);

        string filePath = Path.Combine(_env.ContentRootPath, "appsettings.json");
        try
        {
            lock (_fileLock)
            {
                string json     = System.IO.File.ReadAllText(filePath);
                var root        = JsonNode.Parse(json)!.AsObject();
                var pipeline    = root["Pipeline"]!.AsObject();
                pipeline["BilateralD"]          = d;
                pipeline["BilateralSigmaColor"] = sigmaColor;
                pipeline["BilateralSigmaSpace"] = sigmaSpace;

                System.IO.File.WriteAllText(
                    filePath,
                    root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                _configRoot.Reload();
            }

            _logger.LogInformation(
                "PipelineController: Bilateral → D={D}, SigmaColor={C}, SigmaSpace={S}",
                d, sigmaColor, sigmaSpace);
        }
        catch (Exception ex)
        {
            _logger.LogError("PipelineController: Zápis appsettings.json zlyhal: {Msg}", ex.Message);
            return StatusCode(500, new { error = "Zmenu sa nepodarilo uložiť." });
        }

        return Ok(new { d, sigmaColor, sigmaSpace });
    }

    public record BilateralRequest(int D, int SigmaColor, int SigmaSpace);

    /// <summary>
    /// GET /api/pipeline/clahe
    /// Vráti aktuálne parametre CLAHE z appsettings.json.
    /// </summary>
    [HttpGet("/api/pipeline/clahe")]
    public IActionResult GetClahe() => Ok(new
    {
        clipLimit   = _config.GetValue("Pipeline:CLAHEClipLimit",    5.0),
        tileGridSizeX = _config.GetValue("Pipeline:CLAHETileGridSizeX", 2),
        tileGridSizeY = _config.GetValue("Pipeline:CLAHETileGridSizeY", 2)
    });

    /// <summary>
    /// POST /api/pipeline/clahe
    /// Uloží nové parametre CLAHE do appsettings.json.
    /// </summary>
    [HttpPost("/api/pipeline/clahe")]
    public IActionResult SetClahe([FromBody] ClaheRequest req)
    {
        double clipLimit    = Math.Clamp(req.ClipLimit,     0.5, 40.0);
        int    tileGridSizeX = Math.Clamp(req.TileGridSizeX,   2,   32);
        int    tileGridSizeY = Math.Clamp(req.TileGridSizeY,   2,   32);

        string filePath = Path.Combine(_env.ContentRootPath, "appsettings.json");
        try
        {
            lock (_fileLock)
            {
                string json  = System.IO.File.ReadAllText(filePath);
                var root     = JsonNode.Parse(json)!.AsObject();
                var pipeline = root["Pipeline"]!.AsObject();
                pipeline["CLAHEClipLimit"]     = clipLimit;
                pipeline["CLAHETileGridSizeX"] = tileGridSizeX;
                pipeline["CLAHETileGridSizeY"] = tileGridSizeY;

                System.IO.File.WriteAllText(
                    filePath,
                    root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                _configRoot.Reload();
            }

            _logger.LogInformation(
                "PipelineController: CLAHE → ClipLimit={C}, TileX={X}, TileY={Y}",
                clipLimit, tileGridSizeX, tileGridSizeY);
        }
        catch (Exception ex)
        {
            _logger.LogError("PipelineController: Zápis appsettings.json zlyhal: {Msg}", ex.Message);
            return StatusCode(500, new { error = "Zmenu sa nepodarilo uložiť." });
        }

        return Ok(new { clipLimit, tileGridSizeX, tileGridSizeY });
    }

    public record ClaheRequest(double ClipLimit, int TileGridSizeX, int TileGridSizeY);

    /// <summary>
    /// GET /api/pipeline/algorithm
    /// Vráti aktuálne nastavenie algoritmu rozpoznávania a prahu segmentov.
    /// </summary>
    [HttpGet("/api/pipeline/algorithm")]
    public IActionResult GetAlgorithm() => Ok(new
    {
        algorithm              = _config.GetValue("Pipeline:RecognitionAlgorithm",    "ONNX"),
        segmentOnThreshold     = _config.GetValue("Pipeline:SegmentOnThreshold",     0.35),
        onnxMinConfidence      = _config.GetValue("Pipeline:OnnxMinConfidence",      0.89),
        numEvalIntervalSeconds = _config.GetValue("Pipeline:NumEvalIntervalSeconds", 10)
    });

    /// <summary>
    /// POST /api/pipeline/algorithm
    /// Uloží vybraný algoritmus (ONNX alebo SevenSegment) a prah segmentov do appsettings.json.
    /// </summary>
    [HttpPost("/api/pipeline/algorithm")]
    public IActionResult SetAlgorithm([FromBody] AlgorithmRequest req)
    {
        string algo           = req.Algorithm?.Trim() ?? "ONNX";
        if (algo != "ONNX" && algo != "SevenSegment") algo = "ONNX";
        double threshold      = Math.Clamp(req.SegmentOnThreshold,     0.05,  1.0);
        double onnxConfidence = Math.Clamp(req.OnnxMinConfidence,      0.0,   1.0);
        int    evalInterval   = Math.Clamp(req.NumEvalIntervalSeconds, 1,   3600);

        string filePath = Path.Combine(_env.ContentRootPath, "appsettings.json");
        try
        {
            lock (_fileLock)
            {
                string json  = System.IO.File.ReadAllText(filePath);
                var root     = JsonNode.Parse(json)!.AsObject();
                var pipeline = root["Pipeline"]!.AsObject();
                pipeline["RecognitionAlgorithm"]    = algo;
                pipeline["SegmentOnThreshold"]      = threshold;
                pipeline["OnnxMinConfidence"]       = onnxConfidence;
                pipeline["NumEvalIntervalSeconds"]  = evalInterval;

                System.IO.File.WriteAllText(
                    filePath,
                    root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                _configRoot.Reload();
            }

            _logger.LogInformation(
                "PipelineController: Algorithm → {Algo}, SegThreshold={T}, OnnxConf={C}, EvalInterval={I}s",
                algo, threshold, onnxConfidence, evalInterval);
        }
        catch (Exception ex)
        {
            _logger.LogError("PipelineController: Zápis appsettings.json zlyhal: {Msg}", ex.Message);
            return StatusCode(500, new { error = "Zmenu sa nepodarilo uložiť." });
        }

        return Ok(new { algorithm = algo, segmentOnThreshold = threshold, onnxMinConfidence = onnxConfidence, numEvalIntervalSeconds = evalInterval });
    }

    public record AlgorithmRequest(string Algorithm, double SegmentOnThreshold, double OnnxMinConfidence, int NumEvalIntervalSeconds);

    /// <summary>
    /// GET /api/pipeline/threshold
    /// Vráti aktuálne parametre Adaptive Threshold z appsettings.json.
    /// </summary>
    [HttpGet("/api/pipeline/threshold")]
    public IActionResult GetThreshold() => Ok(new
    {
        blockSize = _config.GetValue("Pipeline:AdaptiveThresholdBlockSize", 101),
        c         = _config.GetValue("Pipeline:AdaptiveThresholdC",         5.0)
    });

    /// <summary>
    /// POST /api/pipeline/threshold
    /// Uloží nové parametre Adaptive Threshold do appsettings.json.
    /// BlockSize musí byť nepárne a >= 3 — rovnaká podmienka ako v pipeline.
    /// </summary>
    [HttpPost("/api/pipeline/threshold")]
    public IActionResult SetThreshold([FromBody] ThresholdRequest req)
    {
        int blockSize = Math.Clamp(req.BlockSize, 3, 999);
        if (blockSize % 2 == 0) blockSize++;          // vynúť nepárne
        double c = Math.Clamp(req.C, 0.0, 20.0);

        string filePath = Path.Combine(_env.ContentRootPath, "appsettings.json");
        try
        {
            lock (_fileLock)
            {
                string json  = System.IO.File.ReadAllText(filePath);
                var root     = JsonNode.Parse(json)!.AsObject();
                var pipeline = root["Pipeline"]!.AsObject();
                pipeline["AdaptiveThresholdBlockSize"] = blockSize;
                pipeline["AdaptiveThresholdC"]         = c;

                System.IO.File.WriteAllText(
                    filePath,
                    root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                _configRoot.Reload();
            }

            _logger.LogInformation(
                "PipelineController: Threshold → BlockSize={B}, C={C}",
                blockSize, c);
        }
        catch (Exception ex)
        {
            _logger.LogError("PipelineController: Zápis appsettings.json zlyhal: {Msg}", ex.Message);
            return StatusCode(500, new { error = "Zmenu sa nepodarilo uložiť." });
        }

        return Ok(new { blockSize, c });
    }

    public record ThresholdRequest(int BlockSize, double C);

    /// <summary>
    /// GET /api/pipeline/morphology
    /// Vráti aktuálne parametre Morphology + MinArea z appsettings.json.
    /// </summary>
    [HttpGet("/api/pipeline/morphology")]
    public IActionResult GetMorphology() => Ok(new
    {
        kernelSize      = _config.GetValue("Pipeline:MorphologyKernelSize",       3),
        openIterations  = _config.GetValue("Pipeline:MorphologyOpenIterations",   1),
        closeIterations = _config.GetValue("Pipeline:MorphologyCloseIterations",  1),
        minArea         = _config.GetValue("Pipeline:MinArea",                   80)
    });

    /// <summary>
    /// POST /api/pipeline/morphology
    /// Uloží parametre Morphology + MinArea do appsettings.json.
    /// KernelSize musí byť nepárne a v rozsahu 1–21.
    /// </summary>
    [HttpPost("/api/pipeline/morphology")]
    public IActionResult SetMorphology([FromBody] MorphologyRequest req)
    {
        int kernelSize      = Math.Clamp(req.KernelSize,      1,  21);
        if (kernelSize % 2 == 0) kernelSize++;          // vynúť nepárne
        int openIterations  = Math.Clamp(req.OpenIterations,  1,  10);
        int closeIterations = Math.Clamp(req.CloseIterations, 1,  10);
        int minArea         = Math.Clamp(req.MinArea,         1, 5000);

        string filePath = Path.Combine(_env.ContentRootPath, "appsettings.json");
        try
        {
            lock (_fileLock)
            {
                string json  = System.IO.File.ReadAllText(filePath);
                var root     = JsonNode.Parse(json)!.AsObject();
                var pipeline = root["Pipeline"]!.AsObject();
                pipeline["MorphologyKernelSize"]      = kernelSize;
                pipeline["MorphologyOpenIterations"]  = openIterations;
                pipeline["MorphologyCloseIterations"] = closeIterations;
                pipeline["MinArea"]                   = minArea;

                System.IO.File.WriteAllText(
                    filePath,
                    root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                _configRoot.Reload();
            }

            _logger.LogInformation(
                "PipelineController: Morphology → KernelSize={K}, Open={O}, Close={C}, MinArea={A}",
                kernelSize, openIterations, closeIterations, minArea);
        }
        catch (Exception ex)
        {
            _logger.LogError("PipelineController: Zápis appsettings.json zlyhal: {Msg}", ex.Message);
            return StatusCode(500, new { error = "Zmenu sa nepodarilo uložiť." });
        }

        return Ok(new { kernelSize, openIterations, closeIterations, minArea });
    }

    public record MorphologyRequest(int KernelSize, int OpenIterations, int CloseIterations, int MinArea);

    /// <summary>
    /// GET /api/pipeline/split
    /// Vráti aktuálne hodnoty deliaceho bodu L/P číslice z appsettings.json.
    /// </summary>
    [HttpGet("/api/pipeline/split")]
    public IActionResult GetSplit() => Ok(new
    {
        fromLeft  = _config.GetValue("Pipeline:FromLeft",  49),
        fromRight = _config.GetValue("Pipeline:FromRight", 49)
    });

    /// <summary>
    /// POST /api/pipeline/split
    /// Uloží deliaci bod L/P číslice do appsettings.json.
    /// FromLeft + FromRight musia byť v rozsahu 1–99.
    /// </summary>
    [HttpPost("/api/pipeline/split")]
    public IActionResult SetSplit([FromBody] SplitRequest req)
    {
        int fromLeft  = Math.Clamp(req.FromLeft,  1, 99);
        int fromRight = Math.Clamp(req.FromRight, 1, 99);

        string filePath = Path.Combine(_env.ContentRootPath, "appsettings.json");
        try
        {
            lock (_fileLock)
            {
                string json  = System.IO.File.ReadAllText(filePath);
                var root     = JsonNode.Parse(json)!.AsObject();
                var pipeline = root["Pipeline"]!.AsObject();
                pipeline["FromLeft"]  = fromLeft;
                pipeline["FromRight"] = fromRight;

                System.IO.File.WriteAllText(
                    filePath,
                    root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                _configRoot.Reload();
            }

            _logger.LogInformation(
                "PipelineController: Split → FromLeft={L}, FromRight={R}",
                fromLeft, fromRight);
        }
        catch (Exception ex)
        {
            _logger.LogError("PipelineController: Zápis appsettings.json zlyhal: {Msg}", ex.Message);
            return StatusCode(500, new { error = "Zmenu sa nepodarilo uložiť." });
        }

        return Ok(new { fromLeft, fromRight });
    }

    public record SplitRequest(int FromLeft, int FromRight);
}
