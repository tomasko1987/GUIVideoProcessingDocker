// SignalR — pripravené na budúce použitie (push hodnôt do prehliadača)
using Microsoft.AspNetCore.SignalR;
// StreamHub — pripravené na budúce použitie
using GUIVideoProcessing.Web.Hubs;

// Zaradenie triedy do menného priestoru projektu
namespace GUIVideoProcessing.Web.Services
{
    /// <summary>
    /// BackgroundService — číta MJPEG stream z ESP32-CAM.
    /// Prenesená logika z MainForm.CaptureLoop() (WinForms projekt).
    ///
    /// Hlavné rozdiely oproti WinForms verzii:
    /// - Žiadne Invoke() na UI thread — nie je UI
    /// - JPEG bajty sa ukladajú do _lastFrameBytes (zdieľané s /stream endpointom)
    /// - Pipeline sa volá pre každý frame (bude implementovaná neskôr)
    /// - Plne async/await (WinForms verzia používala .GetAwaiter().GetResult())
    /// </summary>
    // Dedí od BackgroundService → ASP.NET Core ju automaticky spustí pri štarte aplikácie
    public class MjpegCaptureService : BackgroundService
    {
        // ASP.NET Core built-in logger — píše do konzoly/súboru automaticky
        // Náhrada za náš vlastný Logger.cs z WinForms projektu
        private readonly ILogger<MjpegCaptureService> _logger;

        // Prístup k appsettings.json — načítame URL kamery a timeouty
        private readonly IConfiguration _config;

        // ImagePipelineService — zavolá sa pre každý JPEG frame
        // Spracuje obraz (OpenCV) a rozpozná číslice (ONNX)
        private readonly ImagePipelineService _pipeline;

        // Posledný kompletný JPEG frame v pamäti
        // null = žiadny frame ešte neprišiel
        // /stream endpoint odtiaľto číta a posiela do prehliadača
        private byte[]? _lastFrameBytes;

        // Ochrana pred súbehom vlákien:
        // MjpegCaptureService PÍŠE _lastFrameBytes (background thread)
        // /stream endpoint ČÍTA _lastFrameBytes (HTTP thread)
        // Bez locku by mohlo dôjsť ku corrupted read
        private readonly Lock _frameLock = new();

        // static = jeden HttpClient pre celú aplikáciu (best practice v .NET)
        // InfiniteTimeSpan = HttpClient sám nikdy nevyprší
        // Timeouty riešime manuálne cez CancellationToken (connectCts, frameCts)
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

        // Samostatný HttpClient pre posielanie príkazov na ESP32-CAM (/control endpoint).
        // Krátky timeout — kamera je na LAN, dlhé čakanie nedáva zmysel.
        private static readonly HttpClient _controlHttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        // ASP.NET Core DI automaticky dodá všetky parametre pri vytvorení triedy
        // Nie je potrebné nič volať manuálne — framework sa postará
        public MjpegCaptureService(
            ILogger<MjpegCaptureService> logger,
            IConfiguration config,
            ImagePipelineService pipeline)
        {
            _logger = logger;
            _config = config;
            _pipeline = pipeline;
        }

        /// <summary>
        /// Vráti kópiu posledného JPEG frame (thread-safe).
        /// Volané z /stream endpointu.
        /// </summary>
        public byte[]? GetLastFrame()
        {
            lock (_frameLock)
            {
                // .ToArray() vracia KÓPIU, nie referenciu na originál
                // Dôvod: keby sme vrátili referenciu, /stream endpoint by čítal pole
                // kým ho MjpegCaptureService súčasne prepíše → race condition
                return _lastFrameBytes?.ToArray();
            }
        }

