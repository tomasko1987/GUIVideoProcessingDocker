using Microsoft.AspNetCore.SignalR;

namespace GUIVideoProcessing.Web.Hubs
{
    /// <summary>
    /// SignalR Hub — trvalé spojenie medzi serverom a prehliadačom.
    /// Server môže kedykoľvek pushnúť správu všetkým pripojeným klientom.
    /// </summary>
    public class StreamHub : Hub
    {
        private readonly ILogger<StreamHub> _logger;

        public StreamHub(ILogger<StreamHub> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Zavolané automaticky keď sa prehliadač pripojí na /hub.
        /// Context.ConnectionId = unikátne ID spojenia (napr. "abc123")
        /// </summary>
        public override Task OnConnectedAsync()
        {
            _logger.LogInformation("SignalR: Client connected. ConnectionId={Id}", Context.ConnectionId);
            return base.OnConnectedAsync();
        }

        /// <summary>
        /// Zavolané automaticky keď sa prehliadač odpojí (zavretie okna, strata siete).
        /// exception = null ak sa odpojil čisto (zavretie okna), inak dôvod chyby.
        /// </summary>
        public override Task OnDisconnectedAsync(Exception? exception)
        {
            if (exception == null)
                _logger.LogInformation("SignalR: Client disconnected. ConnectionId={Id}", Context.ConnectionId);
            else
                _logger.LogWarning("SignalR: Client disconnected with error. ConnectionId={Id} Error={Msg}",
                    Context.ConnectionId, exception.Message);

            return base.OnDisconnectedAsync(exception);
        }

        // Metódy sem môže volať klient (prehliadač) → server.
        // Server posiela správy klientom cez IHubContext<StreamHub> injektovaný do služieb.
    }
}
