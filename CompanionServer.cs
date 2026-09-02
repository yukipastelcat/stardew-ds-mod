using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace StardewDS
{
    /// <summary>
    /// A tiny local HTTP/WebSocket server the companion app connects to
    /// for live game state, and sends item-selection/move requests to.
    ///
    /// State goes out over a WebSocket (<c>GET /ws</c>) that this class
    /// pushes a fresh snapshot to whenever it actually changes — see
    /// <see cref="UpdateSnapshot"/> — rather than the app polling for it
    /// (the plain <c>GET /state</c> route is still served too, for manual
    /// testing/compatibility). Runs on its own background thread and
    /// never touches Stardew Valley game objects directly: it only reads
    /// the latest cached <see cref="GameStateSnapshot"/> (published from
    /// the main game thread once per tick via <see cref="UpdateSnapshot"/>)
    /// and hands off incoming "select slot"/"move item" requests to
    /// callbacks, which are expected to queue them for the main thread
    /// instead of applying them here — Stardew Valley's game state is not
    /// safe to mutate off the main thread.
    /// </summary>
    internal sealed class CompanionServer
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        private readonly IMonitor _monitor;
        private readonly int _port;
        private readonly Action<int> _onSelectRequested;
        private readonly Action<int, int> _onMoveRequested;
        private readonly Action _onOrganizeRequested;
        private readonly Action _onOpenJournalRequested;

        private HttpListener? _listener;
        private Thread? _thread;
        private volatile GameStateSnapshot? _snapshot;
        private volatile bool _running;

        // Connected companion-app WebSocket clients, pushed a fresh JSON
        // snapshot whenever it actually changes (see UpdateSnapshot) —
        // this is what replaced the app's old 1.5s /state poll. /select
        // and /move stay plain POST requests; only the state push moved
        // to a socket.
        private readonly List<WebSocket> _sockets = new();
        private readonly object _socketsLock = new();
        private string? _lastBroadcastJson;

        public CompanionServer(IMonitor monitor, int port, Action<int> onSelectRequested, Action<int, int> onMoveRequested, Action onOrganizeRequested, Action onOpenJournalRequested)
        {
            this._monitor = monitor;
            this._port = port;
            this._onSelectRequested = onSelectRequested;
            this._onMoveRequested = onMoveRequested;
            this._onOrganizeRequested = onOrganizeRequested;
            this._onOpenJournalRequested = onOpenJournalRequested;
        }

        /// <summary>Called from the main game thread each tick to publish the latest state. Pass null when no save is loaded. Cheap no-op for connected WebSocket clients when nothing actually changed since the last call — only a real difference triggers a push, so this can safely be called every tick instead of needing its own throttle.</summary>
        public void UpdateSnapshot(GameStateSnapshot? snapshot)
        {
            this._snapshot = snapshot;

            string json = snapshot is null ? "{\"connected\":false}" : JsonSerializer.Serialize(snapshot, JsonOptions);
            if (json == this._lastBroadcastJson)
                return;

            this._lastBroadcastJson = json;
            this.Broadcast(json);
        }

        /// <summary>Fire-and-forget push of <paramref name="json"/> to every connected WebSocket client. Never blocks the caller (the main game thread) — each send is its own Task, and a slow/dead client can't hold up the others.</summary>
        private void Broadcast(string json)
        {
            List<WebSocket> sockets;
            lock (this._socketsLock)
            {
                if (this._sockets.Count == 0)
                    return;
                sockets = new List<WebSocket>(this._sockets);
            }

            byte[] bytes = Encoding.UTF8.GetBytes(json);
            foreach (WebSocket socket in sockets)
                _ = SendAsync(socket, bytes);
        }

        private static async Task SendAsync(WebSocket socket, byte[] bytes)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
                // Dead/dropped connection — HandleWebSocketAsync's receive
                // loop will notice and remove it from _sockets.
            }
        }

        /// <summary>Upgrades the request to a WebSocket, registers it, sends the current snapshot immediately (so the client isn't left waiting for the next state change), then just keeps receiving (the client never sends anything over this socket — /select and /move stay separate POST requests) until it closes or drops, at which point it's unregistered and disposed. Runs as its own fire-and-forget task per connection so it never blocks the accept loop in <see cref="Listen"/>.</summary>
        private async Task HandleWebSocketAsync(HttpListenerContext ctx)
        {
            WebSocket socket;
            try
            {
                WebSocketContext wsContext = await ctx.AcceptWebSocketAsync(subProtocol: null);
                socket = wsContext.WebSocket;
            }
            catch (Exception ex)
            {
                this._monitor.Log($"WebSocket handshake failed: {ex.Message}", LogLevel.Warn);
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); }
                catch { /* already closed */ }
                return;
            }

            lock (this._socketsLock)
                this._sockets.Add(socket);
            this._monitor.Log("Companion app connected via WebSocket.", LogLevel.Trace);

            try
            {
                string? current = this._lastBroadcastJson;
                if (current is not null)
                    await SendAsync(socket, Encoding.UTF8.GetBytes(current));

                var buffer = new byte[1024];
                while (socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        break;
                    }
                }
            }
            catch
            {
                // Client disconnected uncleanly (app backgrounded, wifi
                // dropped, etc.) — not an error, just fall through to cleanup.
            }
            finally
            {
                lock (this._socketsLock)
                    this._sockets.Remove(socket);
                socket.Dispose();
                this._monitor.Log("Companion app WebSocket disconnected.", LogLevel.Trace);
            }
        }

        public void Start()
        {
            if (this._running)
                return;

            // Bind on all interfaces so a phone on the same Wi-Fi/LAN can
            // reach it, not just requests from this machine.
            this._listener = new HttpListener();
            this._listener.Prefixes.Add($"http://+:{this._port}/");

            try
            {
                this._listener.Start();
            }
            catch (Exception ex)
            {
                // Binding to all interfaces ("+") can be refused on some
                // setups; fall back to localhost-only, which still works
                // for testing from the same machine.
                this._monitor.Log($"Could not bind {this._port} on all interfaces ({ex.Message}); falling back to localhost-only. A phone on another device won't be able to reach it until this is resolved.", LogLevel.Warn);

                this._listener = new HttpListener();
                this._listener.Prefixes.Add($"http://localhost:{this._port}/");
                this._listener.Start();
            }

            this._running = true;
            this._thread = new Thread(this.Listen) { IsBackground = true, Name = "StardewDS companion server" };
            this._thread.Start();

            this._monitor.Log($"Companion server listening on port {this._port}.", LogLevel.Info);
        }

        public void Stop()
        {
            this._running = false;
            try { this._listener?.Stop(); }
            catch { /* already stopped */ }

            List<WebSocket> sockets;
            lock (this._socketsLock)
            {
                sockets = new List<WebSocket>(this._sockets);
                this._sockets.Clear();
            }
            foreach (WebSocket socket in sockets)
            {
                try { socket.Abort(); }
                catch { /* already closed */ }
            }
        }

        private void Listen()
        {
            while (this._running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = this._listener!.GetContext(); // blocks until a request arrives
                }
                catch (Exception)
                {
                    break; // listener was stopped
                }

                try
                {
                    this.Handle(ctx);
                }
                catch (Exception ex)
                {
                    this._monitor.Log($"Error handling companion request: {ex}", LogLevel.Error);
                    try
                    {
                        ctx.Response.StatusCode = 500;
                        ctx.Response.Close();
                    }
                    catch { /* response already closed */ }
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            HttpListenerRequest request = ctx.Request;
            HttpListenerResponse response = ctx.Response;
            response.Headers.Add("Access-Control-Allow-Origin", "*");

            string path = request.Url?.AbsolutePath ?? "";

            // The Flutter app's web build (e.g. run via Docker, to avoid
            // needing an emulator) runs in an actual browser, which sends a
            // CORS preflight OPTIONS request before the real POST /select —
            // native builds never hit this branch.
            if (request.HttpMethod == "OPTIONS")
            {
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                response.StatusCode = 204;
                response.Close();
                return;
            }

            // Live state now pushes over this socket instead of the app
            // polling /state — handled as its own fire-and-forget task so
            // a long-lived connection never blocks this accept loop from
            // handling the next request (see HandleWebSocketAsync).
            if (request.HttpMethod == "GET" && path == "/ws" && request.IsWebSocketRequest)
            {
                _ = this.HandleWebSocketAsync(ctx);
                return;
            }

            // Kept for compatibility/manual testing — the app itself now
            // gets state via /ws, not by polling this.
            if (request.HttpMethod == "GET" && path == "/state")
            {
                GameStateSnapshot? snap = this._snapshot;
                string json = snap is null
                    ? "{\"connected\":false}"
                    : JsonSerializer.Serialize(snap, JsonOptions);
                WriteJson(response, json);
            }
            else if (request.HttpMethod == "POST" && path == "/select")
            {
                using StreamReader reader = new(request.InputStream, request.ContentEncoding);
                string body = reader.ReadToEnd();

                try
                {
                    SelectRequest? payload = JsonSerializer.Deserialize<SelectRequest>(body, JsonOptions);
                    if (payload is not null)
                        this._onSelectRequested(payload.Index);
                    WriteJson(response, "{\"ok\":true}");
                }
                catch (JsonException)
                {
                    response.StatusCode = 400;
                    WriteJson(response, "{\"ok\":false,\"error\":\"invalid request body\"}");
                }
            }
            else if (request.HttpMethod == "POST" && path == "/move")
            {
                // Moves (swaps) the items at two backpack slots — the
                // app's drag-and-drop between slots. Applied on the main
                // thread by ModEntry.ApplyPendingMove, same
                // queue-and-apply pattern as /select.
                using StreamReader reader = new(request.InputStream, request.ContentEncoding);
                string body = reader.ReadToEnd();

                try
                {
                    MoveRequest? payload = JsonSerializer.Deserialize<MoveRequest>(body, JsonOptions);
                    if (payload is not null)
                        this._onMoveRequested(payload.From, payload.To);
                    WriteJson(response, "{\"ok\":true}");
                }
                catch (JsonException)
                {
                    response.StatusCode = 400;
                    WriteJson(response, "{\"ok\":false,\"error\":\"invalid request body\"}");
                }
            }
            else if (request.HttpMethod == "POST" && path == "/organize")
            {
                // The app's organize button — no body needed. Applied on
                // the main thread by ModEntry.ApplyPendingOrganize, same
                // queue-and-apply pattern as /select and /move.
                this._onOrganizeRequested();
                WriteJson(response, "{\"ok\":true}");
            }
            else if (request.HttpMethod == "POST" && path == "/open-journal")
            {
                // The app's new Journal button — no body needed. Applied
                // on the main thread by ModEntry.ApplyPendingOpenJournal,
                // same queue-and-apply pattern as /organize; opens the
                // real vanilla QuestLog menu, same as pressing the
                // journal key (or the in-game quest-log button) would.
                this._onOpenJournalRequested();
                WriteJson(response, "{\"ok\":true}");
            }
            else if (request.HttpMethod == "GET" && path == "/sprite")
            {
                // Real item icons, cropped from the player's own loaded
                // game textures by SpriteCache (see GameStateSnapshot.Capture,
                // which warms this cache every tick for whatever's actually
                // in the inventory/equipped) — not bundled or downloaded by
                // the app itself.
                string? id = request.QueryString["id"];

                if (string.IsNullOrEmpty(id))
                {
                    response.StatusCode = 400;
                    WriteJson(response, "{\"error\":\"missing ?id= query parameter\"}");
                    return;
                }

                byte[]? png = SpriteCache.TryGet(id);

                if (png is null)
                {
                    response.StatusCode = 404;
                    WriteJson(response, "{\"error\":\"sprite not cached yet\"}");
                }
                else
                {
                    response.ContentType = "image/png";
                    response.ContentLength64 = png.Length;
                    response.OutputStream.Write(png, 0, png.Length);
                    response.OutputStream.Close();
                }
            }
            else if (request.HttpMethod == "GET" && path == "/animal-sprite")
            {
                // Real farm-animal AND house-pet (Cat/Dog) breed
                // portraits, cropped from the game's own loaded animal
                // textures by AnimalIconCache (see
                // GameStateSnapshot.Capture/CollectPets, which warm this
                // cache every tick for whatever's actually on the farm
                // or in the farmhouse) — not bundled or downloaded by
                // the app itself. Keyed by breed (`type`), not by
                // individual animal/pet — see AnimalIconCache's doc
                // comment for the pet cache-key scheme.
                string? type = request.QueryString["type"];

                if (string.IsNullOrEmpty(type))
                {
                    response.StatusCode = 400;
                    WriteJson(response, "{\"error\":\"missing ?type= query parameter\"}");
                    return;
                }

                byte[]? animalPng = AnimalIconCache.TryGet(type);

                if (animalPng is null)
                {
                    response.StatusCode = 404;
                    WriteJson(response, "{\"error\":\"breed not cached yet\"}");
                }
                else
                {
                    response.ContentType = "image/png";
                    response.ContentLength64 = animalPng.Length;
                    response.OutputStream.Write(animalPng, 0, animalPng.Length);
                    response.OutputStream.Close();
                }
            }
            else if (request.HttpMethod == "GET" && path == "/portrait")
            {
                // The player's actual composited farmer sprite — see
                // PortraitRenderer.cs — refreshed periodically on the main
                // thread, served here as a plain PNG.
                byte[]? png = PortraitRenderer.TryGet();

                if (png is null)
                {
                    response.StatusCode = 404;
                    WriteJson(response, "{\"error\":\"portrait not rendered yet\"}");
                }
                else
                {
                    response.ContentType = "image/png";
                    response.ContentLength64 = png.Length;
                    response.OutputStream.Write(png, 0, png.Length);
                    response.OutputStream.Close();
                }
            }
            else if (request.HttpMethod == "GET" && path == "/mini-portrait")
            {
                // The real vanilla head+hair-only icon (no shirt/pants/
                // hat) — the exact FarmerRenderer.drawMiniPortrat call
                // GameMenu's Skills tab and MapPage's own player marker
                // both use — see MiniPortraitRenderer.cs.
                byte[]? png = MiniPortraitRenderer.TryGet();

                if (png is null)
                {
                    response.StatusCode = 404;
                    WriteJson(response, "{\"error\":\"mini portrait not rendered yet\"}");
                }
                else
                {
                    response.ContentType = "image/png";
                    response.ContentLength64 = png.Length;
                    response.OutputStream.Write(png, 0, png.Length);
                    response.OutputStream.Close();
                }
            }
            else if (request.HttpMethod == "GET" && path == "/icon")
            {
                // Fixed UI icons (bottom-nav tab icons) cropped from the
                // game's own Cursors sheet — see UiIconCache.cs.
                string? name = request.QueryString["name"];

                if (string.IsNullOrEmpty(name))
                {
                    response.StatusCode = 400;
                    WriteJson(response, "{\"error\":\"missing ?name= query parameter\"}");
                    return;
                }

                byte[]? png = UiIconCache.TryGet(name);

                if (png is null)
                {
                    response.StatusCode = 404;
                    WriteJson(response, "{\"error\":\"unknown icon name, or not cached yet\"}");
                }
                else
                {
                    response.ContentType = "image/png";
                    response.ContentLength64 = png.Length;
                    response.OutputStream.Write(png, 0, png.Length);
                    response.OutputStream.Close();
                }
            }
            else if (request.HttpMethod == "GET" && path == "/season-icon")
            {
                WriteIconOrError(response, request.QueryString["n"], n => SeasonWeatherIconCache.TryGetSeason(n));
            }
            else if (request.HttpMethod == "GET" && path == "/weather-icon")
            {
                WriteIconOrError(response, request.QueryString["n"], n => SeasonWeatherIconCache.TryGetWeather(n));
            }
            else if (request.HttpMethod == "GET" && path == "/portrait-background")
            {
                bool night = request.QueryString["night"] == "true" || request.QueryString["night"] == "1";
                byte[]? png = PortraitBackgroundCache.TryGet(night);

                if (png is null)
                {
                    response.StatusCode = 404;
                    WriteJson(response, "{\"error\":\"not rendered yet\"}");
                }
                else
                {
                    response.ContentType = "image/png";
                    response.ContentLength64 = png.Length;
                    response.OutputStream.Write(png, 0, png.Length);
                    response.OutputStream.Close();
                }
            }
            else if (request.HttpMethod == "GET" && path == "/window-border")
            {
                byte[]? png = WindowBorderCache.TryGet();

                if (png is null)
                {
                    response.StatusCode = 404;
                    WriteJson(response, "{\"error\":\"not rendered yet\"}");
                }
                else
                {
                    response.ContentType = "image/png";
                    response.ContentLength64 = png.Length;
                    response.OutputStream.Write(png, 0, png.Length);
                    response.OutputStream.Close();
                }
            }
            else if (request.HttpMethod == "GET" && path == "/clock-box")
            {
                byte[]? png = ClockCache.TryGetBox();

                if (png is null)
                {
                    response.StatusCode = 404;
                    WriteJson(response, "{\"error\":\"not rendered yet\"}");
                }
                else
                {
                    response.ContentType = "image/png";
                    response.ContentLength64 = png.Length;
                    response.OutputStream.Write(png, 0, png.Length);
                    response.OutputStream.Close();
                }
            }
            else if (request.HttpMethod == "GET" && path == "/clock-needle")
            {
                byte[]? png = ClockCache.TryGetNeedle();

                if (png is null)
                {
                    response.StatusCode = 404;
                    WriteJson(response, "{\"error\":\"not rendered yet\"}");
                }
                else
                {
                    response.ContentType = "image/png";
                    response.ContentLength64 = png.Length;
                    response.OutputStream.Write(png, 0, png.Length);
                    response.OutputStream.Close();
                }
            }
            else if (request.HttpMethod == "GET" && path == "/slot-frame")
            {
                // The backpack grid's own slot background frame — see
                // InventorySlotIconCache.cs.
                byte[]? png = InventorySlotIconCache.TryGetSlotFrame();

                if (png is null)
                {
                    response.StatusCode = 404;
                    WriteJson(response, "{\"error\":\"not rendered yet\"}");
                }
                else
                {
                    response.ContentType = "image/png";
                    response.ContentLength64 = png.Length;
                    response.OutputStream.Write(png, 0, png.Length);
                    response.OutputStream.Close();
                }
            }
            else if (request.HttpMethod == "GET" && path == "/slot-locked-overlay")
            {
                // Drawn on top of /slot-frame (at 50% opacity, same as the
                // vanilla menu) for a backpack slot beyond the player's
                // current capacity — see InventorySlotIconCache.cs.
                byte[]? png = InventorySlotIconCache.TryGetLockedOverlay();

                if (png is null)
                {
                    response.StatusCode = 404;
                    WriteJson(response, "{\"error\":\"not rendered yet\"}");
                }
                else
                {
                    response.ContentType = "image/png";
                    response.ContentLength64 = png.Length;
                    response.OutputStream.Write(png, 0, png.Length);
                    response.OutputStream.Close();
                }
            }
            else if (request.HttpMethod == "GET" && path == "/world-map")
            {
                // The real vanilla world-map background (MapPage's own
                // texture) — see WorldMapCache.cs. Combine with
                // GameStateSnapshot's MapMarkerX/MapMarkerY (0-1
                // fractions of this image's own width/height) to place
                // the player's position marker.
                byte[]? png = WorldMapCache.TryGet();

                if (png is null)
                {
                    response.StatusCode = 404;
                    WriteJson(response, "{\"error\":\"not rendered yet\"}");
                }
                else
                {
                    response.ContentType = "image/png";
                    response.ContentLength64 = png.Length;
                    response.OutputStream.Write(png, 0, png.Length);
                    response.OutputStream.Close();
                }
            }
            else if (request.HttpMethod == "GET" && path == "/slot-selected-frame")
            {
                // Used *in place of* /slot-frame (not composited on top of
                // it) for whichever backpack slot is the player's currently
                // selected/equipped item — the real vanilla hotbar's own
                // highlighted-slot background — see InventorySlotIconCache.cs.
                byte[]? png = InventorySlotIconCache.TryGetSelectedFrame();

                if (png is null)
                {
                    response.StatusCode = 404;
                    WriteJson(response, "{\"error\":\"not rendered yet\"}");
                }
                else
                {
                    response.ContentType = "image/png";
                    response.ContentLength64 = png.Length;
                    response.OutputStream.Write(png, 0, png.Length);
                    response.OutputStream.Close();
                }
            }
            else
            {
                response.StatusCode = 404;
                WriteJson(response, "{\"error\":\"not found\"}");
            }
        }

        /// <summary>Shared by /season-icon and /weather-icon: parses the ?n= query param as an int and looks it up via <paramref name="lookup"/>, writing the PNG or an error response.</summary>
        private static void WriteIconOrError(HttpListenerResponse response, string? rawN, System.Func<int, byte[]?> lookup)
        {
            if (string.IsNullOrEmpty(rawN) || !int.TryParse(rawN, out int n))
            {
                response.StatusCode = 400;
                WriteJson(response, "{\"error\":\"missing or invalid ?n= query parameter\"}");
                return;
            }

            byte[]? png = lookup(n);
            if (png is null)
            {
                response.StatusCode = 404;
                WriteJson(response, "{\"error\":\"not cached yet\"}");
                return;
            }

            response.ContentType = "image/png";
            response.ContentLength64 = png.Length;
            response.OutputStream.Write(png, 0, png.Length);
            response.OutputStream.Close();
        }

        private static void WriteJson(HttpListenerResponse response, string json)
        {
            response.ContentType = "application/json";
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }

        private sealed class SelectRequest
        {
            public int Index { get; set; }
        }

        private sealed class MoveRequest
        {
            public int From { get; set; }
            public int To { get; set; }
        }
    }
}
