using Microsoft.AspNetCore.SignalR;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using GUIVideoProcessing.Web.Hubs;

namespace GUIVideoProcessing.Web.Services
{
    /// <summary>
    /// Spracovanie obrazu z ESP32-CAM — prenesená logika z MainForm.Pipeline() (WinForms projekt).
    ///
    /// Kroky pipeline:
    /// 1. BGR → Grayscale
    /// 2. Bilateral filter (redukcia šumu)
    /// 3. CLAHE (lokálne zvýšenie kontrastu)
    /// 4. Adaptive threshold (binarizácia)
    /// 5. Morphology Open/Close (čistenie)
    /// 6. Connected Components (odstránenie malých objektov)
    /// 7. Rozdelenie na LEFT a RIGHT číslicu
    /// 8. ONNX rozpoznávanie číslic
    /// 9. Push výsledkov cez SignalR do prehliadača
    ///
    /// Hlavný rozdiel oproti WinForms verzii:
    /// - Žiadne UI aktualizácie (PictureBox, ListView)
    /// - Výsledky idú cez SignalR do prehliadača
    /// - Parametre sa čítajú z appsettings.json namiesto NumericUpDown kontrol
    /// </summary>
    public class ImagePipelineService : IDisposable
    {
        // SignalR Hub kontext — pre push výsledkov do prehliadača
        private readonly IHubContext<StreamHub> _hub;

        // ASP.NET Core logger
        private readonly ILogger<ImagePipelineService> _logger;

        // Konfigurácia z appsettings.json
        private readonly IConfiguration _config;

        // InfluxDB writer — asynchrónny zápis rozpoznaných hodnôt do DB
        private readonly InfluxWriterService _influx;

        // ONNX model pre rozpoznávanie číslic
        // null = model nie je k dispozícii → fallback na 7-SEG dekóder
        private InferenceSession? _onnxSession;
        private string? _onnxInputName;

        // Časovač pre periodické vyhodnocovanie (každých N sekúnd)
        // Rozpoznávanie neprebieha pri každom frame — zbytočne by zaťažovalo CPU
        private DateTime _nextEvalUtc = DateTime.UtcNow;

        // Zámok pre _nextEvalUtc (čítaný/zapisovaný z pipeline threadu)
        private readonly Lock _evalLock = new();

        // Posledný spracovaný "cleaned" obraz (po morphology + connected components, pred splitom)
        // Slúži pre /cleaned endpoint — zobrazenie vstupu do ONNX v prehliadači
        private byte[]? _lastCleanedBytes;
        private readonly Lock _cleanedLock = new();

        // Posledné výrezy ľavej a pravej číslice (po splite, pred ONNX)
        // Slúžia pre /left a /right endpointy — zobrazenie vstupu ONNX pre každú číslicu
        private byte[]? _lastLeftBytes;
        private byte[]? _lastRightBytes;
        private readonly Lock _digitLock = new();

        // Cesta k priečinku pre trénovacie vzorky (StoreSample)
        // Štruktúra: logs/examples/{0-9,N}/ a logs/examples/{0-9_O,N_O}/
        private static readonly string _examplesDir =
            Path.Combine(AppContext.BaseDirectory, "logs", "examples");

        /// <summary>
        /// Vráti kópiu posledného cleaned frame ako JPEG (thread-safe).
        /// Volané z /cleaned endpointu.
        /// </summary>
        public byte[]? GetLastCleaned()
        {
            lock (_cleanedLock)
                return _lastCleanedBytes?.ToArray();
        }

        /// <summary>Vráti kópiu posledného výrezu ľavej číslice ako JPEG (thread-safe).</summary>
        public byte[]? GetLastLeft()
        {
            lock (_digitLock) return _lastLeftBytes?.ToArray();
        }

        /// <summary>Vráti kópiu posledného výrezu pravej číslice ako JPEG (thread-safe).</summary>
        public byte[]? GetLastRight()
        {
            lock (_digitLock) return _lastRightBytes?.ToArray();
        }

        public ImagePipelineService(
            IHubContext<StreamHub> hub,
            ILogger<ImagePipelineService> logger,
            IConfiguration config,
            InfluxWriterService influx)
        {
            _hub = hub;
            _logger = logger;
            _config = config;
            _influx = influx;

            // Pokus o načítanie ONNX modelu pri štarte
            LoadOnnxModel();
        }