        /// <summary>
        /// Hlavná slučka — spustená automaticky pri štarte aplikácie frameworkom.
        /// override = prepíšeme abstraktnú metódu z BackgroundService
        /// token príde od frameworku — zruší sa pri Ctrl+C / app.StopAsync()
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken token)
        {
            // Načíta pole URL z appsettings.json → sekcia "Camera" → pole "Urls"
            var urls = _config.GetSection("Camera:Urls").Get<string[]>();
            // Vezme prvú URL, alebo prázdny string ak pole neexistuje
            string selectedUrl = urls?.FirstOrDefault() ?? "";

            // Ak nie je nakonfigurovaná URL, nemá zmysel pokračovať
            if (string.IsNullOrWhiteSpace(selectedUrl))
            {
                _logger.LogError("MjpegCaptureService: Camera URL is not configured.");
                return;
            }

            // Načíta timeouty z appsettings.json, druhý parameter = default hodnota ak chýba
            int connectTimeoutSec = _config.GetValue("Camera:ConnectTimeoutSec", 15);
            // Ak za frameTimeoutSec nepríde žiadny bajt → reconnect
            int frameTimeoutSec   = _config.GetValue("Camera:FrameTimeoutSec", 5);
            // Maximálna pauza pred reconnectom (cap pre backoff algoritmus)
            int backoffMaxSec     = _config.GetValue("Camera:ReconnectBackoffMaxSec", 60);

            // 64 KB — veľkosť jednej čítacej dávky zo siete
            const int READ_BUF_SIZE = 65536;
            // 4 MB — max veľkosť akumulačného buffera (JPEG frame je ~50-150 KB)
            const int ACC_BUF_SIZE  = 4 * 1024 * 1024;
            // Sem sa čítajú surové bajty zo siete (jedna dávka)
            var readBuf = new byte[READ_BUF_SIZE];
            // Sem sa akumulujú bajty kým nenájdeme kompletný JPEG
            var accBuf  = new byte[ACC_BUF_SIZE];
            // Koľko platných bajtov je aktuálne v accBuf
            int accLen  = 0;

            // Počet pokusov o pripojenie — slúži na výpočet backoff čakania
            int totalReconnectAttempts = 0;
            // Čas kedy naposledy vypadol stream — pre logovanie dĺžky výpadku
            // null = stream beží normálne
            DateTime? disconnectedAt   = null;

            _logger.LogInformation("MjpegCaptureService: Starting. URL={Url}", selectedUrl);

            // ========================= VONKAJŠIA RECONNECT SLUČKA =========================
            // Beží kým aplikácia beží — zastaví sa len keď príde Ctrl+C (token.Cancel)
            while (!token.IsCancellationRequested)
            {
                // Backoff pred reconnectom — preskakujeme pri 1. pokuse (hneď sa pripojíme)
                if (totalReconnectAttempts > 0)
                {
                    // Backoff rastie lineárne: 1s, 2s, 3s, ... až po backoffMaxSec (60s)
                    // Math.Min zabezpečí že nikdy neprekročí maximum
                    int backoffSec = Math.Min(totalReconnectAttempts, backoffMaxSec);
                    _logger.LogWarning("STREAM [pokus #{Attempt}]: Čakám {Backoff}s pred ďalším pokusom.",
                        totalReconnectAttempts, backoffSec);

                    // Čaká pred ďalším pokusom
                    // Ak príde token.Cancel() počas čakania → OperationCanceledException → break
                    try { await Task.Delay(TimeSpan.FromSeconds(backoffSec), token); }
                    catch (OperationCanceledException) { break; }
                }

                // Zvýš počítadlo pokusov
                totalReconnectAttempts++;
                // Reset buffera — staré dáta z predošlého spojenia zahodiť
                accLen = 0;

                try
                {
                    // === PRIPOJENIE S TIMEOUTOM ===

                    // Vytvorí nový CancellationToken "prepojený" s hlavným token
                    // Ak sa zruší hlavný token → zruší sa aj connectCts
                    // Ak sa zruší connectCts (timeout) → hlavný token NIE je zrušený
                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    // Automaticky zruší connectCts po connectTimeoutSec → timeout pre pripojenie
                    connectCts.CancelAfter(TimeSpan.FromSeconds(connectTimeoutSec));

                    _logger.LogInformation(
                        "STREAM [pokus #{Attempt}]: Pripájam sa na {Url} (connect timeout={Timeout}s)...",
                        totalReconnectAttempts, selectedUrl, connectTimeoutSec);

                    HttpResponseMessage response;
                    try
                    {
                        // ResponseHeadersRead = vráti sa hneď po prijatí HTTP hlavičiek
                        // Nečakáme na celé telo — stream je nekonečný
                        // connectCts.Token = ak vyprší 15s timeout → OperationCanceledException
                        response = await _httpClient.GetAsync(
                            selectedUrl,
                            HttpCompletionOption.ResponseHeadersRead,
                            connectCts.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        // OperationCanceledException = vypršal connectCts (15s timeout)
                        // when (!token.IsCancellationRequested) = NIE je to Ctrl+C, len náš timeout
                        // continue = ďalší pokus vo vonkajšej reconnect slučke
                        _logger.LogWarning("MJPEG: Connection timeout after {Timeout}s. Will retry.", connectTimeoutSec);
                        continue;
                    }

                    _logger.LogInformation(
                        "STREAM [pokus #{Attempt}]: Pripojené. HTTP {Status}. Posielam nastavenia kamery...",
                        totalReconnectAttempts, (int)response.StatusCode);

                    // Po každom úspešnom pripojení (vrátane reconnectu) odošli nastavenia kamery.
                    // ESP32-CAM po HW reštarte môže mať predvolené hodnoty — týmto ich obnovíme.
                    // SendCameraSettingsAsync je fire-and-continue: pri čiastočnom zlyhaní loguje varovania,
                    // ale stream sa spustí aj tak (kamera môže fungovať s predvolenými hodnotami).
                    await SendCameraSettingsAsync(selectedUrl, token);

                    _logger.LogInformation(
                        "STREAM [pokus #{Attempt}]: Čakám na dáta...",
                        totalReconnectAttempts);

                    // using = automaticky zatvorí a uvoľní response po skončení bloku
                    // await using = asynchrónny Dispose na konci bloku pre stream
                    using (response)
                    await using (var stream = await response.Content.ReadAsStreamAsync(token))
                    {
                        // Flag či sme už dostali aspoň jeden frame (pre logovanie)
                        bool receivedFirstFrame = false;

                        // === VNÚTORNÁ SLUČKA – ČÍTANIE STREAMU ===
                        // Beží kým stream funguje — pri chybe sa vyskočí a vonkajšia slučka spraví reconnect
                        while (!token.IsCancellationRequested)
                        {
                            // Per-frame timeout — nový pre každé čítanie
                            // Prepojený s hlavným token (Ctrl+C ho tiež zruší)
                            using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                            // Ak za frameTimeoutSec neprídu žiadne dáta → zruší sa frameCts
                            frameCts.CancelAfter(TimeSpan.FromSeconds(frameTimeoutSec));

                            int bytesRead;
                            try
                            {
                                // Čítaj až READ_BUF_SIZE bajtov do readBuf
                                // Vracia koľko bajtov skutočne prišlo (môže byť menej)
                                // Blokuje (asynchrónne) kým neprídu dáta alebo nevyprší frameCts
                                bytesRead = await stream.ReadAsync(readBuf, 0, READ_BUF_SIZE, frameCts.Token);
                            }
                            catch (OperationCanceledException) when (!token.IsCancellationRequested)
                            {
                                // Vypršal frame timeout (nie Ctrl+C) → reconnect
                                if (!disconnectedAt.HasValue) disconnectedAt = DateTime.UtcNow;
                                _logger.LogWarning(
                                    "STREAM: Frame timeout po {Timeout}s. Výpadok od {Since:HH:mm:ss}. Restartujem.",
                                    frameTimeoutSec, disconnectedAt.Value);
                                // break = vyskočí z vnútornej slučky → vonkajšia slučka spraví reconnect
                                break;
                            }

                            // bytesRead == 0 = server uzavrel spojenie (HTTP EOF)
                            if (bytesRead == 0)
                            {
                                if (!disconnectedAt.HasValue) disconnectedAt = DateTime.UtcNow;
                                _logger.LogWarning(
                                    "STREAM: Server uzavrel spojenie. Výpadok od {Since:HH:mm:ss}. Restartujem.",
                                    disconnectedAt.Value);
                                break;
                            }

                            // Ochrana pred pretečením akumulačného buffera
                            if (accLen + bytesRead > ACC_BUF_SIZE)
                            {
                                _logger.LogWarning("MJPEG: Buffer overflow – resetujem.");
                                accLen = 0;
                            }
                            // Skopíruj novo načítané bajty NA KONIEC akumulačného buffera
                            // readBuf[0..bytesRead] → accBuf[accLen..accLen+bytesRead]
                            Array.Copy(readBuf, 0, accBuf, accLen, bytesRead);
                            // Aktualizuj dĺžku platných dát v accBuf
                            accLen += bytesRead;

                            // === EXTRAKCIA JPEG RÁMCOV ===
                            // Jeden průchod = jeden pokus nájsť kompletný JPEG v accBuf
                            // Môže nájsť viacero JPEG rámcov v jednom READ_BUF_SIZE čítaní
                            while (true)
                            {
                                // Hľadá 0xFF 0xD8 0xFF = začiatok JPEG (SOI = Start Of Image)
                                // Vráti index alebo -1 ak nenájde
                                int jpegStart = FindSOI(accBuf, accLen);
                                // Žiadny JPEG začiatok v bufferi → zahoď všetko a čakaj na ďalšie dáta
                                if (jpegStart < 0) { accLen = 0; break; }

                                // Hľadá 0xFF 0xD9 = koniec JPEG (EOI = End Of Image)
                                // Hľadá od jpegStart+3 (preskočíme SOI bajty)
                                int jpegEnd = FindEOI(accBuf, jpegStart + 3, accLen);
                                if (jpegEnd < 0)
                                {
                                    // JPEG ešte nie je kompletný — prišla len časť
                                    if (jpegStart > 0)
                                    {
                                        // Posun buffera doľava — zahoď bajty pred JPEG začiatkom
                                        // Zachovaj bajty od jpegStart ďalej (začiatok nášho JPEG)
                                        Array.Copy(accBuf, jpegStart, accBuf, 0, accLen - jpegStart);
                                        accLen -= jpegStart;
                                    }
                                    // Čakaj na ďalšie dáta zo siete (ďalší ReadAsync)
                                    break;
                                }

                                // Kompletný JPEG nájdený — extrahuj ho
                                // +2 = zahrnúť EOI bajty 0xFF 0xD9
                                int jpegLen   = jpegEnd - jpegStart + 2;
                                var jpegBytes = new byte[jpegLen];
                                Array.Copy(accBuf, jpegStart, jpegBytes, 0, jpegLen);

                                // Odstráň spracovaný JPEG z buffera (posun zvyšku doľava)
                                // +2 = za koniec EOI bajty
                                int consumed = jpegEnd + 2;
                                if (consumed < accLen)
                                    Array.Copy(accBuf, consumed, accBuf, 0, accLen - consumed);
                                accLen -= consumed;
                                if (accLen < 0) accLen = 0;
                                // Ďalší průchod while(true) spracuje prípadný ďalší JPEG v bufferi

                                // Prvý úspešný frame — zaloguj stav spojenia
                                if (!receivedFirstFrame)
                                {
                                    receivedFirstFrame = true;
                                    int prevAttempts = totalReconnectAttempts;
                                    // Reset počítadla — úspešné spojenie
                                    totalReconnectAttempts = 0;

                                    if (disconnectedAt.HasValue)
                                    {
                                        double downtimeSec = (DateTime.UtcNow - disconnectedAt.Value).TotalSeconds;
                                        _logger.LogInformation(
                                            "STREAM: Obnovený po {Attempts} pokusoch, výpadok trval {Downtime:F0}s.",
                                            prevAttempts, downtimeSec);
                                        disconnectedAt = null;
                                    }
                                    else
                                    {
                                        _logger.LogInformation(
                                            "STREAM [pokus #{Attempt}]: Prvý frame prijatý – stream aktívny.",
                                            prevAttempts);
                                    }
                                }

                                // Ulož frame thread-safe — /stream endpoint zavolá GetLastFrame()
                                lock (_frameLock) { _lastFrameBytes = jpegBytes; }

                                // Spracuj frame — OpenCV pipeline + ONNX rozpoznávanie
                                // Fire-and-forget: neblokujeme capture loop čakaním na pipeline
                                _ = _pipeline.ProcessFrameAsync(jpegBytes, token);
                            } // while JPEG extraction
                        } // while inner stream read
                    } // using stream + response
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Ctrl+C / app.StopAsync() → čistý koniec
                    break;
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    // Iná chyba (sieťová, HTTP, ...) → zaloguj a skús znova (vonkajšia slučka)
                    if (!disconnectedAt.HasValue) disconnectedAt = DateTime.UtcNow;
                    _logger.LogWarning(
                        "STREAM: Chyba [{Type}]: {Message}. Výpadok od {Since:HH:mm:ss}. Restartujem.",
                        ex.GetType().Name, ex.Message, disconnectedAt.Value);
                }
            } // while outer reconnect

            _logger.LogInformation("MjpegCaptureService: Stopped.");
        }

        // ========================= NASTAVENIA KAMERY (ESP32-CAM) =========================

        /// <summary>
        /// Odošle parametre kamery na ESP32-CAM cez HTTP GET /control?var=...&amp;val=...
        /// Volá sa po každom úspešnom TCP pripojení (prvý connect aj reconnect).
        /// ESP32-CAM po HW reštarte môže mať predvolené hodnoty — týmto ich obnovíme pred štartom streamu.
        /// </summary>
        private async Task SendCameraSettingsAsync(string streamUrl, CancellationToken token)
        {
            try
            {
                // Extrahuj len hostname — stream URL má port :81, control endpoint beží na porte 80
                // Príklad: "http://192.168.50.96:81/stream" → "http://192.168.50.96/control"
                string host    = new Uri(streamUrl).Host;
                string baseUrl = $"http://{host}/control";

                var cmds = new (string Var, int Val)[]
                {
                    ("framesize",    _config.GetValue("Camera:CamFramesize",  13)),
                    ("brightness",   _config.GetValue("Camera:CamBrightness",  2)),
                    ("contrast",     _config.GetValue("Camera:CamContrast",    2)),
                    ("saturation",   _config.GetValue("Camera:CamSaturation",  2)),
                    ("led_intensity", _config.GetValue("Camera:LedIntensity", 14)),
                };

                foreach (var (varName, val) in cmds)
                {
                    if (token.IsCancellationRequested) break;

                    string url = $"{baseUrl}?var={varName}&val={val}";
                    try
                    {
                        var response = await _controlHttpClient.GetAsync(url, token);
                        if (response.IsSuccessStatusCode)
                            _logger.LogInformation("CAM: {Var}={Val} OK", varName, val);
                        else
                            _logger.LogWarning("CAM: {Var}={Val} HTTP {Status}", varName, val, (int)response.StatusCode);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("CAM: {Var}={Val} chyba – {Msg}", varName, val, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("CAM: SendCameraSettings neočakávaná chyba – {Msg}", ex.Message);
            }
        }

        // ========================= JPEG HELPERS =========================

        // Hľadá začiatok JPEG: 0xFF 0xD8 0xFF (SOI = Start Of Image)
        // Každý JPEG súbor začína presne týmito troma bajtmi (štandard JFIF/Exif)
        // Vráti index v bufferi alebo -1 ak nenájde
        private static int FindSOI(byte[] buf, int len)
        {
            for (int i = 0; i < len - 2; i++)
                if (buf[i] == 0xFF && buf[i + 1] == 0xD8 && buf[i + 2] == 0xFF)
                    return i;
            return -1;
        }

        // Hľadá koniec JPEG: 0xFF 0xD9 (EOI = End Of Image)
        // Každý JPEG súbor končí presne týmito dvoma bajtmi (štandard)
        // from = začína hľadanie od tohto indexu (preskočíme SOI)
        // Vráti index v bufferi alebo -1 ak JPEG ešte nie je kompletný
        private static int FindEOI(byte[] buf, int from, int len)
        {
            for (int i = from; i < len - 1; i++)
                if (buf[i] == 0xFF && buf[i + 1] == 0xD9)
                    return i;
            return -1;
        }
    }
}
