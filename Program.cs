using Serilog;
using Serilog.Events;

// ========================= SERILOG =========================
// Konfigurujeme pred builder — aby zachytil aj chyby zo štartu aplikácie

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    // ASP.NET Core interné logy (routing, middleware) — len Warning a vyššie
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    // Zápis do konzoly (rovnaký výstup ako pred tým)
    .WriteTo.Console(outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} | {Level:u4} | {SourceContext} | {Message:lj}{NewLine}{Exception}")
    // Zápis do súboru — denná rotácia + rotácia podľa veľkosti (5 MB)
    // Výsledok: logs/log_2026-02-26.txt, logs/log_2026-02-26_001.txt, ...
    .WriteTo.File(
        path: "logs/log_.txt",
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 5 * 1024 * 1024,   // 5 MB — rovnaké ako pôvodný Logger.cs
        rollOnFileSizeLimit: true,               // pri prekročení vytvorí _001, _002...
        retainedFileCountLimit: null,            // nemazať staré logy automaticky
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} | {Level:u4} | {SourceContext} | {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// Nahraď zabudovaný ASP.NET Core logger Serilogom
builder.Host.UseSerilog();

// ========================= REGISTRÁCIA SLUŽIEB =========================

// Razor Pages — HTML stránky v Pages/ adresári
builder.Services.AddRazorPages();

// SignalR — real-time komunikácia server → prehliadač (pre rozpoznané čísla, logy)
builder.Services.AddSignalR();

// Controllers — potrebné pre StreamController (/stream endpoint)
builder.Services.AddControllers();

// ImagePipelineService — spracovanie obrazu + ONNX rozpoznávanie
// Singleton = jedna inštancia zdieľaná medzi MjpegCaptureService a DI kontajnerom
builder.Services.AddSingleton<GUIVideoProcessing.Web.Services.ImagePipelineService>();

// MjpegCaptureService registrovaná DVAKRÁT — zámerné:
//
// 1. AddSingleton → DI kontajner vytvorí jednu inštanciu a zdieľa ju
//    StreamController si vypýta MjpegCaptureService cez konštruktor
//    a dostane TÚ ISTÚ inštanciu ako AddHostedService nižšie
//
// 2. AddHostedService → zaregistruje TÚ ISTÚ Singleton inštanciu ako BackgroundService
//    Framework ju automaticky spustí (ExecuteAsync) pri štarte aplikácie
//
// Bez AddSingleton by Controller dostal inú (prázdnu) inštanciu než tú,
// ktorá skutočne číta stream — GetLastFrame() by vždy vracala null
builder.Services.AddSingleton<GUIVideoProcessing.Web.Services.MjpegCaptureService>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<GUIVideoProcessing.Web.Services.MjpegCaptureService>());

// InfluxWriterService — asynchrónny zápis rozpoznaných hodnôt do InfluxDB
// Singleton = ImagePipelineService si ho vypýta cez DI (Enqueue)
// HostedService = framework spustí ExecuteAsync (reconnect + write loop) pri štarte
builder.Services.AddSingleton<GUIVideoProcessing.Web.Services.InfluxWriterService>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<GUIVideoProcessing.Web.Services.InfluxWriterService>());

// ========================= ZOSTAVENIE APLIKÁCIE =========================
var app = builder.Build();

// ========================= HTTP PIPELINE (MIDDLEWARE) =========================

// V produkcii: zobraz chybovú stránku namiesto stack trace
// V Development móde: ASP.NET Core zobrazí detailný stack trace automaticky
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // HSTS = HTTP Strict Transport Security
    // Hovorí prehliadaču aby vždy používal HTTPS (platí 30 dní)
    app.UseHsts();
}

// UseHttpsRedirection() — vypnuté, aplikácia beží v Docker len cez HTTP

// Zisti kam request patrí (URL routing)
app.UseRouting();

// Over oprávnenia (login, role) — zatiaľ nepoužívame, ale pipeline to vyžaduje
app.UseAuthorization();

// Statické súbory z wwwroot/ (CSS, JS, obrázky)
// UseStaticFiles = funguje vždy (Development aj Production, bez manifestu)
app.UseStaticFiles();

// Razor Pages — namapuj URL na stránky v Pages/
app.MapRazorPages();

// SignalR Hub — trvalé WebSocket spojenie na /hub
app.MapHub<GUIVideoProcessing.Web.Hubs.StreamHub>("/hub");

// Controllers — namapuj URL na Controller akcie (vrátane GET /stream)
app.MapControllers();

app.Run();