        /// <summary>
        /// Načíta ONNX model zo súboru definovaného v appsettings.json.
        /// Ak model neexistuje, pipeline bude fungovať bez ONNX (bez rozpoznávania).
        /// </summary>
        private void LoadOnnxModel()
        {
            // Cesta k ONNX modelu z appsettings.json, default = "model/model.onnx"
            string modelPath = _config.GetValue("Pipeline:OnnxModelPath", "model/model.onnx")!;

            if (!File.Exists(modelPath))
            {
                _logger.LogWarning("ImagePipelineService: ONNX model not found at '{Path}'. Running without recognition.", modelPath);
                return;
            }

            try
            {
                // SessionOptions = nastavenia ONNX Runtime
                // Plne kvalifikovaný názov — SessionOptions existuje aj v Microsoft.AspNetCore.Builder
                using var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
                // Zapni všetky optimalizácie grafu pre rýchlejší inference
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

                // Vytvorí ONNX inference session z .onnx súboru
                _onnxSession = new InferenceSession(modelPath, options);

                // Zistí názov vstupného tensora (potrebné pri volaní Run())
                _onnxInputName = _onnxSession.InputMetadata.Keys.First();

                _logger.LogInformation("ImagePipelineService: ONNX model loaded from '{Path}'.", modelPath);
            }
            catch (Exception ex)
            {
                _logger.LogError("ImagePipelineService: Failed to load ONNX model: {Message}", ex.Message);
                _onnxSession = null;
            }
        }

        /// <summary>
        /// Spracuje jeden JPEG frame — zavolané z MjpegCaptureService pre každý frame.
        /// CPU-intenzívna operácia (OpenCV) — volať z background threadu, NIE z HTTP threadu.
        /// </summary>
        /// <param name="jpegBytes">Surové JPEG bajty z ESP32-CAM</param>
        /// <param name="token">CancellationToken pre prípad zastavenia aplikácie</param>
        public async Task ProcessFrameAsync(byte[] jpegBytes, CancellationToken token)
        {
            // Dekóduj JPEG bajty → OpenCV Mat (BGR farebný obraz)
            using var frame = Cv2.ImDecode(jpegBytes, ImreadModes.Color);

            // Ak sa dekódovanie nepodarilo (poškodený JPEG), preskočíme frame
            if (frame == null || frame.Empty())
                return;

            // Načítaj ROI parametre z appsettings.json (v percentách 0-100)
            int roiX      = _config.GetValue("Pipeline:ROIX", 52);
            int roiY      = _config.GetValue("Pipeline:ROIY", 23);
            int roiWidth  = _config.GetValue("Pipeline:ROIWidth", 13);
            int roiHeight = _config.GetValue("Pipeline:ROIHeight", 19);

            // Vypočítaj ROI obdĺžnik v pixeloch z percent
            int x = frame.Width  * roiX      / 100;
            int y = frame.Height * roiY      / 100;
            int w = frame.Width  * roiWidth  / 100;
            int h = frame.Height * roiHeight / 100;

            // Ochrana pred neplatným ROI (mimo hraníc frame)
            x = Math.Clamp(x, 0, frame.Width  - 1);
            y = Math.Clamp(y, 0, frame.Height - 1);
            w = Math.Clamp(w, 1, frame.Width  - x);
            h = Math.Clamp(h, 1, frame.Height - y);

            // Vyreže ROI z frame — iba táto časť obsahuje číslice
            using var roi = new Mat(frame, new Rect(x, y, w, h));

            // Skontroluj či je čas na vyhodnotenie (každých N sekúnd)
            bool doEval = false;
            lock (_evalLock)
            {
                if (DateTime.UtcNow >= _nextEvalUtc)
                {
                    doEval = true;
                    // Nastav čas ďalšieho vyhodnotenia
                    int intervalSec = _config.GetValue("Pipeline:NumEvalIntervalSeconds", 10);
                    _nextEvalUtc = DateTime.UtcNow.AddSeconds(intervalSec);
                }
            }

            // Ak nie je čas na vyhodnotenie, preskočíme pipeline
            if (!doEval)
                return;

            // Spusti pipeline na background threade (CPU-intenzívna práca)
            // Task.Run = použije ThreadPool vlákno, neuvoľní HTTP thread
            await Task.Run(() => RunPipeline(roi, token), token);
        }

