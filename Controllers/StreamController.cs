using Microsoft.AspNetCore.Mvc;
using GUIVideoProcessing.Web.Services;

namespace GUIVideoProcessing.Web.Controllers
{
    /// <summary>
    /// HTTP Controller pre live video stream.
    ///
    /// Čo robí:
    /// - Poskytuje endpoint GET /stream
    /// - Číta JPEG framy z MjpegCaptureService (ktorá ich získava z ESP32-CAM)
    /// - Posiela ich do prehliadača vo formáte multipart/x-mixed-replace (MJPEG)
    ///
    /// Prečo Controller a nie Razor Page:
    /// - Razor Pages sú určené pre HTML stránky
    /// - Controller je vhodnejší pre "surové" HTTP odpovede (binárne dáta, streamy)
    /// </summary>
    // [ApiController] = označuje triedu ako API controller (automatická validácia, routing)
    [ApiController]
    public class StreamController : ControllerBase
    {
        // MjpegCaptureService — injektovaná cez DI, poskytuje posledný JPEG frame
        private readonly MjpegCaptureService _captureService;

        // ImagePipelineService — poskytuje posledný spracovaný (cleaned) JPEG frame
        private readonly ImagePipelineService _pipeline;

        // ILogger — built-in ASP.NET Core logging
        private readonly ILogger<StreamController> _logger;

        // Konštruktor — ASP.NET Core DI automaticky dodá všetky závislosti
        public StreamController(
            MjpegCaptureService captureService,
            ImagePipelineService pipeline,
            ILogger<StreamController> logger)
        {
            _captureService = captureService;
            _pipeline       = pipeline;
            _logger         = logger;
        }

        /// <summary>
        /// GET /stream
        ///
        /// Posiela nepretržitý MJPEG stream do prehliadača.
        ///
        /// Čo je MJPEG (multipart/x-mixed-replace):
        /// - Špeciálny HTTP Content-Type ktorý hovorí prehliadaču:
        ///   "toto nie je jeden obrázok, ale séria obrázkov za sebou"
        /// - Každý JPEG frame je oddelený boundary reťazcom (--frame)
        /// - Prehliadač zobrazuje každý nový frame hneď ako príde → animácia = video
        /// - Funguje priamo v <img src="/stream"> bez akéhokoľvek JavaScriptu
        ///
        /// Formát odpovede:
        /// --frame
        /// Content-Type: image/jpeg
        ///
        /// [JPEG bajty]
        /// --frame
        /// Content-Type: image/jpeg
        ///
        /// [JPEG bajty]
        /// ...
        /// </summary>
        // [HttpGet] = táto metóda obslúži HTTP GET požiadavku
        // "/stream" = URL cesta
        [HttpGet("/stream")]
        public async Task Stream(CancellationToken token)
        {
            _logger.LogInformation("Stream: Nový klient sa pripojil na /stream");

            // Nastav HTTP hlavičky odpovede pre MJPEG stream
            // multipart/x-mixed-replace = MJPEG formát
            // boundary=frame = oddeľovač medzi jednotlivými JPEG framami
            Response.ContentType = "multipart/x-mixed-replace; boundary=frame";

            // Vypni bufferovanie odpovede — každý frame musí ísť okamžite do prehliadača
            // Bez tohto by ASP.NET Core buffferoval odpoveď a prehliadač by nič nevidel
            Response.Headers["Cache-Control"] = "no-cache, no-store";
            Response.Headers["X-Accel-Buffering"] = "no"; // pre nginx reverse proxy

            try
            {
                // Posiela framy kým:
                // - klient neodpojí prehliadač (token sa zruší)
                // - aplikácia sa nevypne (token sa zruší)
                while (!token.IsCancellationRequested)
                {
                    // Získaj posledný JPEG frame z MjpegCaptureService (thread-safe)
                    // null = kamera ešte neposlala žiadny frame (napr. práve sa pripája)
                    byte[]? frameBytes = _captureService.GetLastFrame();

                    if (frameBytes == null || frameBytes.Length == 0)
                    {
                        // Kamera ešte nie je pripravená — počkaj chvíľu a skús znova
                        await Task.Delay(50, token);
                        continue;
                    }

                    try
                    {
                        // === POŠLI JEDEN JPEG FRAME ===

                        // 1. Boundary + Content-Type hlavička
                        // Prehliadač podľa tohto vie kde začína nový frame
                        var header = "--frame\r\nContent-Type: image/jpeg\r\n\r\n"u8.ToArray();
                        await Response.Body.WriteAsync(header, token);

                        // 2. Samotné JPEG bajty (obrázok)
                        await Response.Body.WriteAsync(frameBytes, token);

                        // 3. Oddeľovač za framom (prázdny riadok)
                        var footer = "\r\n"u8.ToArray();
                        await Response.Body.WriteAsync(footer, token);

                        // 4. Flush — okamžite pošli všetko do prehliadača
                        // Bez tohto by dáta zostali v bufferi a prehliadač by čakal
                        await Response.Body.FlushAsync(token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Klient zatvoril prehliadač / tab — normálny stav, nie chyba
                        break;
                    }

                    // Krátka pauza medzi framami
                    // ~33ms = ~30 FPS maximum (kamera reálne posiela menej)
                    // Bez pauzy by sme CPU vyťažovali zbytočne rýchlym čítaním
                    await Task.Delay(33, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Klient sa odpojil alebo aplikácia sa vypína — normálny koniec
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Stream: Chyba pri streamovaní: {Message}", ex.Message);
            }
            finally
            {
                _logger.LogInformation("Stream: Klient sa odpojil od /stream");
            }
        }

        /// <summary>
        /// GET /cleaned
        /// Vráti posledný spracovaný frame (po OpenCV pipeline, pred rozdelením na ľavý/pravý digit)
        /// ako statický JPEG obrázok — vhodné na periodické dopytovanie z frontendu.
        /// </summary>
        [HttpGet("/cleaned")]
        public IActionResult Cleaned()
        {
            byte[]? bytes = _pipeline.GetLastCleaned();
            if (bytes == null || bytes.Length == 0)
                return NoContent();

            return File(bytes, "image/jpeg");
        }

        /// <summary>GET /left — posledný výrez ľavej číslice ako JPEG.</summary>
        [HttpGet("/left")]
        public IActionResult Left()
        {
            byte[]? bytes = _pipeline.GetLastLeft();
            if (bytes == null || bytes.Length == 0)
                return NoContent();

            return File(bytes, "image/jpeg");
        }

        /// <summary>GET /right — posledný výrez pravej číslice ako JPEG.</summary>
        [HttpGet("/right")]
        public IActionResult Right()
        {
            byte[]? bytes = _pipeline.GetLastRight();
            if (bytes == null || bytes.Length == 0)
                return NoContent();

            return File(bytes, "image/jpeg");
        }
    }
}