        /// <summary>
        /// Hlavná image processing pipeline — beží na ThreadPool vlákne.
        /// Všetky Mat objekty sú lokálne → žiadne zdieľanie, žiadne locky.
        /// </summary>
        private void RunPipeline(Mat roi, CancellationToken token)
        {
            // Lokálne Mat premenné pre jednotlivé kroky
            // Každý krok vytvorí nový Mat a predchádzajúci sa zahodí
            Mat? gray    = null;
            Mat? smooth  = null;
            Mat? contrast = null;
            Mat? binary  = null;
            Mat? cleaned = null;

            try
            {
                // ========================= 1) BGR → GRAYSCALE =========================
                // ESP32-CAM posiela farebný BGR obraz
                // Pre spracovanie číslic potrebujeme 1-kanálový grayscale
                gray = new Mat();
                Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);

                // ========================= 2) BILATERAL FILTER =========================
                // Edge-preserving smoothing — zníži šum, ale zachová hrany číslic
                // Na rozdiel od Gaussian blur nezmaže hrany segmentov
                int bilD          = _config.GetValue("Pipeline:BilateralD", 5);
                int bilSigmaColor = _config.GetValue("Pipeline:BilateralSigmaColor", 40);
                int bilSigmaSpace = _config.GetValue("Pipeline:BilateralSigmaSpace", 40);

                smooth = new Mat();
                Cv2.BilateralFilter(gray, smooth,
                    d: bilD,
                    sigmaColor: bilSigmaColor,
                    sigmaSpace: bilSigmaSpace);

                // ========================= 3) CLAHE =========================
                // Contrast Limited Adaptive Histogram Equalization
                // Lokálne zvýši kontrast — číslice budú zreteľnejšie aj pri slabom osvetlení
                double claheClip = _config.GetValue("Pipeline:CLAHEClipLimit", 5.0);
                int claheTileX   = _config.GetValue("Pipeline:CLAHETileGridSizeX", 2);
                int claheTileY   = _config.GetValue("Pipeline:CLAHETileGridSizeY", 2);

                using var clahe = Cv2.CreateCLAHE(claheClip, new Size(claheTileX, claheTileY));
                contrast = new Mat();
                clahe.Apply(smooth, contrast);

                // ========================= 4) ADAPTIVE THRESHOLD =========================
                // Binarizácia — každý pixel bude buď 0 (čierna) alebo 255 (biela)
                // BinaryInv = číslice čierne (0), pozadie biele (255)
                // Adaptive = prah sa počíta lokálne pre každú oblasť → lepšie pri nerovnomernom osvetlení
                int blockSize = _config.GetValue("Pipeline:AdaptiveThresholdBlockSize", 101);
                double c      = _config.GetValue("Pipeline:AdaptiveThresholdC", 5.0);

                // blockSize MUSÍ byť nepárne a >= 3
                if (blockSize < 3) blockSize = 3;
                if (blockSize % 2 == 0) blockSize++;

                binary = new Mat();
                Cv2.AdaptiveThreshold(contrast, binary,
                    maxValue: 255,
                    adaptiveMethod: AdaptiveThresholdTypes.GaussianC,
                    thresholdType: ThresholdTypes.BinaryInv,
                    blockSize: blockSize,
                    c: c);

                // ========================= 5) MORPHOLOGY =========================
                // Open = erózia → dilatácia → odstráni malé bodky/šum
                // Close = dilatácia → erózia → vyplní malé diery v čísliciach
                int kernelSize     = _config.GetValue("Pipeline:MorphologyKernelSize", 3);
                int openIterations = _config.GetValue("Pipeline:MorphologyOpenIterations", 1);
                int closeIter      = _config.GetValue("Pipeline:MorphologyCloseIterations", 1);

                // Štruktúrujúci element (kernel) pre morphology operácie
                using var kernel = Cv2.GetStructuringElement(
                    MorphShapes.Rect,
                    new Size(kernelSize, kernelSize));

                Cv2.MorphologyEx(binary, binary, MorphTypes.Open,  kernel, iterations: openIterations);
                Cv2.MorphologyEx(binary, binary, MorphTypes.Close, kernel, iterations: closeIter);

                // ========================= 6) CONNECTED COMPONENTS =========================
                // Nájde všetky súvislé biele objekty v binárnom obraze
                // Malé objekty (šum) zahodí, veľké (číslice) ponechá
                using var labels    = new Mat();
                using var stats     = new Mat();
                using var centroids = new Mat();

                int numLabels = Cv2.ConnectedComponentsWithStats(
                    binary, labels, stats, centroids, PixelConnectivity.Connectivity8);

                // Výsledný obraz — začíname s čiernym pozadím
                cleaned = Mat.Zeros(binary.Size(), MatType.CV_8UC1);

                int minArea = _config.GetValue("Pipeline:MinArea", 80);
                int statArea = (int)ConnectedComponentsTypes.Area;

                // i=0 je pozadie (label 0), preskakujeme ho
                for (int i = 1; i < numLabels; i++)
                {
                    int area = stats.At<int>(i, statArea);
                    // Malé objekty (šum) zahodíme
                    if (area < minArea) continue;

                    // Vytvor masku pre tento objekt a nakresli ho bielo do cleaned
                    using var mask = new Mat();
                    // CmpTypes (nová verzia) namiesto CmpType (obsolete)
                    Cv2.Compare(labels, i, mask, CmpTypes.EQ);
                    cleaned.SetTo(255, mask);
                }

                // Ulož cleaned obraz ako JPEG pre /cleaned endpoint (vstup do ONNX pred splitom)
                var cleanedJpeg = cleaned.ToBytes(".jpg");
                lock (_cleanedLock) { _lastCleanedBytes = cleanedJpeg; }

                // ========================= 7) ROZDELENIE NA ĽAVÚ A PRAVÚ ČÍSLICU =========================
                int fromLeftPct  = _config.GetValue("Pipeline:FromLeft", 49);
                int fromRightPct = _config.GetValue("Pipeline:FromRight", 49);

                // Výpočet šírky každej časti v pixeloch
                int leftWidth  = cleaned.Width * fromLeftPct  / 100;
                int rightWidth = cleaned.Width * fromRightPct / 100;

                // Ochrana pred neplatnými hodnotami
                leftWidth  = Math.Clamp(leftWidth,  1, cleaned.Width);
                rightWidth = Math.Clamp(rightWidth, 1, cleaned.Width);

                // Vyreže ľavú časť (začína od ľavého okraja)
                using var leftPart  = new Mat(cleaned, new Rect(0, 0, leftWidth, cleaned.Height));
                // Vyreže pravú časť (začína od pravého okraja)
                int rightStart = cleaned.Width - rightWidth;
                using var rightPart = new Mat(cleaned, new Rect(rightStart, 0, rightWidth, cleaned.Height));

                // Ulož výrezy číslic pre /left a /right endpointy
                var leftJpeg  = leftPart.ToBytes(".jpg");
                var rightJpeg = rightPart.ToBytes(".jpg");
                lock (_digitLock) { _lastLeftBytes = leftJpeg; _lastRightBytes = rightJpeg; }

                // ========================= 8) ROZPOZNÁVANIE (ONNX alebo 7-SEGMENT) =========================
                string algorithm = _config.GetValue("Pipeline:RecognitionAlgorithm", "ONNX") ?? "ONNX";
                bool useSevenSeg = algorithm.Equals("SevenSegment", StringComparison.OrdinalIgnoreCase);

                int? leftDigit, rightDigit;
                if (useSevenSeg)
                {
                    leftDigit  = TryDecodeSevenSegment(leftPart);
                    rightDigit = TryDecodeSevenSegment(rightPart);
                }
                else
                {
                    leftDigit  = RecognizeDigit(leftPart);
                    rightDigit = RecognizeDigit(rightPart);
                }

                string leftStr  = leftDigit.HasValue  ? leftDigit.Value.ToString()  : "?";
                string rightStr = rightDigit.HasValue ? rightDigit.Value.ToString() : "?";

                _logger.LogInformation("{Algo}: Left={Left}, Right={Right}", algorithm, leftStr, rightStr);

                // ========================= 9) SIGNALR PUSH =========================
                // Pošli výsledky do všetkých pripojených prehliadačov
                // Fire-and-forget (neblokujeme pipeline thread)
                var timestamp = DateTime.UtcNow;
                _ = _hub.Clients.All.SendAsync("NewReading",
                    leftDigit,
                    rightDigit,
                    $"{leftStr}{rightStr}",
                    timestamp,
                    token);

                // ========================= 10) EXPORT TRÉNOVACÍCH PRÍKLADOV (PNG) =========================
                // Aktívne len ak Pipeline:StoreSample = true v appsettings.json
                // Ukladá 64×96 px PNG výrezy číslic pre ďalší tréning ONNX modelu
                if (_config.GetValue("Pipeline:StoreSample", false))
                {
                    string exportTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");

                    // Cleaned (binárne) výrezy — rovnaké ako vstup do ONNX
                    ExportDigitExample(leftPart,  leftDigit,  "L", exportTimestamp, "");
                    ExportDigitExample(rightPart, rightDigit, "R", exportTimestamp, "");

                    // Originálne (farebné) výrezy priamo z ROI — pre vizuálnu kontrolu
                    int origLeftWidth  = Math.Clamp(roi.Width * fromLeftPct  / 100, 1, roi.Width);
                    int origRightWidth = Math.Clamp(roi.Width * fromRightPct / 100, 1, roi.Width);
                    int origRightStart = roi.Width - origRightWidth;
                    using var origLeftPart  = new Mat(roi, new Rect(0, 0, origLeftWidth, roi.Height)).Clone();
                    using var origRightPart = new Mat(roi, new Rect(origRightStart, 0, origRightWidth, roi.Height)).Clone();
                    ExportDigitExample(origLeftPart,  leftDigit,  "L", exportTimestamp, "_O");
                    ExportDigitExample(origRightPart, rightDigit, "R", exportTimestamp, "_O");
                }

                // ========================= 11) INFLUXDB ZÁPIS =========================
                // Enqueue je neblokujúce — zápis prebehne asynchrónne v InfluxWriterService
                string sourceTag = _config.GetValue("Pipeline:SourceTag", "default")!;
                _influx.Enqueue(new InfluxWriterService.Reading(
                    timestamp,
                    leftDigit,
                    rightDigit,
                    $"{leftStr}{rightStr}",
                    sourceTag));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("ImagePipelineService: Pipeline error: {Message}", ex.Message);
            }
            finally
            {
                // Uvoľni všetky Mat objekty — OpenCV pracuje s natívnou pamäťou
                // ktorú GC nevidí → musíme Dispose volať manuálne
                gray?.Dispose();
                smooth?.Dispose();
                contrast?.Dispose();
                binary?.Dispose();
                cleaned?.Dispose();
            }
        }

        /// <summary>
        /// Uloží výrez číslice ako PNG do trénovacej štruktúry priečinkov.
        /// Priečinky: logs/examples/{0-9,N}/ (cleaned) a logs/examples/{0-9_O,N_O}/ (originál).
        /// Resize na 64×96 px — rovnaká veľkosť ako vstup ONNX modelu.
        /// Chyby sú potichu zalogované, aby nezastavili pipeline.
        /// </summary>
        private void ExportDigitExample(Mat digitMat, int? recognizedValue, string side,
                                         string timestamp, string folderSuffix)
        {
            if (digitMat == null || digitMat.Empty()) return;
            try
            {
                // Resize na 64×96 px (šírka × výška) — InterpolationFlags.Area je optimálna pre downscaling
                using var resized = new Mat();
                Cv2.Resize(digitMat, resized, new Size(64, 96), 0, 0, InterpolationFlags.Area);

                // Priečinok podľa rozpoznanej číslice: "0"-"9" alebo "N" (nerozpoznané) + voliteľný suffix
                string folderName = (recognizedValue.HasValue
                    ? recognizedValue.Value.ToString()
                    : "N") + folderSuffix;

                string dirPath = Path.Combine(_examplesDir, folderName);
                Directory.CreateDirectory(dirPath);

                // Formát názvu: {yyyyMMdd_HHmmss_fff}_{L|R}.png
                string fullPath = Path.Combine(dirPath, $"{timestamp}_{side}.png");
                Cv2.ImWrite(fullPath, resized);

                _logger.LogDebug("Example exported: {Path}", fullPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("ExportDigitExample ({Side}): {Msg}", side, ex.Message);
            }
        }

        /// <summary>
        /// Rozpozná jednu číslicu z Mat obrazu pomocou ONNX modelu.
        /// Ak model nie je načítaný alebo inference zlyhá, vráti null.
        /// </summary>
        private int? RecognizeDigit(Mat digitMat)
        {
            // Ak ONNX model nie je k dispozícii, nemôžeme rozpoznávať
            if (_onnxSession == null || _onnxInputName == null)
                return null;

            try
            {
                // Konverzia na grayscale ak obraz nie je jednokanálový
                using var gray = new Mat();
                if (digitMat.Channels() == 1)
                    digitMat.CopyTo(gray);
                else
                    Cv2.CvtColor(digitMat, gray, ColorConversionCodes.BGR2GRAY);

                // Resize na 64×98 px — presne to čo model očakáva (shape: [1, 1, 98, 64])
                using var resized = new Mat();
                Cv2.Resize(gray, resized, new Size(64, 98), 0, 0, InterpolationFlags.Area);

                // Vytvorenie vstupného tensora [1, 1, 98, 64]
                // Hodnoty normalizované na 0.0-1.0 (pixel / 255)
                const int H = 98, W = 64;
                var inputData = new float[1 * 1 * H * W];
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                        inputData[y * W + x] = resized.At<byte>(y, x) / 255f;

                var tensor = new DenseTensor<float>(inputData, new[] { 1, 1, H, W });
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_onnxInputName, tensor)
                };

                // Spusti inference — model vráti 10 logitov (jeden pre každú číslicu 0-9)
                using var results = _onnxSession.Run(inputs);
                var logits = results.First().AsEnumerable<float>().ToArray();

                // Softmax — konvertuj logity na pravdepodobnosti (súčet = 1.0)
                float maxL = logits.Max();
                var exps   = logits.Select(l => (float)Math.Exp(l - maxL)).ToArray();
                float sum  = exps.Sum();
                var probs  = exps.Select(e => e / sum).ToArray();

                // Nájdi číslicu s najvyššou pravdepodobnosťou
                int digit      = Array.IndexOf(probs, probs.Max());
                float confidence = probs[digit];

                // Filter podľa minimálnej konfidence
                float minConf = _config.GetValue("Pipeline:OnnxMinConfidence", 0.5f);
                if (confidence < minConf)
                    return null;

                return digit;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("ImagePipelineService: ONNX inference failed: {Message}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Dekóduje jednu číslicu zo 7-seg displeja z binárneho Mat obrazu.
        /// Automaticky invertuje ak segmenty nie sú biele na čiernej.
        /// Vráti 0–9 ak úspech, null ak zlyhanie.
        /// </summary>
        private int? TryDecodeSevenSegment(Mat cleaned)
        {
            if (cleaned == null || cleaned.Empty()) return null;

            Mat bin;
            if (cleaned.Channels() == 1)
            {
                bin = cleaned.Clone();
            }
            else
            {
                using var gray = new Mat();
                Cv2.CvtColor(cleaned, gray, ColorConversionCodes.BGR2GRAY);
                bin = new Mat();
                Cv2.Threshold(gray, bin, 0, 255, ThresholdTypes.Otsu);
            }

            try
            {
                // Auto-invert: segmenty musia byť BIELE na ČIERNEJ
                int white = Cv2.CountNonZero(bin);
                int total = bin.Rows * bin.Cols;
                if (total > 0 && (double)white / total > 0.50)
                    Cv2.BitwiseNot(bin, bin);

                return DecodeSingle7SegDigit(bin, new Rect(0, 0, bin.Cols, bin.Rows));
            }
            finally
            {
                bin.Dispose();
            }
        }

        /// <summary>
        /// Zmeráme 7 segmentových oblastí v percentách → bitmaska → Dictionary lookup → číslica 0-9.
        /// Layout segmentov:
        ///   aaaaa
        ///  f     b
        ///   ggggg
        ///  e     c
        ///   ddddd
        /// </summary>
        private int? DecodeSingle7SegDigit(Mat bin, Rect digitRect)
        {
            // Defenzívne orezanie rectu do hraníc obrazu
            Rect r = digitRect;
            if (r.X < 0) r.X = 0;
            if (r.Y < 0) r.Y = 0;
            if (r.X + r.Width  > bin.Cols) r.Width  = bin.Cols - r.X;
            if (r.Y + r.Height > bin.Rows) r.Height = bin.Rows - r.Y;
            if (r.Width <= 0 || r.Height <= 0) return null;

            using var digit = new Mat(bin, r).Clone();
            if (digit.Empty()) return null;

            // Jemný crop margin (5%) — odstraňuje okrajový šum
            int mx = (int)(digit.Cols * 0.05);
            int my = (int)(digit.Rows * 0.05);
            int x0 = Math.Clamp(mx, 0, digit.Cols - 1);
            int y0 = Math.Clamp(my, 0, digit.Rows - 1);
            int w0 = Math.Clamp(digit.Cols - 2 * mx, 1, digit.Cols);
            int h0 = Math.Clamp(digit.Rows - 2 * my, 1, digit.Rows);
            using var d = new Mat(digit, new Rect(x0, y0, w0, h0)).Clone();

            int W = d.Cols;
            int H = d.Rows;
            if (W < 10 || H < 10) return null;

            double onThreshold = _config.GetValue("Pipeline:SegmentOnThreshold", 0.35);

            // Lokálna funkcia: pomer bielych pixelov v segmente >= threshold → ON
            bool SegmentOn(Rect seg)
            {
                if (seg.Width <= 0 || seg.Height <= 0) return false;
                if (seg.X < 0 || seg.Y < 0) return false;
                if (seg.X + seg.Width > W || seg.Y + seg.Height > H) return false;
                using var roi = new Mat(d, seg);
                int w = Cv2.CountNonZero(roi);
                int t = roi.Rows * roi.Cols;
                if (t <= 0) return false;
                return (double)w / t >= onThreshold;
            }

            // Hrúbky segmentov (percentá z rozmeru číslice, min 3px)
            int tH = Math.Max((int)(H * 0.18), 3); // výška horizontálnych segmentov
            int tW = Math.Max((int)(W * 0.20), 3); // šírka vertikálnych segmentov

            // Definície 7 segmentových oblastí
            Rect segA = new Rect((int)(W * 0.20), 0,               (int)(W * 0.60), tH);
            Rect segD = new Rect((int)(W * 0.20), H - tH,          (int)(W * 0.60), tH);
            Rect segG = new Rect((int)(W * 0.20), (int)(H * 0.45), (int)(W * 0.60), tH);
            Rect segF = new Rect(0,               (int)(H * 0.10), tW,              (int)(H * 0.35));
            Rect segB = new Rect(W - tW,          (int)(H * 0.10), tW,              (int)(H * 0.35));
            Rect segE = new Rect(0,               (int)(H * 0.55), tW,              (int)(H * 0.35));
            Rect segC = new Rect(W - tW,          (int)(H * 0.55), tW,              (int)(H * 0.35));

            bool A = SegmentOn(segA), B = SegmentOn(segB), C = SegmentOn(segC), D = SegmentOn(segD);
            bool E = SegmentOn(segE), F = SegmentOn(segF), G = SegmentOn(segG);

            // Bitmaska: A=bit0, B=bit1, C=bit2, D=bit3, E=bit4, F=bit5, G=bit6
            static int Bits(bool a, bool b, bool c, bool d, bool e, bool f, bool g)
                => (a ? 1 : 0) | (b ? 2 : 0) | (c ? 4 : 0) | (d ? 8 : 0)
                 | (e ? 16 : 0) | (f ? 32 : 0) | (g ? 64 : 0);

            int mask = Bits(A, B, C, D, E, F, G);

            // Mapovanie bitmasky na číslicu (štandardné 7-seg wiring)
            var map = new Dictionary<int, int>
            {
                { Bits(true,  true,  true,  true,  true,  true,  false), 0 },
                { Bits(false, true,  true,  false, false, false, false), 1 },
                { Bits(true,  true,  false, true,  true,  false, true ), 2 },
                { Bits(true,  true,  true,  true,  false, false, true ), 3 },
                { Bits(false, true,  true,  false, false, true,  true ), 4 },
                { Bits(true,  false, true,  true,  false, true,  true ), 5 },
                { Bits(true,  false, true,  true,  true,  true,  true ), 6 },
                { Bits(true,  true,  true,  false, false, false, false), 7 },
                { Bits(true,  true,  true,  true,  true,  true,  true ), 8 },
                { Bits(true,  true,  true,  true,  false, true,  true ), 9 },
            };

            return map.TryGetValue(mask, out int val) ? val : (int?)null;
        }

        /// <summary>
        /// Uvoľní ONNX session — zavolané pri zastavení aplikácie.
        /// </summary>
        public void Dispose()
        {
            _onnxSession?.Dispose();
            _onnxSession = null;
        }
    }
}
