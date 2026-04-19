/*▄▄▄    ███▄ ▄███▓  ▄████  ▄▄▄██▀▀▀▓█████▄▄▄█████▓
▓█████▄ ▓██▒▀█▀ ██▒ ██▒ ▀█▒   ▒██   ▓█   ▀▓  ██▒ ▓▒
▒██▒ ▄██▓██    ▓██░▒██░▄▄▄░   ░██   ▒███  ▒ ▓██░ ▒░
▒██░█▀  ▒██    ▒██ ░▓█  ██▓▓██▄██▓  ▒▓█  ▄░ ▓██▓ ░ 
░▓█  ▀█▓▒██▒   ░██▒░▒▓███▀▒ ▓███▒   ░▒████▒ ▒██▒ ░ 
░▒▓███▀▒░ ▒░   ░  ░ ░▒   ▒  ▒▓▒▒░   ░░ ▒░ ░ ▒ ░░   
▒░▒   ░ ░  ░      ░  ░   ░  ▒ ░▒░    ░ ░  ░   ░    
 ░    ░ ░      ░   ░ ░   ░  ░ ░ ░      ░    ░      
 ░             ░         ░  ░   ░      ░  ░*/
using LZ4;
using ProtoBuf;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Net.WebSockets;
using System.Reflection;
using UnityEngine;
static class EmbededDlls
{
    public static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        var requestedName = new AssemblyName(args.Name).Name + ".dll";
        Console.WriteLine($"Resolving managed: {requestedName}");
        var resourceName = requestedName switch
        {
            "LZ4.dll" => "RustRelay9._0.Libs.LZ4.dll",
            "LZ4pn.dll" => "RustRelay9._0.Libs.LZ4pn.dll",
            "Rust.Polyfills.dll" => "RustRelay9._0.Libs.Rust.Polyfills.dll",
            _ => null
        };
        if (resourceName == null) { return null; }
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) { return null; }
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Assembly.Load(ms.ToArray());
    }
}
namespace RustRelayReceiver
{
    class Program
    {
        // Configuration
        public static string Version = "RustRelay9.0 V004";
        private static string _baseUrl = "http://localhost:8080";
        private static string _authToken = "demotoken";
        private static readonly HttpListener _httpListener = new();
        private static readonly ConcurrentDictionary<WebSocketClient, string?> _webSocketClients = new();
        private static readonly ConcurrentDictionary<string, ServerInfo> _connectedServers = new();
        private static readonly Dictionary<string, (WorldSerialization ws, DateTime loadedAt, string filePath)> _mapCache = new();
        private static readonly SemaphoreSlim _fileWriteSemaphore = new(1, 1);
        private static readonly ConcurrentDictionary<string, Dictionary<uint, string>> _wipeStringPools = new();
        private static readonly ConcurrentDictionary<uint, string> _globalStringPool = new();
        private static readonly ConcurrentDictionary<WebSocketClient, byte[]> _clientBuffers = new();
        private static readonly ConcurrentDictionary<string, (byte[] Data, long Timestamp)> _mapDataCache = new();
        private static readonly object _mapCacheLock = new();
        private const int MAP_CACHE_TTL_MINUTES = 30;
        private static long _totalPacketsReceived = 0;
        private static readonly long _startTime = Stopwatch.GetTimestamp();
        private const int MarkerMagic = 1398035026;
        private const int MarkerLength = 12;
        public static readonly DateTime UnixEpoch = new();
        private static readonly object _consoleLock = new();
        private static int _headerHeight = 0;
        private static readonly string DataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RelayData");
        private static readonly object FileWriteLock = new();
        public static readonly string[] MapLayers = new string[] { "terrain", "height", "splat", "biome", "topology", "alpha", "water" };
        public static readonly ConcurrentDictionary<string, ConcurrentDictionary<ulong, TrackedEntity>> _entitiesByWipe = new();
        public static readonly ConcurrentDictionary<string, ConcurrentDictionary<ulong, TrackedEffect>> _effectsByWipe = new();
        public static readonly ConcurrentDictionary<string, bool> _wipeIds = new();
        public struct VectorData
        {
            [NonSerialized] public float x;
            [NonSerialized] public float y;
            [NonSerialized] public float z;
            public VectorData(float x, float y, float z)
            {
                this.x = x;
                this.y = y;
                this.z = z;
            }
            [JsonPropertyName("x")]
            public float X => x;
            [JsonPropertyName("y")]
            public float Y => y;
            [JsonPropertyName("z")]
            public float Z => z;
        }
        private enum MessageType : byte
        {
            Welcome = 1,
            Auth = 2,
            Approved = 3,
            Ready = 4,
            Entities = 5,
            EntityDestroy = 6,
            GroupChange = 7,
            GroupDestroy = 8,
            RPCMessage = 9,
            EntityPosition = 10,
            ConsoleMessage = 11,
            ConsoleCommand = 12,
            Effect = 13,
            DisconnectReason = 14,
            Tick = 15,
            Message = 16,
            RequestUserInformation = 17,
            GiveUserInformation = 18,
            GroupEnter = 19,
            GroupLeave = 20,
            VoiceData = 21,
            EAC = 22,
            EntityFlags = 23,
            World = 24,
            ConsoleReplicatedVars = 25,
            QueueUpdate = 26,
            SyncVar = 27,
            PackedSyncVar = 28,
            Last = 28,
        }
        #region Server
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += EmbededDlls.Resolve;
            PrintStatistics();
            ParseArguments(args);
            Console.WriteLine($"Starting {Version} relay server on {_baseUrl}");
            StartHttpListener();
            Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(2000);
                    PrintStatistics();
                }
            });
            var mre = new ManualResetEvent(false);
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                mre.Set();
            };
            mre.WaitOne();
            Console.WriteLine("\nShutting down...");
            StopAll();
        }
        private static void StartHttpListener()
        {
            _httpListener.Prefixes.Clear();
            _httpListener.Prefixes.Add(_baseUrl.TrimEnd('/') + "/");
            try { _httpListener.Start(); }
            catch (Exception ex)
            {
                LogDebug($"Failed to start: {ex.Message}");
                System.Environment.Exit(1);
            }
            Task.Run(async () =>
            {
                while (_httpListener.IsListening)
                {
                    try
                    {
                        var context = await _httpListener.GetContextAsync();
                        ProcessRequestAsync(context);
                    }
                    catch (HttpListenerException) { break; }
                    catch (Exception ex) { LogDebug($"Accept error: {ex.Message}"); }
                }
            });
        }
        private class WebSocketClient
        {
            public System.Net.WebSockets.WebSocket? Socket { get; set; }
            public string? WipeId { get; set; }
            public DateTime ConnectedAt { get; set; }
        }
        public static void SafeWrite(HttpListenerResponse resp, byte[] data, string contentType = "application/octet-stream")
        {
            try
            {
                if (resp == null || !resp.OutputStream.CanWrite) { return; }
                resp.ContentType = contentType;
                resp.ContentLength64 = data.Length;
                resp.OutputStream.Write(data, 0, data.Length);
                resp.OutputStream.Flush();
            }
            catch (HttpListenerException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception) { }
            finally
            {
                try { resp.OutputStream.Close(); } catch { }
                try { resp.Close(); } catch { }
            }
        }
        private static void OnStringPoolReceived(string? wipeId, Dictionary<uint, string> stringPool)
        {
            _ = SaveStringPoolToFile(wipeId, stringPool);
        }
        private static void OnManifestReceived(string? wipeId, Dictionary<uint, string> manifest)
        {
            SaveManifestToFile(wipeId, manifest);
        }
        private static void OnSnapshotReceived(string? wipeId, byte[] data)
        {
            SaveSnapshotToFile(wipeId, data);
        }
        private static void OnMapFileReceived(string? wipeId, string filename, byte[] data)
        {
            if (wipeId == null) { return; }
            SaveMapFileToDirectory(wipeId, filename, data);
            LogDebug($"[MapFile] {filename}");
        }
        public static event Action<TrackedEntity, string>? OnEntityCreated;
        public static event Action<TrackedEntity, string>? OnEntityUpdated;
        public static event Action<string, ulong>? OnEntityDestroyed;
        public static event Action<EffectData, string>? OnEffectDecoded;
        public static event Action<string, ulong, ulong>? OnRPCReceived;
        #endregion
        #region Processing
        private static async void ProcessRequestAsync(HttpListenerContext context)
        {
            try
            {
                string? serverid = context.Request.Headers["X-Wipe-Id"];
                if (!string.IsNullOrEmpty(serverid)) { CreateDirectories(serverid); }
                if (context.Request.IsWebSocketRequest) { await HandleWebSocketRequest(context); }
                else { _ = Task.Run(() => HandleHttpRequest(context)); }
            }
            catch (Exception ex) { LogDebug($"Processing error: {ex.Message}"); }
            finally { }
        }
        private static async Task<Dictionary<string, byte[]>> ParseMultipartFormData(HttpListenerRequest request)
        {
            var files = new Dictionary<string, byte[]>();
            string contentType = request.ContentType ?? "";
            if (!contentType.Contains("multipart/form-data"))
            {
                throw new Exception("Expected multipart/form-data content type");
            }
            int boundaryIndex = contentType.IndexOf("boundary=");
            if (boundaryIndex < 0)
            {
                throw new Exception("No boundary found");
            }
            string boundary = contentType[(boundaryIndex + 9)..];
            if (boundary.StartsWith("\""))
            {
                int endQuote = boundary.IndexOf('"', 1);
                if (endQuote > 0) { boundary = boundary[1..endQuote]; } else { boundary = boundary[1..]; }
            }
            else
            {
                int endIndex = boundary.Length;
                for (int i = 0; i < boundary.Length; i++)
                {
                    if (boundary[i] == ' ' || boundary[i] == ';')
                    {
                        endIndex = i;
                        break;
                    }
                }
                boundary = boundary[..endIndex];
            }
            using (var ms = new MemoryStream())
            {
                await request.InputStream.CopyToAsync(ms);
                byte[] bodyBytes = ms.ToArray();
                int bodyLength = bodyBytes.Length;
                byte[] boundaryWithPrefix = Encoding.UTF8.GetBytes("--" + boundary);
                byte[] boundaryRaw = Encoding.UTF8.GetBytes(boundary);
                int firstBoundary = FindBytes(bodyBytes, boundaryWithPrefix, 0);
                int startPos = 0;
                if (firstBoundary >= 0) { startPos = firstBoundary + boundaryWithPrefix.Length; }
                else
                {
                    firstBoundary = FindBytes(bodyBytes, boundaryRaw, 0);
                    if (firstBoundary >= 0) { startPos = firstBoundary + boundaryRaw.Length; }
                    else { return files; }
                }
                if (startPos + 1 < bodyLength && bodyBytes[startPos] == '\r' && bodyBytes[startPos + 1] == '\n') { startPos += 2; }
                else if (startPos < bodyLength && bodyBytes[startPos] == '\n') { startPos += 1; }
                byte[] doubleCrlf = Encoding.UTF8.GetBytes("\r\n\r\n");
                byte[] boundaryWithCrlf = Encoding.UTF8.GetBytes("\r\n--" + boundary);
                byte[] finalBoundary = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--");
                int pos = startPos;
                int partNumber = 0;
                while (pos < bodyLength)
                {
                    partNumber++;
                    int headerEnd = FindBytes(bodyBytes, doubleCrlf, pos);
                    if (headerEnd < 0) { break; }
                    byte[] headerBytes = new byte[headerEnd - pos];
                    Array.Copy(bodyBytes, pos, headerBytes, 0, headerBytes.Length);
                    string headers = Encoding.UTF8.GetString(headerBytes);
                    pos = headerEnd + 4;
                    int contentEnd = -1;
                    int nextBound = FindBytes(bodyBytes, boundaryWithCrlf, pos);
                    if (nextBound >= 0) { contentEnd = nextBound; }
                    else
                    {
                        nextBound = FindBytes(bodyBytes, boundaryWithPrefix, pos);
                        if (nextBound >= 0) { contentEnd = nextBound; }
                        else
                        {
                            nextBound = FindBytes(bodyBytes, finalBoundary, pos);
                            if (nextBound >= 0) { contentEnd = nextBound; }
                            else { contentEnd = bodyLength; }
                        }
                    }
                    if (contentEnd > 2 && bodyBytes[contentEnd - 2] == '\r' && bodyBytes[contentEnd - 1] == '\n') { contentEnd -= 2; }
                    int contentLength = contentEnd - pos;
                    if (contentLength > 0)
                    {
                        string? filename = ExtractFilename(headers);
                        string? formName = ExtractFormName(headers);
                        if (!string.IsNullOrEmpty(filename))
                        {
                            byte[] fileContent = new byte[contentLength];
                            Array.Copy(bodyBytes, pos, fileContent, 0, contentLength);
                            files[filename] = fileContent;
                        }
                        else if (!string.IsNullOrEmpty(formName) && formName == "map")
                        {
                            string? originalFilename = ExtractOriginalFilename(headers);
                            if (!string.IsNullOrEmpty(originalFilename)) { filename = originalFilename; }
                            else { filename = $"map_{DateTime.UtcNow:yyyyMMddHHmmss}.bin"; }
                            byte[] fileContent = new byte[contentLength];
                            Array.Copy(bodyBytes, pos, fileContent, 0, contentLength);
                            files[filename] = fileContent;
                        }
                    }
                    else if (contentLength < 0) { break; }
                    if (contentEnd < bodyLength)
                    {
                        pos = contentEnd + 2;
                        if (pos + 2 <= bodyLength && bodyBytes[pos - 2] == '-' && bodyBytes[pos - 1] == '-') { break; }
                        if (pos < bodyLength && bodyBytes[pos] == '\r' && pos + 1 < bodyLength && bodyBytes[pos + 1] == '\n') { pos += 2; }
                    }
                    else { break; }
                }
            }
            return files;
        }
        private static async Task HandleWebSocketRequest(HttpListenerContext context)
        {
            WebSocket? webSocket = null;
            WebSocketClient? client = null;
            byte[]? buffer = null;
            string? wipeId = context.Request.QueryString["wipeId"];
            try
            {
                var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
                webSocket = wsContext.WebSocket;
                LogDebug($"[WS] Client connected - wipeId={wipeId}, token={(!string.IsNullOrEmpty(_authToken))}");
                TrackServerActivity(wipeId);
                client = new WebSocketClient
                {
                    Socket = webSocket,
                    WipeId = wipeId,
                    ConnectedAt = DateTime.UtcNow
                };
                string? remoteIp = context.Request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
                _webSocketClients.AddOrUpdate(client, remoteIp, (_, __) => remoteIp);
                buffer = _clientBuffers.GetOrAdd(client, static _ => ArrayPool<byte>.Shared.Rent(65_536));
                var fragmentBuffer = new List<byte[]>();
                int fragmentTotalLength = 0;
                while (webSocket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await webSocket.ReceiveAsync(buffer, CancellationToken.None);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (WebSocketException ex)
                    {
                        LogDebug($"[WS] Receive error: {ex.Message}");
                        break;
                    }
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        LogDebug($"[WS] Client disconnected - wipeId={wipeId}");
                        break;
                    }
                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        try
                        {
                            if (result.EndOfMessage == false)
                            {
                                byte[] fragmentCopy = new byte[result.Count];
                                Buffer.BlockCopy(buffer, 0, fragmentCopy, 0, result.Count);
                                fragmentBuffer.Add(fragmentCopy);
                                fragmentTotalLength += result.Count;
                            }
                            else
                            {
                                byte[] completeData;
                                if (fragmentBuffer.Count > 0)
                                {
                                    byte[] finalFragment = new byte[result.Count];
                                    Buffer.BlockCopy(buffer, 0, finalFragment, 0, result.Count);
                                    fragmentBuffer.Add(finalFragment);
                                    fragmentTotalLength += result.Count;
                                    completeData = new byte[fragmentTotalLength];
                                    int destOffset = 0;
                                    foreach (var fragment in fragmentBuffer)
                                    {
                                        Buffer.BlockCopy(fragment, 0, completeData, destOffset, fragment.Length);
                                        destOffset += fragment.Length;
                                    }
                                    fragmentBuffer.Clear();
                                    fragmentTotalLength = 0;
                                }
                                else
                                {
                                    completeData = new byte[result.Count];
                                    Buffer.BlockCopy(buffer, 0, completeData, 0, result.Count);
                                }
                                ProcessBinaryPacket(
                              completeData.AsSpan(),
                              completeData.Length,
                                wipeId
                            );
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"[WS] Connection error: {ex.Message}");
            }
            finally
            {
                if (client != null)
                {
                    _webSocketClients.TryRemove(client, out _);
                    if (_clientBuffers.TryRemove(client, out var rented))
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }
                if (webSocket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
                {
                    try
                    {
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closing",
                            CancellationToken.None
                        );
                    }
                    catch { }
                }
                webSocket?.Dispose();
            }
        }
        private static async Task HandleHttpRequest(HttpListenerContext context)
        {
            var response = context.Response;
            var request = context.Request;
            string? wipeId = request.Headers["X-Wipe-Id"];
            string? auth = request.Headers["Authorization"];
            try
            {
                string? path = request.Url?.AbsolutePath.ToLower().Replace("//", "/");
                if (string.IsNullOrEmpty(path)) { await HandleIndexPage(context); return; }
                if (!string.IsNullOrEmpty(auth) && auth.Replace("Bearer ", "") == _authToken)
                {
                    if (path == "/api/stringpool" && request.HttpMethod == "POST")
                    {
                        await HandleStringPoolUpload(context, wipeId);
                    }
                    else if (path == "/api/manifest" && request.HttpMethod == "POST")
                    {
                        await HandleManifestUpload(context, wipeId);
                    }
                    else if (path == "/api/snapshot" && request.HttpMethod == "POST")
                    {
                        await HandleSnapshotUpload(context, wipeId);
                    }
                    else if (path == "/api/mapsnapshot" && request.HttpMethod == "POST")
                    {
                        await HandleMapSnapshotUpload(context, wipeId);
                    }
                }
                else if (path == "/" || path == "/index" || path == "/index.html")
                {
                    await HandleIndexPage(context);
                }
                else if (path.EndsWith("/favicon.ico"))
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using var stream = asm.GetManifestResourceStream("RustRelay9._0.Libs.favicon.ico");
                    if (stream == null) { return; }
                    using var ms = new MemoryStream((int)stream.Length);
                    stream.CopyTo(ms);
                    SafeWrite(context.Response, ms.GetBuffer(), "image/x-icon");
                }
                else if (path == "/api/servers")
                {
                    await HandleServersApi(context);
                }
                else if (path.StartsWith("/api/server/"))
                {
                    await HandleServerDetailApi(context);
                }
                else if (path == "/api/login")
                {
                    await HandleLoginApi(context);
                }
                else if (path.StartsWith("/3dmap/data/"))
                {
                    await Handle3DMapData(context, GetOptions());
                }
                else if (path.StartsWith("/3dmap/entities/"))
                {
                    await Handle3DEntitiesData(context);
                }
                else if (path.StartsWith("/3dmap/update/"))
                {
                    await Handle3DUpdateData(context);
                }
                else if (path.StartsWith("/3dviewer/"))
                {
                    DownloadAndUnzipModels();
                    await Serve3DViewer(context);
                }
                else if (path.StartsWith("/api/download/"))
                {
                    await HandleFileDownload(context);
                }
                else if (path.StartsWith("/api/paths/"))
                {
                    await HandlePathsApi(context);
                }
                else if (path.StartsWith("/api/prefabs/"))
                {
                    await HandlePrefabsApi(context);
                }
                else if (path.StartsWith("/api/mapdata/"))
                {
                    await HandleMapDataApi(context);
                }
                else if (path.StartsWith("/api/savdata/"))
                {
                    await HandleSavDataApi(context);
                }
                else if (path.StartsWith("/models/"))
                {
                    await HandleModels(context);
                }
                else if (path.StartsWith("/server/"))
                {
                    await HandleServerDetailPage(context);
                }
                else
                {
                    await SendHtmlResponse(response, 404, "<h1>404 Not Found</h1>");
                }
            }
            catch (Exception ex)
            {
                LogDebug($"[HTTP] Error: {ex.Message}");
                await SendJsonResponse(response, 500, new { error = ex.Message });
            }
            response.Close();
        }
        private static byte[] GetEntitiesJson(
    string wipeId,
    float? cameraX = null,
    float? cameraY = null,
    float? cameraZ = null,
    bool unlimitedView = false)
        {
            const float DEFAULT_DISTANCE = 800f;
            const float ORIGIN_EXCLUDE_RADIUS = 40f;
            const float ORIGIN_EXCLUDE_RADIUS_SQ = ORIGIN_EXCLUDE_RADIUS * ORIGIN_EXCLUDE_RADIUS;

            Dictionary<ulong, (float x, float y, float z)> _entityLastRotation = new();
            bool nodata = true;

            if (_entitiesByWipe.TryGetValue(wipeId, out var entities)) nodata = false;
            if (_effectsByWipe.TryGetValue(wipeId, out var effects)) nodata = false;

            if (nodata)
                return Encoding.UTF8.GetBytes("{\"timestamp\":0,\"wipeId\":\"\",\"entityCount\":0,\"entities\":[],\"effects\":[]}");

            int entityCount = 0;
            int effectCount = 0;
            List<ulong> toRemove = new();
            List<ulong> rotationChangedIds = new();
            if (entities?.Values.Count > 0)
            {
                foreach (var entity in entities.Values)
                {
                    entityCount++;
                    if (entity.isDestroyed)
                        toRemove.Add(entity.Id);
                }

                foreach (var entity in entities.Values)
                {
                    float px = entity.pos.x;
                    float py = entity.pos.y;
                    float pz = entity.pos.z;
                    if (px == 0 && py == 0 && pz == 0)
                        continue;
                    float originDistSq = px * px + py * py + pz * pz;
                    if (originDistSq <= ORIGIN_EXCLUDE_RADIUS_SQ)
                        continue;

                    if (_entityLastRotation.TryGetValue(entity.Id, out var lastRot))
                    {
                        if (MathF.Abs(entity.rot.x - lastRot.x) > 0.001f ||
                            MathF.Abs(entity.rot.y - lastRot.y) > 0.001f ||
                            MathF.Abs(entity.rot.z - lastRot.z) > 0.001f)
                        {
                            rotationChangedIds.Add(entity.Id);
                        }
                    }
                    else
                    {
                        rotationChangedIds.Add(entity.Id);
                    }
                }
            }

            if (effects != null)
                foreach (var effect in effects.Values)
                    effectCount++;

            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
            writer.WriteStartObject();
            writer.WriteNumber("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            writer.WriteString("wipeId", wipeId);
            writer.WriteNumber("entityCount", entityCount);
            writer.WriteNumber("effectCount", effectCount);
            writer.WriteStartArray("entities");

            if (entities?.Values.Count > 0)
            {
                foreach (var entity in entities.Values)
                {
                    if (entity.Parent != 0 &&
                        !string.IsNullOrEmpty(entity.Playername) &&
                        entities.TryGetValue(entity.Parent, out var parentEntity))
                    {
                        entity.pos.x += parentEntity.pos.x;
                        entity.pos.y += parentEntity.pos.y;
                        entity.pos.z += parentEntity.pos.z;
                    }
                    float px = entity.pos.x;
                    float py = entity.pos.y;
                    float pz = entity.pos.z;
                    if (px == 0 && py == 0 && pz == 0)
                        continue;
                    float originDistSq = px * px + py * py + pz * pz;
                    if (originDistSq <= ORIGIN_EXCLUDE_RADIUS_SQ)
                        continue;
                    if (!unlimitedView && cameraX.HasValue && cameraY.HasValue && cameraZ.HasValue)
                    {
                        float dx = px - cameraX.Value;
                        float dz = pz - cameraZ.Value;
                        if ((dx * dx + dz * dz) > (DEFAULT_DISTANCE * DEFAULT_DISTANCE))
                            continue;
                    }
                    bool hasRotationChange = rotationChangedIds.Contains(entity.Id);
                    writer.WriteStartObject();
                    writer.WriteNumber("id", entity.Id);
                    writer.WriteNumber("prefabId", entity.PrefabId);
                    writer.WriteString("prefabName", entity.PrefabName ?? "");
                    writer.WriteNumber("groupId", entity.GroupId);
                    writer.WriteStartObject("pos");
                    writer.WriteNumber("x", px);
                    writer.WriteNumber("y", py);
                    writer.WriteNumber("z", pz);
                    writer.WriteEndObject();
                    writer.WriteStartObject("rot");
                    writer.WriteNumber("x", entity.rot.x);
                    writer.WriteNumber("y", entity.rot.y);
                    writer.WriteNumber("z", entity.rot.z);
                    writer.WriteEndObject();
                    writer.WriteNumber("flags", entity.Flags);
                    writer.WriteBoolean("isdestroyed", entity.isDestroyed);
                    if (hasRotationChange) writer.WriteNumber("rotUpdated", 1);
                    writer.WriteString("typeClass", entity.TypeClass ?? "");
                    if (entity.MaxHealth.HasValue && entity.MaxHealth.Value > 0)
                        writer.WriteNumber("maxHealth", entity.MaxHealth.Value);
                    if (!string.IsNullOrEmpty(entity.Playername))
                        writer.WriteString("playerName", entity.Playername);
                    if (entity.Health > 0)
                        writer.WriteNumber("health", entity.Health);
                    if (entity.OwnerId.HasValue && entity.OwnerId.Value != 0)
                        writer.WriteNumber("ownerId", entity.OwnerId.Value);
                    if (entity.Extra != null && entity.Extra.Count > 0)
                    {
                        writer.WritePropertyName("extra");
                        writer.WriteRawValue(JsonSerializer.Serialize(entity.Extra));
                    }

                    writer.WriteEndObject();

                    _entityLastRotation[entity.Id] = (entity.rot.x, entity.rot.y, entity.rot.z);
                }
            }

            writer.WriteEndArray();
            writer.WriteStartArray("effects");

            if (effects != null)
            {
                foreach (var effect in effects.Values)
                {
                    float px = effect.pos.x;
                    float py = effect.pos.y;
                    float pz = effect.pos.z;
                    float originDistSq = px * px + py * py + pz * pz;
                    if (originDistSq <= ORIGIN_EXCLUDE_RADIUS_SQ)
                        continue;

                    if (!unlimitedView && cameraX.HasValue && cameraY.HasValue && cameraZ.HasValue)
                    {
                        float dx = px - cameraX.Value;
                        float dz = pz - cameraZ.Value;
                        if ((dx * dx + dz * dz) > (DEFAULT_DISTANCE * DEFAULT_DISTANCE))
                            continue;
                    }
                    writer.WriteStartObject();
                    writer.WriteNumber("id", effect.Id);
                    writer.WriteStartObject("pos");
                    writer.WriteNumber("x", px);
                    writer.WriteNumber("y", py);
                    writer.WriteNumber("z", pz);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            if (entities != null)
            {
                foreach (var id in toRemove)
                {
                    entities.TryRemove(id, out _);
                    _entityLastRotation.Remove(id);
                }
            }

            return stream.ToArray();
        }

        private static void ProcessBinaryPacket(ReadOnlySpan<byte> buffer, int length, string? wipeId)
        {
            try
            {
                if (string.IsNullOrEmpty(wipeId)) { return; }
                Interlocked.Increment(ref _totalPacketsReceived);
                try { UpdateServerStats(wipeId, length); } catch { }
                if (buffer.Length < 4) { return; }
                if (buffer.Length == MarkerLength && BinaryPrimitives.TryReadInt32LittleEndian(buffer, out int magic) && magic == MarkerMagic)
                {
                    try { HandleMarker(wipeId, BinaryPrimitives.ReadInt64LittleEndian(buffer[4..])); } catch { }
                    return;
                }
                int offset = 0;
                if (offset + 1 > buffer.Length) return;
                byte type = buffer[offset];
                offset += 1;
                if (type <= 140) return;
                type -= 140;
                ulong entityId = 0;
                switch ((MessageType)type)
                {
                    case MessageType.GroupEnter:
                    case MessageType.GroupLeave:
                        return;
                    case MessageType.Entities:
                        try
                        {
                            if (offset + sizeof(uint) > buffer.Length) { return; }
                            offset += sizeof(uint);
                            int entityDataLength = buffer.Length - offset;
                            if (entityDataLength <= 0) { return; }
                            ReadOnlySpan<byte> entityBytes = buffer[offset..];
                            var entity = Entity.Deserialize(entityBytes.ToArray());
                            if (entity != null && entity.baseNetworkable != null)
                            {
                                var bn = entity.baseNetworkable;
                                ulong pid = 0;
                                try
                                {
                                    if (entity.parent != null)
                                    {
                                        pid = entity.parent.uid.Value;
                                    }
                                }
                                catch { pid = 0; }
                                string pname = "";
                                try { pname = entity.basePlayer?.name ?? ""; } catch { }
                                float h = 0f;
                                try { h = entity.baseCombat?.health ?? 0f; } catch { }
                                var tracked = new TrackedEntity
                                {
                                    Id = bn.uid.Value,
                                    PrefabId = bn.prefabID,
                                    GroupId = bn.group,
                                    PrefabName = "",
                                    Parent = pid,
                                    Playername = pname,
                                    Health = h,
                                    TypeClass = "",
                                    MaxHealth = 0,
                                };
                                try
                                {
                                    tracked.PrefabName = Path.GetFileName(GetStringFromPool(wipeId, bn.prefabID) ?? "");
                                }
                                catch { }
                                try
                                {
                                    tracked.TypeClass = entity?.GetType().Name ?? "";
                                }
                                catch { }
                                try
                                {
                                    tracked.MaxHealth = entity?.baseCombat?.maxHealth ?? 0;
                                }
                                catch { }
                                tracked.Extra ??= new();
                                try
                                {
                                    if (entity?.basePlayer != null && entity.basePlayer.metabolism != null)
                                    {
                                        tracked.Extra["stamina"] = entity.basePlayer.metabolism.heartrate;
                                        tracked.Extra["food"] = entity.basePlayer.metabolism.calories;
                                        tracked.Extra["water"] = entity.basePlayer.metabolism.hydration;
                                    }
                                    else if (entity?.worldItem != null && entity.worldItem.item != null)
                                    {
                                        tracked.Extra["itemID"] = entity.worldItem.item.itemid;
                                        tracked.Extra["stackCount"] = entity.worldItem.item.amount;
                                    }
                                    else if (entity?.buildingBlock != null)
                                    {
                                        tracked.Extra["construction"] = entity.buildingBlock.grade;
                                    }
                                    else if (entity?.baseNPC != null)
                                    {
                                        tracked.Extra["npcClass"] = entity.baseNPC.GetType().Name;
                                    }
                                }
                                catch { }
                                if (_entitiesByWipe.TryGetValue(wipeId, out var entities))
                                {
                                    entities[tracked.Id] = tracked;
                                    try { LogDebug($"[SPAWN] Wipe={wipeId} Prefab={bn.prefabID} NETID={bn.uid.Value}"); } catch { }
                                    try { OnEntityCreated?.Invoke(tracked, wipeId); } catch { }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            try { LogDebug($"[Entities] Error: {ex.Message}"); } catch { }
                        }
                        break;
                    case MessageType.Message:
                        break;
                    case MessageType.VoiceData:
                        break;
                    case MessageType.EntityDestroy:
                        try
                        {
                            if (offset + sizeof(ulong) + sizeof(byte) > buffer.Length) { return; }
                            entityId = BinaryPrimitives.ReadUInt64LittleEndian(buffer[offset..]);
                            offset += sizeof(ulong);
                            offset += sizeof(byte);
                            if (_entitiesByWipe.TryGetValue(wipeId, out var entitiesDel))
                            {
                                if (entitiesDel.TryGetValue(entityId, out var entityDel))
                                {
                                    entityDel.isDestroyed = true;
                                }
                            }
                            try { LogDebug($"[DESTROY] Wipe={wipeId} Entity={entityId}"); } catch { }
                            try { OnEntityDestroyed?.Invoke(wipeId, entityId); } catch { }
                        }
                        catch (Exception ex)
                        {
                            try { LogDebug($"[EntityDestroy] Error: {ex.Message}"); } catch { }
                        }
                        break;
                    case MessageType.EntityPosition:
                        try
                        {
                            int minPositionSize = sizeof(ulong) + (sizeof(float) * 7);
                            if (offset + minPositionSize > buffer.Length) return;
                            entityId = BinaryPrimitives.ReadUInt64LittleEndian(buffer[offset..]);
                            offset += sizeof(ulong);
                            float posX = BinaryPrimitives.ReadSingleLittleEndian(buffer[offset..]);
                            offset += sizeof(float);
                            float posY = BinaryPrimitives.ReadSingleLittleEndian(buffer[offset..]);
                            offset += sizeof(float);
                            float posZ = BinaryPrimitives.ReadSingleLittleEndian(buffer[offset..]);
                            offset += sizeof(float);
                            float rotX = BinaryPrimitives.ReadSingleLittleEndian(buffer[offset..]);
                            offset += sizeof(float);
                            float rotY = BinaryPrimitives.ReadSingleLittleEndian(buffer[offset..]);
                            offset += sizeof(float);
                            float rotZ = BinaryPrimitives.ReadSingleLittleEndian(buffer[offset..]);
                            offset += sizeof(float);
                            float time = BinaryPrimitives.ReadSingleLittleEndian(buffer[offset..]);
                            offset += sizeof(float);
                            ulong parentId = 0;
                            if (offset + sizeof(ulong) <= buffer.Length)
                            {
                                parentId = BinaryPrimitives.ReadUInt64LittleEndian(buffer[offset..]);
                            }
                            if (_entitiesByWipe.TryGetValue(wipeId, out var entitiesPos) &&
                                entitiesPos.TryGetValue(entityId, out var entityPos))
                            {
                                entityPos.UpdatePosition(posX, posY, posZ, rotX, rotY, rotZ);
                                try { OnEntityUpdated?.Invoke(entityPos, wipeId); } catch { }
                            }
                        }
                        catch (Exception ex)
                        {
                            try { LogDebug($"[EntityPosition] Error: {ex.Message}"); } catch { }
                        }
                        break;
                    case MessageType.Effect:
                        try
                        {
                            if (offset + sizeof(uint) > buffer.Length) { return; }
                            int effectDataLength = buffer.Length - offset;
                            if (effectDataLength <= 0) { return; }
                            ReadOnlySpan<byte> effectBytes = buffer[offset..];
                            var effect = EffectData.Deserialize(effectBytes.ToArray());
                            if (effect != null && effect.pooledstringid != 0)
                            {
                                string? name = "";
                                try { name = Path.GetFileName(GetStringFromPool(wipeId, effect.pooledstringid) ?? ""); } catch { }
                                ulong effectId = (ulong)effect.pooledstringid;
                                var tracked = new TrackedEffect()
                                {
                                    Id = effectId,
                                    pos = effect.origin,
                                    PrefabName = name
                                };
                                var effects = _effectsByWipe.GetOrAdd(wipeId, _ => new ConcurrentDictionary<ulong, TrackedEffect>());
                                effects[effectId] = tracked;
                                try { LogDebug($"[EFFECT] Wipe={wipeId} Added ID={effectId} Name={name}"); } catch { }
                                try { OnEffectDecoded?.Invoke(effect, wipeId); } catch { }
                                try
                                {
                                    Task.Run(async () =>
                                    {
                                        await Task.Delay(1000);
                                        if (wipeId != null && _effectsByWipe.TryGetValue(wipeId, out var wipeEffects)){wipeEffects.TryRemove(effectId, out _);                                        }
                                    });
                                }
                                catch { }
                            }
                        }
                        catch (Exception ex)
                        {
                            try { LogDebug($"[Effect] Error: {ex.Message}"); } catch { }
                        }
                        break;
                    case MessageType.RPCMessage:
                        try
                        {
                            if (offset + sizeof(ulong) + sizeof(uint) > buffer.Length) { return; }
                            entityId = BinaryPrimitives.ReadUInt64LittleEndian(buffer[offset..]);
                            offset += sizeof(ulong);
                            uint rpcId = BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
                            try { LogDebug($"[RPC] Wipe={wipeId} Entity={entityId} RPC={rpcId}"); } catch { }
                            try { OnRPCReceived?.Invoke(wipeId, entityId, rpcId); } catch { }
                        }
                        catch (Exception ex)
                        {
                            try { LogDebug($"[RPC] Error: {ex.Message}"); } catch { }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                try { LogDebug($"[ProcessBinaryPacket] Error: {ex.Message}"); } catch { }
            }
        }

        private static async Task HandleModels(HttpListenerContext ctx)
        {
            if (ctx.Request.Url == null) { return; }
            if (!ctx.Request.Url.AbsolutePath.StartsWith("/models/", StringComparison.OrdinalIgnoreCase)) { return; }
            string rawFileName = ctx.Request.Url.AbsolutePath["/models/".Length..];
            if (rawFileName.Contains('?')) { rawFileName = rawFileName.Split('?')[0]; }
            string fileName = Path.GetFileName(rawFileName);
            string rootDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models");
            string fullPath = Path.Combine(rootDirectory, fileName);
            string canonicalPath = Path.GetFullPath(fullPath);
            if (!canonicalPath.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 403;
                ctx.Response.Close();
                return;
            }
            string extension = Path.GetExtension(fileName).ToLowerInvariant();
            string contentType = extension switch
            {
                ".glb" => "model/gltf-binary",
                ".gltf" => "model/gltf+json",
                ".json" => "application/json",
                ".bin" => "application/octet-stream",
                _ => "application/octet-stream",
            };
            ;
            try
            {
                byte[] fileBytes = File.ReadAllBytes(fullPath);
                ctx.Response.ContentType = contentType;
                ctx.Response.StatusCode = 200;
                ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
                ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, OPTIONS");
                ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
                ctx.Response.AddHeader("Content-Encoding", "gzip");
                using (var ms = new MemoryStream())
                {
                    using (var gzip = new GZipStream(ms, CompressionMode.Compress, true)) { await gzip.WriteAsync(fileBytes, 0, fileBytes.Length); }
                    byte[] compressedBytes = ms.ToArray();
                    ctx.Response.ContentLength64 = compressedBytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(compressedBytes, 0, compressedBytes.Length);
                }
                ctx.Response.OutputStream.Close();
            }
            catch
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.OutputStream.Close();
            }
        }
        private static void HandleMarker(string? wipeId, long serverTicks)
        {
            if (wipeId == null) return;
        }
        private static async Task HandleIndexPage(HttpListenerContext context)
        {
            var response = context.Response;
            bool isAuthenticated = HasAuth(context, true);
            await SendHtmlResponse(response, 200, HTML.GetIndexHtmlBytes(isAuthenticated));
        }
        private static async Task HandleServersApi(HttpListenerContext context)
        {
            var response = context.Response;
            if (!HasAuth(context)) { return; }
            var serversList = _connectedServers.Values.ToList();
            await SendJsonResponse(response, 200, new { servers = serversList });
        }
        private static ServerInfo? GetServerByWipeId(string? wipeId)
        {
            if (string.IsNullOrEmpty(wipeId)) return null;
            return _connectedServers.TryGetValue(wipeId, out var server) ? server : null;
        }
        private static async Task HandleServerDetailApi(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            if (!HasAuth(context)) { return; }
            string? path = request.Url?.AbsolutePath;
            string? wipeId = path?.Replace("/api/server/", "").Replace("/api/server", "");
            if (string.IsNullOrEmpty(wipeId))
            {
                await SendJsonResponse(response, 400, new { error = "Server ID required" });
                return;
            }
            ServerInfo? server = GetServerByWipeId(wipeId);
            if (server == null)
            {
                await SendJsonResponse(response, 404, new { error = "Server not found" });
                return;
            }
            string serverDataDir = Path.Combine(DataDirectory, wipeId, "Maps");
            var mapFiles = new List<object>();
            var snapshotFiles = new List<object>();
            var stringPoolFiles = new List<object>();
            var manifestFiles = new List<object>();
            try
            {
                if (Directory.Exists(serverDataDir))
                {
                    foreach (var file in Directory.GetFiles(serverDataDir))
                    {
                        var fi = new FileInfo(file);
                        mapFiles.Add(new
                        {
                            name = fi.Name,
                            size = fi.Length,
                            created = fi.CreationTimeUtc,
                            modified = fi.LastWriteTimeUtc
                        });
                    }
                }
                string snapshotDir = Path.Combine(DataDirectory, wipeId, "Snapshots");
                if (Directory.Exists(snapshotDir))
                {
                    var serverSnapshots = Directory.GetFiles(snapshotDir, $"*.sav");
                    foreach (var file in serverSnapshots)
                    {
                        var fi = new FileInfo(file);
                        snapshotFiles.Add(new
                        {
                            name = fi.Name,
                            size = fi.Length,
                            created = fi.CreationTimeUtc,
                            modified = fi.LastWriteTimeUtc
                        });
                    }
                }
                string stringPoolDir = Path.Combine(DataDirectory, wipeId, "StringPools");
                if (Directory.Exists(stringPoolDir))
                {
                    var stringPoolFilesFound = Directory.GetFiles(stringPoolDir, "*.json");
                    foreach (var file in stringPoolFilesFound)
                    {
                        var fi = new FileInfo(file);
                        stringPoolFiles.Add(new
                        {
                            name = fi.Name,
                            size = fi.Length,
                            created = fi.CreationTimeUtc,
                            modified = fi.LastWriteTimeUtc
                        });
                    }
                }
                string manifestDir = Path.Combine(DataDirectory, wipeId, "Manifests");
                if (Directory.Exists(manifestDir))
                {
                    var manifestFilesFound = Directory.GetFiles(manifestDir, $"*.json");
                    foreach (var file in manifestFilesFound)
                    {
                        var fi = new FileInfo(file);
                        manifestFiles.Add(new
                        {
                            name = fi.Name,
                            size = fi.Length,
                            created = fi.CreationTimeUtc,
                            modified = fi.LastWriteTimeUtc
                        });
                    }
                }
            }
            catch (Exception ex) { LogDebug($"[SERVER DETAIL] Error reading files: {ex.Message}"); }
            await SendJsonResponse(response, 200, new
            {
                server = new
                {
                    server.wipeId,
                    server.connectedAt,
                    server.lastActivity,
                    server.packetsReceived,
                    server.bytesReceived,
                },
                files = new
                {
                    maps = mapFiles,
                    snapshots = snapshotFiles,
                    stringPools = stringPoolFiles,
                    manifests = manifestFiles
                },
                mapInfo = LoadMapInfo(wipeId)
            });
        }
        private static async Task HandlePathsApi(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            if (!HasAuth(context)) { return; }
            string? path = request.Url?.AbsolutePath;
            string? wipeId = path?.Replace("/api/paths/", "").Split('/')[0];
            int.TryParse(request.QueryString["page"], out int page);
            if (page < 1) page = 1;
            const int pageSize = 50;
            try
            {
                WorldSerialization? worldSerialization = GetCachedWorldSerialization(wipeId);
                if (worldSerialization == null || worldSerialization.world == null)
                {
                    await SendJsonResponse(response, 404, new { error = "No maps found" });
                    return;
                }
                var allPaths = worldSerialization.world.paths.Select((p, idx) => new { index = idx, p.name, p.spline, p.start, p.end, p.width, nodes = p.nodes?.Count ?? 0 }).ToList();
                var totalPaths = allPaths.Count;
                var pagedPaths = allPaths.Skip((page - 1) * pageSize).Take(pageSize).ToList();
                await SendJsonResponse(response, 200, new { paths = pagedPaths, total = totalPaths, page, pageSize, totalPages = (int)Math.Ceiling((double)totalPaths / pageSize) });
            }
            catch (Exception ex)
            {
                LogDebug($"[PATHS API] Error: {ex.Message}");
                await SendJsonResponse(response, 500, new { error = ex.Message });
            }
        }
        private static async Task HandlePrefabsApi(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            if (!HasAuth(context)) { return; }
            string? path = request.Url?.AbsolutePath;
            string? wipeId = path?.Replace("/api/prefabs/", "").Split('/')[0];
            int.TryParse(request.QueryString["page"], out int page);
            if (page < 1) page = 1;
            string? category = request.QueryString["category"];
            const int pageSize = 100;
            try
            {
                WorldSerialization? worldSerialization = GetCachedWorldSerialization(wipeId);
                if (worldSerialization == null || worldSerialization.world == null)
                {
                    await SendJsonResponse(response, 404, new { error = "No maps found" });
                    return;
                }
                var allPrefabs = worldSerialization.world.prefabs.AsEnumerable();
                if (!string.IsNullOrEmpty(category) && category != "all")
                {
                    allPrefabs = allPrefabs.Where(p => p.category == category);
                }
                var prefabList = allPrefabs
                    .Select((p, idx) => new
                    {
                        index = idx,
                        category = p.category ?? "Unknown",
                        p.id,
                        name = GetStringFromPool(wipeId, p.id) ?? "Unknown",
                        position = $"{p.position.x:F2}, {p.position.y:F2}, {p.position.z:F2}"
                    })
                    .ToList();
                var totalPrefabs = prefabList.Count;
                var pagedPrefabs = prefabList.Skip((page - 1) * pageSize).Take(pageSize).ToList();
                await SendJsonResponse(response, 200, new { prefabs = pagedPrefabs, total = totalPrefabs, page, pageSize, totalPages = (int)Math.Ceiling((double)totalPrefabs / pageSize), filterCategory = category ?? "all" });
            }
            catch (Exception ex)
            {
                LogDebug($"[PREFABS API] Error: {ex.Message}");
                await SendJsonResponse(response, 500, new { error = ex.Message });
            }
        }
        private static async Task HandleMapDataApi(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            if (!HasAuth(context)) { return; }
            string? path = request.Url?.AbsolutePath;
            string[]? parts = path?.Replace("/api/mapdata/", "").Split('/');
            string? wipeId = parts?[0];
            string? layerName = parts?.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            try
            {
                WorldSerialization? worldSerialization = GetCachedWorldSerialization(wipeId);
                if (worldSerialization == null || worldSerialization.world == null)
                {
                    await SendJsonResponse(response, 404, new { error = "No maps found" });
                    return;
                }
                if (!MapLayers.Contains(layerName)) { layerName = EncryptMapDataName(worldSerialization.world.prefabs.Count, layerName); }
                var mapData = worldSerialization.GetMap(layerName);
                if (mapData == null || mapData.data == null)
                {
                    await SendJsonResponse(response, 404, new { error = "Map layer not found" });
                    return;
                }
                string base64Data = Convert.ToBase64String(mapData.data);
                await SendJsonResponse(response, 200, new { name = layerName, size = mapData.data.Length, data = base64Data });
            }
            catch (Exception ex)
            {
                LogDebug($"[MAPDATA API] Error: {ex.Message}");
                await SendJsonResponse(response, 500, new { error = ex.Message });
            }
        }
        private static async Task HandleSavDataApi(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            if (!HasAuth(context)) { return; }
            try
            {
                string wipeId = request.Url!.AbsolutePath.Replace("/api/savdata/", "");
                string? nameFilter = request.QueryString["name"];
                string savDir = Path.Combine(DataDirectory, wipeId, "Snapshots");
                if (!Directory.Exists(savDir))
                {
                    await SendJsonResponse(response, 404, new { error = "Snapshot directory not found" });
                    return;
                }
                string? savFile = Directory
                    .EnumerateFiles(savDir, "*.sav")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();

                if (savFile == null)
                {
                    await SendJsonResponse(response, 404, new { error = "No save files found" });
                    return;
                }
                LogDebug($"[SavAPI] wipe={wipeId} file={Path.GetFileName(savFile)} filter={nameFilter}");
                var entities = ReadSavFile(wipeId, savFile, nameFilter);
                await SendJsonResponse(response, 200, new
                {
                    wipeId,
                    file = Path.GetFileName(savFile),
                    filter = nameFilter,
                    count = entities.Count,
                    entities
                });
            }
            catch (Exception ex)
            {
                LogDebug($"[SAVDATA API] Error: {ex}");
                await SendJsonResponse(response, 500, new { error = ex.Message });
            }
        }
        private static async Task HandleFileDownload(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            if (!HasAuth(context)) { return; }
            string? path = request.Url?.AbsolutePath;
            string[]? parts = path?.Replace("/api/download/", "").Split('/');
            if (parts?.Length < 2)
            {
                response.StatusCode = 400;
                byte[] buffer = Encoding.UTF8.GetBytes("Invalid path");
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                return;
            }
            string? wipeId = parts?[0];
            if (wipeId == null) { return; }
            string? fileType = parts?[1];
            string? filename = parts?.Length > 2 ? Uri.UnescapeDataString(string.Join("/", parts.Skip(2))) : "";
            try
            {
                string? filePath = "";
                if (fileType == "map" && !string.IsNullOrEmpty(filename))
                {
                    filePath = Path.Combine(DataDirectory, wipeId, "Maps", SanitizeFilename(filename));
                }
                else if (fileType == "snapshot" && !string.IsNullOrEmpty(filename))
                {
                    filePath = Path.Combine(DataDirectory, wipeId, "Snapshots", SanitizeFilename(filename));
                }
                else if (fileType == "manifest" && !string.IsNullOrEmpty(filename))
                {
                    filePath = Path.Combine(DataDirectory, wipeId, "Manifests", SanitizeFilename(filename));
                }
                else if (fileType == "stringpool" && !string.IsNullOrEmpty(filename))
                {
                    filePath = Path.Combine(DataDirectory, wipeId, "StringPools", SanitizeFilename(filename));
                }
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    response.StatusCode = 404;
                    byte[] buffer = Encoding.UTF8.GetBytes("File not found");
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    return;
                }
                byte[] fileData = File.ReadAllBytes(filePath);
                response.StatusCode = 200;
                response.ContentType = "application/octet-stream";
                response.AddHeader("Content-Disposition", $"attachment; filename=\"{Path.GetFileName(filePath)}\"");
                response.ContentLength64 = fileData.Length;
                await response.OutputStream.WriteAsync(fileData, 0, fileData.Length);
            }
            catch (Exception ex)
            {
                LogDebug($"[DOWNLOAD] Error: {ex.Message}");
                response.StatusCode = 500;
                byte[] buffer = Encoding.UTF8.GetBytes(ex.Message);
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
        }
        private static async Task Handle3DEntitiesData(HttpListenerContext context)
        {
            if (!HasAuth(context)) { return; }
            var request = context.Request;
            var response = context.Response;
            string? wipeId = request.Url?.AbsolutePath.Replace("/3dmap/entities/", "").Replace("/3dmap/entities/", "");
            if (string.IsNullOrEmpty(wipeId))
            {
                await SendHtmlResponse(response, 302, "", new Dictionary<string, string> { { "Location", "/" } });
                return;
            }
            string mapsDir = Path.Combine(DataDirectory, wipeId, "Maps");
            var mapFiles = Directory.GetFiles(mapsDir, "*.map");
            if (mapFiles.Length <= 0)
            {
                await SendHtmlResponse(response, 302, "", new Dictionary<string, string> { { "Location", "/" } });
                return;
            }
            try
            {
                byte[] jsonBytes = GetEntitiesJson(wipeId);
                response.AddHeader("Content-Encoding", "gzip");
                response.ContentType = "application/json";
                using (var ms = new MemoryStream())
                {
                    using (var gzip = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                    {
                        gzip.Write(jsonBytes, 0, jsonBytes.Length);
                    }
                    byte[] compressedBytes = ms.ToArray();
                    response.ContentLength64 = compressedBytes.Length;
                    await response.OutputStream.WriteAsync(compressedBytes, 0, compressedBytes.Length);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"[3DMAP] Error: {ex}");
                response.StatusCode = 500;
                byte[] buffer = Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message.Replace("\"", "'")}\"}}");
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
        }
        private static async Task Handle3DUpdateData(HttpListenerContext context)
        {
            if (!HasAuth(context)) { return; }
            var request = context.Request;
            var response = context.Response;
            string? wipeId = request.Url?.AbsolutePath.Replace("/3dmap/update/", "").Replace("/3dmap/update/", "");
            if (string.IsNullOrEmpty(wipeId))
            {
                await SendHtmlResponse(response, 302, "", new Dictionary<string, string> { { "Location", "/" } });
                return;
            }
            string mapsDir = Path.Combine(DataDirectory, wipeId, "Maps");
            var mapFiles = Directory.GetFiles(mapsDir, "*.map");
            if (mapFiles.Length <= 0)
            {
                await SendHtmlResponse(response, 302, "", new Dictionary<string, string> { { "Location", "/" } });
                return;
            }
            float? cameraX = null, cameraY = null, cameraZ = null;
            bool unlimitedView = false;
            var query = request.QueryString;
            if (query != null)
            {
                string? cx = query["cx"];
                string? cy = query["cy"];
                string? cz = query["cz"];
                string? unlimited = query["unlimited"];
                if (!string.IsNullOrEmpty(cx) && float.TryParse(cx, out float parsedX)) cameraX = parsedX;
                if (!string.IsNullOrEmpty(cy) && float.TryParse(cy, out float parsedY)) cameraY = parsedY;
                if (!string.IsNullOrEmpty(cz) && float.TryParse(cz, out float parsedCz)) cameraZ = -parsedCz;
                if (!string.IsNullOrEmpty(unlimited) && bool.TryParse(unlimited, out bool parsedUnlimited)) unlimitedView = parsedUnlimited;
            }
            try
            {
                byte[] jsonBytes = GetEntitiesJson(wipeId, cameraX, cameraY, cameraZ, unlimitedView);
                response.AddHeader("Content-Encoding", "gzip");
                response.ContentType = "application/json";
                using (var ms = new MemoryStream())
                {
                    using (var gzip = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                    {
                        gzip.Write(jsonBytes, 0, jsonBytes.Length);
                    }
                    byte[] compressedBytes = ms.ToArray();
                    response.ContentLength64 = compressedBytes.Length;
                    await response.OutputStream.WriteAsync(compressedBytes, 0, compressedBytes.Length);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"[3DMAP] Error: {ex}");
                response.StatusCode = 500;
                byte[] buffer = Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message.Replace("\"", "'")}\"}}");
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
        }
        private static JsonSerializerOptions GetOptions()
        {
            return new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
        }
        private static async Task Handle3DMapData(HttpListenerContext context, JsonSerializerOptions options)
        {
            if (!HasAuth(context)) { return; }
            var request = context.Request;
            var response = context.Response;
            string? wipeId = request.Url?.AbsolutePath.Replace("/3dmap/data/", "").Replace("/3dmap/data/", "");
            if (string.IsNullOrEmpty(wipeId))
            {
                await SendHtmlResponse(response, 302, "", new Dictionary<string, string> { { "Location", "/" } });
                return;
            }
            if (_mapDataCache.TryGetValue(wipeId, out var cached))
            {
                response.AddHeader("Content-Encoding", "gzip");
                SafeWrite(response, cached.Data, "application/json");
                return;
            }
            string mapsDir = Path.Combine(DataDirectory, wipeId, "Maps");
            var mapFiles = Directory.GetFiles(mapsDir, "*.map");
            if (mapFiles.Length <= 0)
            {
                await SendHtmlResponse(response, 302, "", new Dictionary<string, string> { { "Location", "/" } });
                return;
            }
            try
            {
                string? latestMapFile = Directory.EnumerateFiles(mapsDir, "*.map").FirstOrDefault();
                if (latestMapFile is null) { return; }
                List<object[]> mapRoads = new();
                List<object[]> mapRails = new();
                List<object[]> mapRivers = new();
                List<object> prefabs = new();
                byte[]? heightBytes = null;
                byte[]? splatBytes = null;
                int heightRes = 0;
                int splatRes = 0;
                int worldsize = 4500;
                try
                {
                    WorldSerialization? worldSerialization = GetCachedWorldSerialization(wipeId);
                    if (worldSerialization == null || worldSerialization.world == null)
                    {
                        string errmsg = $"[3DMAP] Error: Can't Load World File {wipeId}";
                        LogDebug(errmsg);
                        response.StatusCode = 500;
                        byte[] buffer = Encoding.UTF8.GetBytes($"{{\"error\": \"{errmsg}\"}}");
                        response.ContentLength64 = buffer.Length;
                        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                        return;
                    }
                    worldsize = (int)worldSerialization.world.size;
                    if (worldsize > 8000 || worldsize < 300) { worldsize = 8000; }
                    if (worldSerialization.world.paths != null)
                    {
                        foreach (var path in worldSerialization.world.paths)
                        {
                            if (path != null && path.nodes != null && path.nodes.Count > 0)
                            {
                                var points = path.nodes.Select(p => new object[] { p.x, p.y, p.z }).ToArray();
                                if (path.name != null && (path.name.Contains("road") || path.name.Contains("Road")))
                                {
                                    mapRoads.Add(points);
                                }
                                else if (path.name != null && (path.name.Contains("rail") || path.name.Contains("Rail")))
                                {
                                    mapRails.Add(points);
                                }
                                else if (path.name != null && (path.name.Contains("river") || path.name.Contains("River")))
                                {
                                    mapRivers.Add(points);
                                }
                            }
                        }
                    }
                    if (worldSerialization.world.prefabs != null)
                    {
                        foreach (var pd in worldSerialization.world.prefabs)
                        {
                            string? name = GetStringFromPool(wipeId, pd.id);
                            if (string.IsNullOrEmpty(name)) { continue; }
                            if (name.Contains("rock_formation_e") || name.Contains("rock_formation_a"))
                            {
                                prefabs.Add(new
                                {
                                    Name = "godrock",
                                    position = new VectorData(pd.position.x, pd.position.y, pd.position.z),
                                    rotation = new VectorData(pd.rotation.x, pd.rotation.y, pd.rotation.z),
                                    scale = new VectorData(pd.scale.x, pd.scale.y, pd.scale.z),
                                });
                                continue;
                            }
                            else if (name.Contains("lava"))
                            {
                                prefabs.Add(new
                                {
                                    Name = "lava",
                                    position = new VectorData(pd.position.x, pd.position.y, pd.position.z),
                                    rotation = new VectorData(pd.rotation.x, pd.rotation.y, pd.rotation.z),
                                    scale = new VectorData(pd.scale.x, pd.scale.y, pd.scale.z),
                                });
                                continue;
                            }
                            else if (name.Contains("modding/cubes"))
                            {
                                prefabs.Add(new
                                {
                                    Name = "cube",
                                    position = new VectorData(pd.position.x, pd.position.y, pd.position.z),
                                    rotation = new VectorData(pd.rotation.x, pd.rotation.y, pd.rotation.z),
                                    scale = new VectorData(pd.scale.x, pd.scale.y, pd.scale.z),
                                });
                                continue;
                            }
                            else if (name.Contains("monument_marker"))
                            {
                                prefabs.Add(new
                                {
                                    Name = pd.category,
                                    position = new VectorData(pd.position.x, pd.position.y, pd.position.z),
                                    rotation = new VectorData(pd.rotation.x, pd.rotation.y, pd.rotation.z),
                                    scale = new VectorData(pd.scale.x, pd.scale.y, pd.scale.z),
                                });
                                continue;
                            }
                            else if (name.Contains("monument") || name.Contains("unique_environment") || name.Contains("tunnel-entrance") || name.Contains("platform") || name.Contains("power substations") || name.Contains("iceberg") || name.Contains("ice_lakes") || name.Contains("assets/bundled/prefabs/static") || name.Contains("modding/lootables") || name.Contains("modding/volumes_and_triggers") || name.Contains("io/electric"))
                            {
                                Vector3 postion = pd.position;
                                if (postion.y <= -499) { postion.y = 0; }
                                name = Path.GetFileNameWithoutExtension(name);
                                if (name.Contains("desert_military_base")) { name = "desert_military_base"; }
                                else if (name.Contains("mining_quarry")) { name = "mining_quarry"; }
                                else if (name.Contains("powerlineplatform")) { name = "powerlineplatform"; }
                                else if (name.Contains("entrance_bunker")) { name = "entrance_bunker"; }
                                prefabs.Add(new
                                {
                                    Name = name,
                                    position = new VectorData(pd.position.x, pd.position.y, pd.position.z),
                                    rotation = new VectorData(pd.rotation.x, pd.rotation.y, pd.rotation.z),
                                    scale = new VectorData(pd.scale.x, pd.scale.y, pd.scale.z),
                                });
                            }
                        }
                    }
                    var mapData = worldSerialization.GetMap("height");
                    if (mapData?.data != null)
                    {
                        heightBytes = DownsampleMap(mapData.data, 2);
                        if (heightBytes == null) { return; }
                        heightRes = (int)Math.Sqrt(heightBytes.Length / 2);
                    }
                    mapData = worldSerialization.GetMap("splat");
                    if (mapData?.data != null)
                    {
                        splatBytes = DownsampleMap(mapData.data, 8);
                        if (splatBytes == null) { return; }
                        splatRes = (int)Math.Sqrt(splatBytes.Length / 8);
                    }
                }
                catch (Exception ex) { LogDebug($"[3DMAP] Error loading map data: {ex.Message}"); }
                var terrainData = new
                {
                    worldSize = worldsize,
                    heightMapResolution = heightRes,
                    splatMapResolution = splatRes,
                    heightmap = heightBytes != null ? CompressAndEncode(heightBytes) : "",
                    splatmap = splatBytes != null ? CompressAndEncode(splatBytes) : "",
                    splatColors = new
                    {
                        dirt = new[] { 0.6f, 0.479f, 0.33f },
                        snow = new[] { 0.862f, 0.929f, 0.941f },
                        sand = new[] { 0.7f, 0.659f, 0.527f },
                        rock = new[] { 0.4f, 0.393f, 0.375f },
                        grass = new[] { 0.354f, 0.37f, 0.203f },
                        forest = new[] { 0.248f, 0.3f, 0.07f },
                        stones = new[] { 0.137f, 0.278f, 0.276f },
                        gravel = new[] { 0.25f, 0.243f, 0.22f }
                    },
                    roads = mapRoads,
                    rail = mapRails,
                    prefabs,
                    river = mapRivers
                };
                response.AddHeader("Content-Encoding", "gzip");
                byte[] compressedBytes;
                using (var ms = new MemoryStream())
                {
                    using (var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        JsonSerializer.Serialize(gzip, terrainData, options);
                    }
                    compressedBytes = ms.ToArray();
                }
                _mapDataCache[wipeId] = (compressedBytes, DateTime.UtcNow.Ticks);
                SafeWrite(response, compressedBytes, "application/json");
            }
            catch (Exception ex)
            {
                LogDebug($"[3DMAP] Error: {ex.Message}");
                response.StatusCode = 500;
                byte[] buffer = Encoding.UTF8.GetBytes($"{{\"error\": \"{ex.Message}\"}}");
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
        }
        private static async Task Serve3DViewer(HttpListenerContext context)
        {
            if (!HasAuth(context)) { return; }
            var request = context.Request;
            var response = context.Response;
            string? wipeId = request.Url?.AbsolutePath.Replace("/3dviewer/", "").Replace("/3dviewer/", "");
            if (string.IsNullOrEmpty(wipeId))
            {
                await SendHtmlResponse(response, 302, "", new Dictionary<string, string> { { "Location", "/" } });
                return;
            }
            await SendHtmlResponse(context.Response, 200, HTML.Get3DViewerHtmlBytes(wipeId));
        }
        private static async Task HandleServerDetailPage(HttpListenerContext context)
        {
            if (!HasAuth(context)) { return; }
            var request = context.Request;
            var response = context.Response;
            bool isAuthenticated = HasAuth(context, true);
            if (!isAuthenticated)
            {
                await SendHtmlResponse(response, 200, HTML.GetIndexHtmlBytes(isAuthenticated));
                return;
            }
            string? path = request.Url?.AbsolutePath;
            string? wipeId = path?.Replace("/server/", "").Replace("/server", "");
            if (string.IsNullOrEmpty(wipeId))
            {
                await SendHtmlResponse(response, 302, "", new Dictionary<string, string> { { "Location", "/" } });
                return;
            }
            await SendHtmlResponse(response, 200, HTML.GetServerDetailHtmlBytes(wipeId, isAuthenticated));
        }
        public static string? DecryptMapDataName(int PreFabCount, string? EncryptedData)
        {
            try
            {
                if (EncryptedData == null) { return string.Empty; }
                using (var aes = Aes.Create())
                {
#pragma warning disable SYSLIB0041
                    var rfc2898DeriveBytes = new Rfc2898DeriveBytes(PreFabCount.ToString(), [73, 118, 97, 110, 32, 77, 101, 100, 118, 101, 100, 101, 118]);
#pragma warning restore SYSLIB0041
                    aes.Key = rfc2898DeriveBytes.GetBytes(32);
                    aes.IV = rfc2898DeriveBytes.GetBytes(16);
                    byte[] cipherText = Convert.FromBase64String(EncryptedData);
                    using (var memoryStream = new MemoryStream(cipherText))
                    {
                        using (var cryptoStream = new CryptoStream(memoryStream, aes.CreateDecryptor(), CryptoStreamMode.Read))
                        {
                            using (var reader = new StreamReader(cryptoStream, Encoding.Unicode)) { return reader.ReadToEnd(); }
                        }
                    }
                }
            }
            catch { return EncryptedData; }
        }
        public static string EncryptMapDataName(int PreFabCount, string DataName)
        {
            try
            {
                using (var aes = Aes.Create())
                {
#pragma warning disable SYSLIB0041
                    var rfc2898DeriveBytes = new Rfc2898DeriveBytes(PreFabCount.ToString(), [73, 118, 97, 110, 32, 77, 101, 100, 118, 101, 100, 101, 118]);
#pragma warning restore SYSLIB0041
                    aes.Key = rfc2898DeriveBytes.GetBytes(32);
                    aes.IV = rfc2898DeriveBytes.GetBytes(16);
                    using (var memoryStream = new MemoryStream())
                    {
                        using (var cryptoStream = new CryptoStream(memoryStream, aes.CreateEncryptor(), CryptoStreamMode.Write))
                        {
                            var D = Encoding.Unicode.GetBytes(DataName);
                            cryptoStream.Write(D, 0, D.Length);
                            cryptoStream.Close();
                        }
                        return Convert.ToBase64String(memoryStream.ToArray());
                    }
                }
            }
            catch { }
            return DataName;
        }
        private static async Task HandleLoginApi(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            string body;
            using (var reader = new StreamReader(request.InputStream)) { body = await reader.ReadToEndAsync(); }
            string? password = null;
            if (body.Contains("password="))
            {
                int idx = body.IndexOf("password=") + 9;
                int end = body.IndexOf('&', idx);
                if (end < 0) end = body.Length;
                password = Uri.UnescapeDataString(body.AsSpan(idx, end - idx));
            }
            bool success = password == _authToken;
            if (success)
            {
                Cookie authCookie = new Cookie("auth", password);
                authCookie.Expires = DateTime.Now.AddDays(7);
                authCookie.Path = "/";
                response.Cookies.Add(authCookie);
            }
            await SendJsonResponse(response, success ? 200 : 401, new { success });
        }
        private static async Task SendHtmlResponse(HttpListenerResponse response, int statusCode, string html, Dictionary<string, string>? additionalHeaders = null)
        {
            response.StatusCode = statusCode;
            response.ContentType = "text/html; charset=utf-8";
            if (additionalHeaders != null)
            {
                foreach (var header in additionalHeaders)
                {
                    if (header.Key == "Location") { response.RedirectLocation = header.Value; }
                    else { response.Headers.Add(header.Key, header.Value); }
                }
            }
            byte[] buffer = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }
        private static async Task SendHtmlResponse(HttpListenerResponse response, int statusCode, byte[] html, Dictionary<string, string>? additionalHeaders = null)
        {
            response.StatusCode = statusCode;
            response.ContentType = "text/html; charset=utf-8";
            if (additionalHeaders != null)
            {
                foreach (var header in additionalHeaders)
                {
                    if (header.Key == "Location") { response.RedirectLocation = header.Value; }
                    else { response.Headers.Add(header.Key, header.Value); }
                }
            }
            response.ContentLength64 = html.Length;
            await response.OutputStream.WriteAsync(html, 0, html.Length);
        }
        private static async Task HandleStringPoolUpload(HttpListenerContext context, string? wipeId)
        {
            var request = context.Request;
            var response = context.Response;
            string json;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding, leaveOpen: true))
            {
                json = await reader.ReadToEndAsync();
            }
            try
            {
                var stringPool = SimpleJsonParser.ParseUintStringDict(json);
                LogDebug($"[StringPool] Received {stringPool.Count} string entries");
                OnStringPoolReceived(wipeId, stringPool);
                MarkServerFileReceived(wipeId, "stringpool");
                await SendJsonResponse(response, 200, new
                {
                    success = true,
                    count = stringPool.Count
                });
            }
            catch (Exception ex)
            {
                LogDebug($"[StringPool] Parse error: {ex.Message}");
                await SendJsonResponse(response, 400, new
                {
                    error = "Invalid JSON format"
                });
            }
        }
        private static async Task HandleManifestUpload(HttpListenerContext context, string? wipeId)
        {
            var request = context.Request;
            var response = context.Response;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                string json = await reader.ReadToEndAsync();
                LogDebug($"[Manifest] wipeId={wipeId}, size={json.Length} bytes");
                try
                {
                    var manifest = SimpleJsonParser.ParseUintStringDict(json);
                    LogDebug($"[Manifest] Received {manifest.Count} manifest entries");
                    OnManifestReceived(wipeId, manifest);
                    MarkServerFileReceived(wipeId, "manifest");
                    await SendJsonResponse(response, 200, new { success = true, count = manifest.Count });
                }
                catch (Exception ex)
                {
                    LogDebug($"[Manifest] Parse error: {ex.Message}");
                    await SendJsonResponse(response, 400, new { error = "Invalid JSON format" });
                }
            }
        }
        private static async Task HandleSnapshotUpload(HttpListenerContext context, string? wipeId)
        {
            var request = context.Request;
            var response = context.Response;
            try
            {
                using (var memoryStream = new MemoryStream())
                {
                    await request.InputStream.CopyToAsync(memoryStream);
                    byte[] fileData = memoryStream.ToArray();
                    LogDebug($"[Snapshot] wipeId={wipeId}, size={fileData.Length} bytes");
                    OnSnapshotReceived(wipeId, fileData);
                    MarkServerFileReceived(wipeId, "snapshot");
                    await SendJsonResponse(response, 200, new { success = true, size = fileData.Length });
                }
            }
            catch (Exception ex)
            {
                LogDebug($"[Snapshot] Error: {ex.Message}");
                await SendJsonResponse(response, 400, new { error = ex.Message });
            }
        }
        private static async Task HandleMapSnapshotUpload(HttpListenerContext context, string? wipeId)
        {
            var request = context.Request;
            var response = context.Response;
            try
            {
                var files = await ParseMultipartFormData(request);
                LogDebug($"[MapSnapshot] wipeId={wipeId}, files={files.Count}");
                foreach (var file in files)
                {
                    OnMapFileReceived(wipeId, file.Key, file.Value);
                    MarkServerFileReceived(wipeId, "map");
                }
                await SendJsonResponse(response, 200, new { success = true, files = files.Keys.ToList() });
            }
            catch (Exception ex)
            {
                LogDebug($"[MapSnapshot] Error: {ex.Message}");
                await SendJsonResponse(response, 400, new { error = ex.Message });
            }
        }
        private static MapInfo? LoadMapInfo(string wipeId)
        {
            var mapInfo = new MapInfo();
            try
            {
                WorldSerialization? worldSerialization = GetCachedWorldSerialization(wipeId);
                if (worldSerialization == null) { return null; }
                var topology = worldSerialization.GetMap("topology")?.data;
                var splat = worldSerialization.GetMap("splat")?.data;
                if (topology == null || splat == null) { return null; }
                int[] Topology = new int[topology.Length];
                Buffer.BlockCopy(topology, 0, Topology, 0, topology.Length);
                MapRender? mapRender = new MapRender(splat, Topology);
                mapInfo.png = CompressAndEncode(mapRender.Render());
                var world = worldSerialization.world;
                if (world == null) { return null; }
                mapInfo.worldsize = (int)world.size;
                if (mapInfo.worldsize > 8000 || mapInfo.worldsize < 300) mapInfo.worldsize = 8000;
                mapInfo.mapCount = world.maps.Count;
                mapInfo.prefabCount = world.prefabs.Count;
                mapInfo.pathCount = world.paths.Count;
                mapInfo.timestamp = worldSerialization.Timestamp;
                foreach (var map in world.maps)
                {
                    mapInfo.mapNames.Add(MapLayers.Contains(map.name) ? map.name ?? "Unnamed" : DecryptMapDataName(mapInfo.prefabCount, map.name) ?? "Unnamed");
                }
                var categories = world.prefabs.GroupBy(p => string.IsNullOrEmpty(p.category) ? "Unknown" : p.category).ToDictionary(g => g.Key, g => g.Count());
                mapInfo.prefabCategoryCounts = categories;
                mapInfo.prefabCategories = [.. categories.Keys.OrderBy(k => k)];
                foreach (var path in world.paths)
                {
                    if (!string.IsNullOrEmpty(path.name))
                        mapInfo.pathNames.Add(path.name);
                }
                var monuments = worldSerialization.GetCustomMonuments().ToList();
                mapInfo.customMonumentCount = monuments.Count;
                foreach (var monument in monuments)
                {
                    mapInfo.customMonuments.Add(new
                    {
                        name = monument.name ?? "Unnamed",
                        size = monument.data?.Length ?? 0
                    });
                }
                LogDebug($"[MAP INFO] Loaded - Maps: {mapInfo.mapCount}, Prefabs: {mapInfo.prefabCount}, Paths: {mapInfo.pathCount}, Monuments: {mapInfo.customMonumentCount}");
            }
            catch (Exception ex) { LogDebug($"[MAP INFO] Error: {ex.Message}"); }
            return mapInfo;
        }
        #endregion
        #region Functions
        public static void DownloadAndUnzipModels()
        {
            if (Directory.Exists("Models")) { return; }
            string url = "https://github.com/bmgjet/MapGenny/raw/refs/heads/main/Models.zip";
            string rootPath = AppDomain.CurrentDomain.BaseDirectory;
            string zipPath = Path.Combine(rootPath, "Models.zip");
            using var http = new HttpClient();
            var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            File.WriteAllBytes(zipPath, bytes);
            ZipFile.ExtractToDirectory(zipPath, rootPath, overwriteFiles: true);
            File.Delete(zipPath);
        }
        private static void GetOrCreateWipe(string wipeId)
        {
            _entitiesByWipe.GetOrAdd(wipeId, static _ => new ConcurrentDictionary<ulong, TrackedEntity>());
        }
        private static bool HasAuth(HttpListenerContext context, bool index = false)
        {
            string? authCookie = null;
            CookieCollection cookies = context.Request.Cookies;
            foreach (Cookie cookie in cookies)
            {
                if (cookie.Name == "auth")
                {
                    authCookie = cookie.Value;
                    break;
                }
            }
            if (string.IsNullOrEmpty(authCookie) || authCookie != _authToken)
            {
                if (!index)
                {
                    using var _ = SendJsonResponse(context.Response, 401, new { error = "Unauthorized" });
                }
                return false;
            }
            return true;
        }
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };
        private static async Task SendJsonResponse(HttpListenerResponse response, int statusCode, object data)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(data, _jsonOptions);
            response.ContentLength64 = jsonBytes.Length;
            await response.OutputStream.WriteAsync(jsonBytes, 0, jsonBytes.Length);
        }
        private static void PrintStatistics()
        {
            lock (_consoleLock)
            {
                DateTime now = DateTime.UtcNow;
                int row = 0;
                MoveCursor(row++);
                ClearLine();
                Console.Write($"[STATS] Uptime: {GetUptimeSeconds():F0}s | WebSockets: {_webSocketClients.Count} | Packets: {Interlocked.Read(ref _totalPacketsReceived)}");
                foreach (var server in _connectedServers.Values)
                {
                    MoveCursor(row++);
                    ClearLine();
                    string connectedStr = FormatReadableTime(now - server.connectedAt);
                    string activeStr = FormatReadableTime(now - server.lastActivity);
                    Console.Write($"[Server] ID: {server.wipeId} | Up: {connectedStr} | Active: {activeStr} | Data: {FormatBytes(server.bytesReceived)}");
                }
                for (int i = row; i < _headerHeight; i++)
                {
                    MoveCursor(i);
                    ClearLine();
                }
                _headerHeight = row + 2;
                MoveCursor(_headerHeight);
            }
        }
        public static void LogDebug(string message)
        {
#if DEBUG
            lock (_consoleLock)
            {
                MoveCursor(_headerHeight);
                ClearLine();
                string msg = $"[{DateTime.Now:HH:mm:ss}] {message}";
                Console.WriteLine(msg);
                try { File.AppendAllText(Path.Combine(DataDirectory, "log.txt"), $"{msg}{System.Environment.NewLine}"); }
                catch { }
            }
#endif
        }
        private static void ClearLine()
        {
            Console.Write("\x1b[2K");
        }
        private static void MoveCursor(int row, int col = 0)
        {
            Console.Write($"\x1b[{row + 1};{col + 1}H");
        }
        private static string FormatReadableTime(TimeSpan ts)
        {
            if (ts.TotalDays >= 1) { return string.Format("{0}d {1}h", (int)ts.TotalDays, ts.Hours); }
            if (ts.TotalHours >= 1) { return string.Format("{0}h {1}m", (int)ts.TotalHours, ts.Minutes); }
            if (ts.TotalMinutes >= 1) { return string.Format("{0}m {1}s", (int)ts.TotalMinutes, ts.Seconds); }
            return string.Format("{0}s", ts.Seconds);
        }
        public static string FormatBytes(long bytes)
        {
            string[] Suffix = { "B", "KB", "MB", "GB", "TB" };
            double dblSByte = bytes;
            int i = 0;
            while (dblSByte >= 1024 && i < Suffix.Length - 1)
            {
                i++;
                dblSByte /= 1024;
            }
            return string.Format("{0:0.#} {1}", dblSByte, Suffix[i]);
        }
        private static double GetUptimeSeconds() { return (Stopwatch.GetTimestamp() - _startTime) / (double)Stopwatch.Frequency; }
        private static void StopAll()
        {
            foreach (var kvp in _webSocketClients)
            {
                var client = kvp.Key;
                try
                {
                    client.Socket?.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Server shutdown", CancellationToken.None).Wait(1000);
                }
                catch { }
            }
            _webSocketClients.Clear();
            _httpListener.Stop();
            _httpListener.Close();
        }
        private static void CreateDirectories(string wipeid)
        {
            if (Directory.Exists(Path.Combine(DataDirectory, wipeid))) { return; }
            try
            {
                Directory.CreateDirectory(Path.Combine(DataDirectory, wipeid, "Snapshots"));
                Directory.CreateDirectory(Path.Combine(DataDirectory, wipeid, "Maps"));
                Directory.CreateDirectory(Path.Combine(DataDirectory, wipeid, "StringPools"));
                Directory.CreateDirectory(Path.Combine(DataDirectory, wipeid, "Manifests"));
                Directory.CreateDirectory(Path.Combine(DataDirectory, wipeid, "Packets"));
            }
            catch (Exception ex) { LogDebug($"[STORAGE] Warning: Could not create directories: {ex.Message}"); }
        }
        private static void ParseArguments(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--url" && i + 1 < args.Length)
                {
                    _baseUrl = args[i + 1];
                    i++;
                }
                if (args[i] == "--token" && i + 1 < args.Length)
                {
                    _authToken = args[i + 1];
                    i++;
                }
            }
        }
        private static void TrackServerActivity(string? wipeId)
        {
            if (string.IsNullOrEmpty(wipeId)) return;
            GetOrCreateWipe(wipeId);
            var server = _connectedServers.GetOrAdd(wipeId, id => new ServerInfo
            {
                wipeId = id,
                connectedAt = DateTime.UtcNow,
                lastActivity = DateTime.UtcNow
            });
            server.lastActivity = DateTime.UtcNow;
        }
        private static void UpdateServerStats(string? wipeId, int bytesReceived)
        {
            if (string.IsNullOrEmpty(wipeId)) return;
            var server = GetServerByWipeId(wipeId);
            if (server != null)
            {
                server.packetsReceived++;
                server.bytesReceived += bytesReceived;
                server.lastActivity = DateTime.UtcNow;
            }
        }
        private static void MarkServerFileReceived(string? wipeId, string fileType)
        {
            if (string.IsNullOrEmpty(wipeId)) return;
            var server = GetServerByWipeId(wipeId);
            if (server != null)
            {
                if (!server.receivedFiles.Contains(fileType))
                    server.receivedFiles.Add(fileType);
                server.lastActivity = DateTime.UtcNow;
            }
        }
        private static string? GetStringFromPool(string? wipeId, uint id)
        {
            if (_globalStringPool.TryGetValue(id, out var global))
                return global;
            if (wipeId != null && _wipeStringPools.TryGetValue(wipeId, out var pool))
                if (pool.TryGetValue(id, out var value))
                    return value;
            LoadWipeStringToGlobalPool(wipeId);
            _globalStringPool.TryGetValue(id, out var result);
            return result;
        }
        private static void LoadWipeStringToGlobalPool(string? wipeId)
        {
            if (wipeId == null) return;
            string stringPoolsDir = Path.Combine(DataDirectory, wipeId, "StringPools");
            if (!Directory.Exists(stringPoolsDir)) return;
            var latestFile = Directory.EnumerateFiles(stringPoolsDir, "*.json").FirstOrDefault();
            if (latestFile == null) { return; }
            try
            {
                foreach (string line in File.ReadAllLines(latestFile))
                {
                    int colonIndex = line.IndexOf(':');
                    if (colonIndex <= 0) { continue; }
                    string keyPart = line[..colonIndex].Trim(' ', '"');
                    string valuePart = line[(colonIndex + 1)..].Trim(' ', '"', ',', '\r', '\n');
                    if (uint.TryParse(keyPart, out uint parsedId) && !string.IsNullOrEmpty(valuePart))
                    {
                        if (!_globalStringPool.ContainsKey(parsedId)) { _globalStringPool[parsedId] = valuePart; }
                    }
                }
            }
            catch (Exception ex) { LogDebug($"[StringPool] Error loading {latestFile}: {ex.Message}"); }
        }
        public static string CompressAndEncode(byte[]? data)
        {
            if (data == null || data.Length == 0) { return string.Empty; }
            using (var outputStream = new MemoryStream())
            {
                using (var gZipStream = new GZipStream(outputStream, System.IO.Compression.CompressionLevel.Optimal, true)) { gZipStream.Write(data, 0, data.Length); gZipStream.Flush(); }
                return Convert.ToBase64String(outputStream.GetBuffer(), 0, (int)outputStream.Length);
            }
        }
        private static byte[]? DownsampleMap(byte[] data, int stepSize)
        {
            if (data == null || data.Length == 0) return null;
            int srcRes = (int)Math.Sqrt(data.Length / stepSize);
            if (srcRes <= 2049) return data;
            int dstRes = ((srcRes - 1) / 2) + 1;
            if (dstRes < 512) dstRes = 512;
            int dstSize = dstRes * dstRes * stepSize;
            byte[] result = new byte[dstSize];
            float ratio = (float)(srcRes - 1) / (dstRes - 1);
            for (int y = 0; y < dstRes; y++)
            {
                for (int x = 0; x < dstRes; x++)
                {
                    int srcX = (int)(x * ratio);
                    int srcY = (int)(y * ratio);
                    int srcIdx = (srcY * srcRes + srcX) * stepSize;
                    int dstIdx = (y * dstRes + x) * stepSize;
                    Array.Copy(data, srcIdx, result, dstIdx, stepSize);
                }
            }
            return result;
        }
        private static string? ExtractFormName(string headers)
        {
            int nameIndex = headers.IndexOf("name=\"");
            if (nameIndex >= 0)
            {
                int start = nameIndex + 6;
                int end = headers.IndexOf("\"", start);
                if (end > start) { return headers[start..end]; }
            }
            nameIndex = headers.IndexOf("name=", StringComparison.OrdinalIgnoreCase);
            if (nameIndex >= 0)
            {
                int start = nameIndex + 5;
                while (start < headers.Length && (headers[start] == ' ' || headers[start] == '\t')) { start++; }
                if (start >= headers.Length) { return null; }
                int end = start;
                while (end < headers.Length)
                {
                    char c = headers[end];
                    if (c == ';' || c == '\r' || c == '\n') { break; }
                    end++;
                }
                while (end > start && (headers[end - 1] == ' ' || headers[end - 1] == '\t')) { end--; }
                if (end > start) { return headers[start..end]; }
            }
            return null;
        }
        private static string? ExtractOriginalFilename(string headers)
        {
            int nameIndex = headers.IndexOf("filename=", StringComparison.OrdinalIgnoreCase);
            if (nameIndex >= 0)
            {
                int start = nameIndex + 9;
                while (start < headers.Length && (headers[start] == ' ' || headers[start] == '\t')) { start++; }
                if (start >= headers.Length) { return null; }
                int end = start;
                while (end < headers.Length)
                {
                    char c = headers[end];
                    if (c == ';' || c == '\r' || c == '\n') { break; }
                    end++;
                }
                while (end > start && (headers[end - 1] == ' ' || headers[end - 1] == '\t')) { end--; }
                if (end > start) { return headers[start..end]; }
            }
            return null;
        }
        private static int FindBytes(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, int startPos)
        {
            if (needle.Length == 0) return startPos;
            if (startPos < 0) startPos = 0;
            if (startPos >= haystack.Length) return -1;
            var searchSpace = haystack[startPos..];
            int idx = searchSpace.IndexOf(needle);
            return idx >= 0 ? idx + startPos : -1;
        }
        private static void SaveMapFileToDirectory(string wipeId, string filename, byte[] data)
        {
            try
            {
                string mapDir = Path.Combine(DataDirectory, wipeId, "Maps");
                Directory.CreateDirectory(mapDir);
                string filepath = Path.Combine(mapDir, SanitizeFilename(filename));
                lock (FileWriteLock) { File.WriteAllBytes(filepath, data); }
                LogDebug($"[STORAGE] Map file saved: {wipeId}");
            }
            catch (Exception ex) { LogDebug($"[STORAGE] Failed to save Map file: {ex.Message}"); }
        }
        private static List<TrackedEntity> ReadSavFileEnts(string wipeId, string filePath)
        {
            var result = new List<TrackedEntity>();
            using var fs = File.OpenRead(filePath);
            using var br = new BinaryReader(fs);
            if (br.ReadSByte() != 'S' ||
                br.ReadSByte() != 'A' ||
                br.ReadSByte() != 'V' ||
                br.ReadSByte() != 'R')
                throw new Exception("Invalid SAV header");
            if (br.PeekChar() == 'J')
            {
                br.ReadChar();
                _ = br.ReadString();
            }
            if (br.PeekChar() == 'D')
            {
                br.ReadChar();
                _ = br.ReadInt32();
            }
            _ = br.ReadUInt32();
            while (fs.Position < fs.Length)
            {
                uint size = br.ReadUInt32();
                long start = fs.Position;
                var entity = Facepunch.Pool.Get<ProtoBuf.Entity>();
                try
                {
                    entity.ReadFromStream(fs, (int)size, false);
                    if (entity.baseNetworkable == null)
                    { continue; }
                    if (entity.basePlayer == null && entity.ownerInfo == null && entity.buildingBlock == null && entity.storageBox == null && entity.buildingPrivilege == null && entity.baseVehicle == null)
                    { continue; }
                    result.Add(BuildTrackedEntity(wipeId, entity));
                }
                catch
                {
                    fs.Position = start + size;
                }
                finally
                {
                    entity.Dispose();
                }
            }
            return result;
        }
        private static TrackedEntity BuildTrackedEntity(string wipeId,ProtoBuf.Entity entity)
        {
            var bn = entity.baseNetworkable;
            var tracked = new TrackedEntity
            {
                Id = bn.uid.Value,
                PrefabId = bn.prefabID,
                GroupId = bn.group,
                Parent = entity.parent?.uid.Value ?? 0,
                Playername = entity.basePlayer?.name ?? "",
                Health = entity.baseCombat?.health ?? 0f,
                MaxHealth = entity.baseCombat?.maxHealth,
                TypeClass = entity.GetType().Name,
                OwnerId = entity.ownerInfo?.steamid,
                Extra = new Dictionary<string, object>()
            };
            try
            {
                tracked.PrefabName = Path.GetFileName(
                    GetStringFromPool(wipeId, bn.prefabID) ?? ""
                );
            }
            catch { }
            try
            {
                var p = entity.baseEntity?.pos;
                var r = entity.baseEntity?.rot;

                if (p != null && r != null)
                {
                    tracked.UpdatePosition(
                        p.Value.x, p.Value.y, p.Value.z,
                        r.Value.x, r.Value.y, r.Value.z
                    );
                }
            }
            catch { }
            try
            {
                tracked.UpdateFlags(entity.baseEntity?.flags ?? 0);
            }
            catch { }
            try
            {
                if (entity.basePlayer != null)
                {
                    tracked.Extra["stamina"] = entity.basePlayer.metabolism.heartrate;
                    tracked.Extra["food"] = entity.basePlayer.metabolism.calories;
                    tracked.Extra["water"] = entity.basePlayer.metabolism.hydration;
                }
                else if (entity.worldItem != null)
                {
                    tracked.Extra["itemID"] = entity.worldItem.item.itemid;
                    tracked.Extra["stackCount"] = entity.worldItem.item.amount;
                }
                else if (entity.buildingBlock != null)
                {
                    tracked.Extra["construction"] = entity.buildingBlock.grade;
                }
                else if (entity.baseNPC != null)
                {
                    tracked.Extra["npcClass"] = entity.baseNPC.GetType().Name;
                }
            }
            catch { }
            return tracked;
        }
        private static List<SavEntityDto> ReadSavFile(string WipeID, string file, string? nameFilter)
        {
            var result = new List<SavEntityDto>();
            string? filter = string.IsNullOrWhiteSpace(nameFilter)? null: nameFilter.ToLowerInvariant();
             using var fs = File.OpenRead(file);
            using var br = new BinaryReader(fs);
            if (br.ReadSByte() != 'S' ||
                br.ReadSByte() != 'A' ||
                br.ReadSByte() != 'V' ||
                br.ReadSByte() != 'R')
                throw new Exception("Invalid SAV header");
            if (br.PeekChar() == 'J')
            {
                br.ReadChar();
                _ = br.ReadString();
            }
            if (br.PeekChar() == 'D')
            {
                br.ReadChar();
                _ = br.ReadInt32();
            }
            _ = br.ReadUInt32();
            while (fs.Position < fs.Length)
            {
                uint size = br.ReadUInt32();
                long start = fs.Position;
                var ent = Facepunch.Pool.Get<ProtoBuf.Entity>();
                try
                {
                    ent.ReadFromStream(fs, (int)size, false);
                    uint prefabId = ent.baseNetworkable?.prefabID ?? 0;
                    string? prefab = GetStringFromPool(WipeID, prefabId);
                    if (filter != null && !string.IsNullOrEmpty(prefab) && !prefab.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    {
                        ent.Dispose();
                        continue;
                    }
                    result.Add(new SavEntityDto
                    {
                        Uid = ent.baseNetworkable?.uid.Value ?? 0,
                        PrefabId = prefabId,
                        Prefab = prefab,
                        Px = ent.baseEntity?.pos.x ?? 0,
                        Py = ent.baseEntity?.pos.y ?? 0,
                        Pz = ent.baseEntity?.pos.z ?? 0,
                        Rx = ent.baseEntity?.rot.x ?? 0,
                        Ry = ent.baseEntity?.rot.y ?? 0,
                        Rz = ent.baseEntity?.rot.z ?? 0,
                        OwnerId = ent.ownerInfo?.steamid ?? 0,
                        SkinId = ent.baseEntity?.skinid ?? 0,
                        Health = ent.baseCombat?.health ?? 0f,
                        IsPlayer = ent.basePlayer != null
                    });
                }
                catch
                {
                    fs.Position = start + size;
                }
                finally
                {
                    ent.Dispose();
                }
            }
            return result;
        }
        private static WorldSerialization? GetCachedWorldSerialization(string? wipeId)
        {
            lock (_mapCacheLock)
            {
                if (wipeId == null) { return null; }
                string mapsDir = Path.Combine(DataDirectory, wipeId, "Maps");
                if (!Directory.Exists(mapsDir)) return null;
                var mapFiles = Directory.GetFiles(mapsDir, "*.map");
                if (mapFiles.Length == 0) return null;
                string? latestMapFile = Directory.EnumerateFiles(mapsDir, "*.map").FirstOrDefault();
                if (latestMapFile is null) { return null; }
                if (_mapCache.TryGetValue(wipeId, out var cached) && cached.loadedAt > DateTime.UtcNow.AddMinutes(-MAP_CACHE_TTL_MINUTES) && cached.filePath == latestMapFile)
                {
                    LogDebug($"[MAP CACHE] Hit for {wipeId}");
                    return cached.ws;
                }
                LogDebug($"[MAP CACHE] Miss for {wipeId}, loading: {latestMapFile}");
                var ws = new WorldSerialization();
                ws.Load(latestMapFile);
                _mapCache[wipeId] = (ws, DateTime.UtcNow, latestMapFile);
                return ws;
            }
        }
        private static string? ExtractFilename(string headers)
        {
            int nameIndex = headers.IndexOf("filename=\"");
            if (nameIndex < 0) { return null; }
            int start = nameIndex + 10;
            int end = headers.IndexOf("\"", start);
            if (end < 0) { return null; }
            return headers[start..end];
        }
        private static async Task SaveStringPoolToFile(string? wipeId, Dictionary<uint, string> stringPool)
        {
            if (wipeId == null) return;
            try
            {
                var filepath = Path.Combine(DataDirectory, wipeId, "StringPools", "stringpool.json");
                await _fileWriteSemaphore.WaitAsync();
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("{");
                    sb.AppendJoin(",\n", stringPool.OrderBy(x => x.Key)
                        .Select(kvp => $"  \"{kvp.Key}\": \"{EscapeJsonString(kvp.Value)}\""));
                    sb.AppendLine("\n}");
                    await File.WriteAllTextAsync(filepath, sb.ToString());
                }
                finally { _fileWriteSemaphore.Release(); }
            }
            catch (Exception ex) { LogDebug($"[STORAGE] Failed: {ex.Message}"); }
        }
        private static void SaveManifestToFile(string? wipeId, Dictionary<uint, string> manifest)
        {
            try
            {
                string filename = $"manifest.json";
                if (wipeId == null) { return; }
                string filepath = Path.Combine(DataDirectory, wipeId, "Manifests", SanitizeFilename(filename));
                var sb = new StringBuilder();
                sb.AppendLine("{");
                bool first = true;
                foreach (var kvp in manifest.OrderBy(x => x.Key))
                {
                    if (!first) sb.AppendLine(",");
                    first = false;
                    sb.Append($"  \"{kvp.Key}\": \"{EscapeJsonString(kvp.Value)}\"");
                }
                sb.AppendLine();
                sb.AppendLine("}");
                lock (FileWriteLock) { File.WriteAllText(filepath, sb.ToString()); }
                LogDebug($"[STORAGE] Manifest saved: {wipeId}");
            }
            catch (Exception ex) { LogDebug($"[STORAGE] Failed to save Manifest: {ex.Message}"); }
        }
        private static void SaveSnapshotToFile(string? wipeId, byte[] data)
        {
            if (wipeId == null)
                return;

            try
            {
                string snapshotDir = Path.Combine(DataDirectory, wipeId, "Snapshots");
                Directory.CreateDirectory(snapshotDir);
                var existingFiles = new DirectoryInfo(snapshotDir)
                    .GetFiles("snapshot_*.sav")
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .Skip(3);

                foreach (var oldFile in existingFiles)
                {
                    try { oldFile.Delete(); } catch { }
                }

                string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                string filename = $"snapshot_{timestamp}.sav";
                string filepath = Path.Combine(snapshotDir, SanitizeFilename(filename));
                lock (FileWriteLock)
                {
                    File.WriteAllBytes(filepath, data);
                }
                LogDebug($"[STORAGE] Snapshot saved: {Path.GetFileName(filepath)} ({wipeId})");
                _ = Task.Run(() =>
                {
                    try
                    {
                        var trackedEntities = ReadSavFileEnts(wipeId, filepath);

                        var dict = new ConcurrentDictionary<ulong, TrackedEntity>();

                        foreach (var tracked in trackedEntities)
                        {
                            dict[tracked.Id] = tracked;

                            try
                            {
                                OnEntityCreated?.Invoke(tracked, wipeId);
                            }
                            catch { }
                        }
                        _entitiesByWipe.AddOrUpdate(
                            wipeId,
                            dict,
                            (_, __) => dict
                        );

                        LogDebug($"[SNAPSHOT LOAD] Loaded {dict.Count} entities for wipe {wipeId}");
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"[SNAPSHOT LOAD] Error ({wipeId}): {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                LogDebug($"[STORAGE] Failed to save Snapshot: {ex.Message}");
            }
        }
        private static string SanitizeFilename(string filename)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in invalid) { filename = filename.Replace(c, '_'); }
            return filename;
        }
        private static string EscapeJsonString(string s)
        {
            if (s == null) { return ""; }
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
    #endregion
    #region Classes
    public static class SimpleJsonParser
    {
        public static Dictionary<uint, string> ParseUintStringDict(string json)
        {
            var result = new Dictionary<uint, string>();
            json = json.Trim();
            if (json.StartsWith("{") && json.EndsWith("}"))
            {
                json = json[1..^1];
                var pairs = SplitJsonObjects(json);
                foreach (var pair in pairs)
                {
                    int colonIndex = pair.IndexOf(':');
                    if (colonIndex > 0)
                    {
                        string keyStr = pair[..colonIndex].Trim().Trim('"');
                        string valueStr = pair[(colonIndex + 1)..].Trim().Trim('"');
                        if (uint.TryParse(keyStr, out uint key)) { result[key] = valueStr; }
                    }
                }
            }
            return result;
        }
        private static List<string> SplitJsonObjects(string json)
        {
            var result = new List<string>();
            int depth = 0;
            int start = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\')) { inString = !inString; }
                else if (!inString)
                {
                    if (c == '{' || c == '[') { depth++; }
                    else if (c == '}' || c == ']') { depth--; }
                    else if (c == ',' && depth == 0)
                    {
                        result.Add(json[start..i]);
                        start = i + 1;
                    }
                }
            }
            if (start < json.Length) { result.Add(json[start..]); }
            return result;
        }
    }
    public sealed class TrackedEntity
    {
        public ulong Id;
        public uint PrefabId;
        public string? PrefabName = "";
        public uint GroupId;
        public VectorData pos;
        public VectorData rot;
        public int Flags;
        public bool isDestroyed = false;
        public ulong Parent = 0;
        public string? Playername;
        public float Health;

        public string? TypeClass { get; set; }
        public float? MaxHealth { get; set; }
        public ulong? OwnerId { get; set; }
        public Dictionary<string, object>? Extra { get; set; } = new();

        public TrackedEntity() { }
        public void UpdatePosition(float px, float py, float pz, float rx, float ry, float rz)
        {
            pos = new VectorData(px, py, pz);
            rot = new VectorData(rx, ry, rz);
        }
        public void UpdateFlags(int flags)
        {
            Flags = flags;
        }
    }
    public sealed class TrackedEffect
    {
        public ulong Id;
        public string? PrefabName = "";
        public VectorData pos;
        public TrackedEffect() { }
        public void UpdatePosition(float px, float py, float pz)
        {
            pos = new VectorData(px, py, pz);
        }
    }
    public class ServerInfo
    {
        public string? wipeId { get; set; }
        public DateTime connectedAt { get; set; }
        public DateTime lastActivity { get; set; }
        public long packetsReceived { get; set; }
        public long bytesReceived { get; set; }
        public List<string> receivedFiles { get; set; } = new();
    }
    public class MapInfo
    {
        public int worldsize { get; set; }
        public int mapCount { get; set; }
        public int prefabCount { get; set; }
        public int pathCount { get; set; }
        public int customMonumentCount { get; set; }
        public List<string> mapNames { get; set; } = new();
        public List<string> prefabCategories { get; set; } = new();
        public Dictionary<string, int> prefabCategoryCounts { get; set; } = new();
        public List<string> pathNames { get; set; } = new();
        public List<object> customMonuments { get; set; } = new();
        public long timestamp { get; set; }
        public string? png { get; set; }
    }
    #endregion
    public sealed class SavEntityDto
    {
        public ulong Uid { get; set; }
        public string? Prefab { get; set; } = "";
        public uint PrefabId { get; set; }

        public float Px { get; set; }
        public float Py { get; set; }
        public float Pz { get; set; }

        public float Rx { get; set; }
        public float Ry { get; set; }
        public float Rz { get; set; }

        public ulong OwnerId { get; set; }
        public ulong SkinId { get; set; }

        public float Health { get; set; }

        public bool IsPlayer { get; set; }
    }
    #region Rust.World
    public class WorldSerialization
    {
        public uint Version { get; set; }
        public long Timestamp { get; set; }
        public WorldSerialization()
        {
            Version = 10U;
            Timestamp = 0L;
        }
        public WorldData? world;
        public MapData? GetMap(string name)
        {
            if (this.world == null) { return null; }
            for (int i = 0; i < this.world.maps.Count; i++)
            {
                if (this.world.maps[i].name == name)
                {
                    return this.world.maps[i];
                }
            }
            return null;
        }
        public List<MapData> GetCustomMonuments() { return world?.maps?.Where(x => x?.name != null && (x.name.StartsWith("CustomMonument_") || x.name.StartsWith(":"))).ToList() ?? new List<MapData>(); }
        public void Load(string fileName)
        {
            try
            {
                using (FileStream fileStream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (BinaryReader binaryReader = new BinaryReader(fileStream))
                    {
                        Version = binaryReader.ReadUInt32();
                        if (Version == 9U)
                        {
                            Version = 10U;
                            Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                        }
                        else if (Version == 10U)
                        {
                            Timestamp = binaryReader.ReadInt64();
                        }
                        using (var lz4Stream = new LZ4Stream(fileStream, LZ4StreamMode.Decompress, LZ4StreamFlags.None, 1048576))
                        {
                            try
                            {
                                using (var bufferStream = new BufferStream().Initialize())
                                {
                                    Span<byte> temp = stackalloc byte[64 * 1024];
                                    int read;
                                    while ((read = lz4Stream.Read(temp)) > 0)
                                    {
                                        var range = bufferStream.GetRange(read);
                                        temp.Slice(0, read).CopyTo(range.GetSpan());
                                    }
                                    bufferStream.Position = 0;
                                    world = WorldData.Deserialize(bufferStream);
                                }
                            }
                            catch
                            {
                                Program.LogDebug("Failed to deserialize map.");
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Program.LogDebug(ex.Message); }
        }
    }
    #endregion
    #region Create Map Image
    public class MapRender
    {
        public int splatres;
        public byte[] Splat;
        public int[] Topology;
        public MapRender(byte[] splat, int[] topology)
        {
            splatres = (int)Math.Sqrt(splat.Length / 8);
            Splat = splat;
            Topology = topology;
        }
        public Array2D<Color> output;
        public struct Array2D<T>
        {
            private T[] _items;
            private int _width;
            private int _height;
            public Array2D(T[] items, int width, int height)
            {
                _items = items;
                _width = width;
                _height = height;
            }
            public ref T this[int x, int y]
            {
                get
                {
                    int num = Math.Max(0, Math.Min(x, _width - 1));
                    int num2 = Math.Max(0, Math.Min(y, _height - 1));
                    return ref _items[num2 * _width + num];
                }
            }
        }
        public int GetTopology(int x, int z) { return Topology[z * splatres + x]; }
        public static float Byte2Float(int b) => b / 255f;
        private static Dictionary<int, int> type2index = new Dictionary<int, int> { { 8, 3 }, { 16, 4 }, { 4, 2 }, { 1, 0 }, { 32, 5 }, { 64, 6 }, { 2, 1 }, { 128, 7 } };
        public static int TypeToIndex(int id) => type2index[id];
        public static int IndexToType(int idx) => 1 << idx;
        public float GetSplat(int x, int z, int mask)
        {
            if (mask > 0 && (mask & (mask - 1)) == 0)
                return Byte2Float(Splat[(TypeToIndex(mask) * splatres + z) * splatres + x]);
            int sum = 0;
            for (int i = 0; i < 8; i++)
                if ((IndexToType(i) & mask) != 0)
                    sum += Splat[(i * splatres + z) * splatres + x];
            return Math.Min(Byte2Float(sum), 1f);
        }
        public readonly struct Vec4
        {
            public readonly float x, y, z, w;
            public Vec4(float x, float y, float z, float w = 1f) { this.x = x; this.y = y; this.z = z; this.w = w; }
            public static Vec4 operator *(Vec4 v, float f) => new Vec4(v.x * f, v.y * f, v.z * f, v.w * f);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vec4 Lerp(Vec4 a, Vec4 b, float t) => new Vec4(
                a.x + (b.x - a.x) * t,
                a.y + (b.y - a.y) * t,
                a.z + (b.z - a.z) * t,
                a.w + (b.w - a.w) * t
            );
        }
        public class Config
        {
            public Vec4 StartColor = new Vec4(0.28627452f, 0.27058825f, 0.24705884f, 1f);
            public Vec4 OffShoreColor = new Vec4(0.04090196f, 0.22060032f, 0.27450982f, 1f);
            public Vec4 GravelColor = new Vec4(0.25f, 0.24342105f, 0.22039475f, 1f);
            public Vec4 DirtColor = new Vec4(0.6f, 0.47959462f, 0.33f, 1f);
            public Vec4 SandColor = new Vec4(0.7f, 0.65968585f, 0.5277487f, 1f);
            public Vec4 GrassColor = new Vec4(0.35486364f, 0.37f, 0.2035f, 1f);
            public Vec4 ForestColor = new Vec4(0.24843751f, 0.3f, 0.0703125f, 1f);
            public Vec4 RockColor = new Vec4(0.4f, 0.39379844f, 0.37519377f, 1f);
            public Vec4 SnowColor = new Vec4(0.86274517f, 0.9294118f, 0.94117653f, 1f);
            public Vec4 PebbleColor = new Vec4(0.13725491f, 0.2784314f, 0.2761563f, 1f);
        }
        [Flags]
        public enum MapEnum
        {
            Ocean = 128,
            Lake = 65536,
            River = 16384,
        }
        public byte[]? Render()
        {
            if (Topology == null || Splat == null) return null;
            var config = new Config();
            Color[] array = new Color[splatres * splatres];
            output = new Array2D<Color>(array, splatres, splatres);
            Parallel.For(0, splatres, x =>
            {
                float[] splatValues = new float[8];
                for (int z = 0; z < splatres; z++)
                {
                    Vec4 vector = config.StartColor;
                    MapEnum topology = (MapEnum)GetTopology(x, z);
                    bool ocean = (topology & MapEnum.Ocean) != 0;
                    if (ocean)
                        vector = config.OffShoreColor;
                    else if ((topology & (MapEnum.Lake | MapEnum.River)) != 0)
                        vector = Vec4.Lerp(vector, config.OffShoreColor, 1f);
                    else
                    {
                        splatValues[0] = GetSplat(x, z, 128);
                        splatValues[1] = GetSplat(x, z, 64);
                        splatValues[2] = GetSplat(x, z, 8);
                        splatValues[3] = GetSplat(x, z, 1);
                        splatValues[4] = GetSplat(x, z, 16);
                        splatValues[5] = GetSplat(x, z, 32);
                        splatValues[6] = GetSplat(x, z, 4);
                        splatValues[7] = GetSplat(x, z, 2);
                        vector = Vec4.Lerp(vector, config.GravelColor, splatValues[0] * config.GravelColor.w);
                        vector = Vec4.Lerp(vector, config.PebbleColor, splatValues[1] * config.PebbleColor.w);
                        vector = Vec4.Lerp(vector, config.RockColor, splatValues[2] * config.RockColor.w);
                        vector = Vec4.Lerp(vector, config.DirtColor, splatValues[3] * config.DirtColor.w);
                        vector = Vec4.Lerp(vector, config.GrassColor, splatValues[4] * config.GrassColor.w);
                        vector = Vec4.Lerp(vector, config.ForestColor, splatValues[5] * config.ForestColor.w);
                        vector = Vec4.Lerp(vector, config.SandColor, splatValues[6] * config.SandColor.w);
                        vector = Vec4.Lerp(vector, config.SnowColor, splatValues[7] * config.SnowColor.w);
                    }
                    vector *= 1.05f;
                    array[z * splatres + x] = Color.FromArgb(
                        255,
                        ClampToByte(vector.x * 255f),
                        ClampToByte(vector.y * 255f),
                        ClampToByte(vector.z * 255f));
                }
            });
            return EncodeBmp(array, splatres, splatres);
        }
        private static byte ClampToByte(float v) => (byte)Math.Max(0, Math.Min(255, (int)v));
        private byte[] EncodeBmp(Color[] pixels, int width, int height)
        {
            int maxDim = Math.Max(width, height);
            float scale = maxDim > 512 ? 512f / maxDim : 1f;
            int newWidth = (int)(width * scale);
            int newHeight = (int)(height * scale);
            Color[] scaled = new Color[newWidth * newHeight];
            for (int y = 0; y < newHeight; y++)
            {
                int srcY = (int)(y / scale);
                for (int x = 0; x < newWidth; x++)
                {
                    int srcX = (int)(x / scale);
                    scaled[y * newWidth + x] = pixels[srcY * width + srcX];
                }
            }
            Color[] mirrored = new Color[scaled.Length];
            for (int y = 0; y < newHeight; y++)
            {
                for (int x = 0; x < newWidth; x++)
                {
                    mirrored[y * newWidth + x] = scaled[y * newWidth + (newWidth - 1 - x)];
                }
            }
            Color[] rotated = new Color[mirrored.Length];
            for (int y = 0; y < newHeight; y++)
            {
                for (int x = 0; x < newWidth; x++)
                {
                    rotated[y * newWidth + x] = mirrored[(newHeight - 1 - y) * newWidth + (newWidth - 1 - x)];
                }
            }
            int rowSize = (newWidth * 3 + 3) & ~3;
            int fileSize = 54 + rowSize * newHeight;
            byte[] buffer = new byte[fileSize];
            using var ms = new MemoryStream(buffer);
            using var bw = new BinaryWriter(ms);
            bw.Write((ushort)0x4D42);
            bw.Write(fileSize);
            bw.Write(0);
            bw.Write(54);
            bw.Write(40);
            bw.Write(newWidth);
            bw.Write(newHeight);
            bw.Write((ushort)1);
            bw.Write((ushort)24);
            bw.Write(0);
            bw.Write(rowSize * newHeight);
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            for (int y = 0; y < newHeight; y++)
            {
                int srcY = newHeight - 1 - y;
                for (int x = 0; x < newWidth; x++)
                {
                    var p = rotated[srcY * newWidth + x];
                    bw.Write(p.B);
                    bw.Write(p.G);
                    bw.Write(p.R);
                }
                for (int i = 0; i < rowSize - newWidth * 3; i++)
                    bw.Write((byte)0);
            }
            return buffer;
        }
    }
    public enum KnownColor
    {
        ActiveBorder = 1,
        ActiveCaption,
        ActiveCaptionText,
        AppWorkspace,
        Control,
        ControlDark,
        ControlDarkDark,
        ControlLight,
        ControlLightLight,
        ControlText,
        Desktop,
        GrayText,
        Highlight,
        HighlightText,
        HotTrack,
        InactiveBorder,
        InactiveCaption,
        InactiveCaptionText,
        Info,
        InfoText,
        Menu,
        MenuText,
        ScrollBar,
        Window,
        WindowFrame,
        WindowText,
        Transparent,
        AliceBlue,
        AntiqueWhite,
        Aqua,
        Aquamarine,
        Azure,
        Beige,
        Bisque,
        Black,
        BlanchedAlmond,
        Blue,
        BlueViolet,
        Brown,
        BurlyWood,
        CadetBlue,
        Chartreuse,
        Chocolate,
        Coral,
        CornflowerBlue,
        Cornsilk,
        Crimson,
        Cyan,
        DarkBlue,
        DarkCyan,
        DarkGoldenrod,
        DarkGray,
        DarkGreen,
        DarkKhaki,
        DarkMagenta,
        DarkOliveGreen,
        DarkOrange,
        DarkOrchid,
        DarkRed,
        DarkSalmon,
        DarkSeaGreen,
        DarkSlateBlue,
        DarkSlateGray,
        DarkTurquoise,
        DarkViolet,
        DeepPink,
        DeepSkyBlue,
        DimGray,
        DodgerBlue,
        Firebrick,
        FloralWhite,
        ForestGreen,
        Fuchsia,
        Gainsboro,
        GhostWhite,
        Gold,
        Goldenrod,
        Gray,
        Green,
        GreenYellow,
        Honeydew,
        HotPink,
        IndianRed,
        Indigo,
        Ivory,
        Khaki,
        Lavender,
        LavenderBlush,
        LawnGreen,
        LemonChiffon,
        LightBlue,
        LightCoral,
        LightCyan,
        LightGoldenrodYellow,
        LightGray,
        LightGreen,
        LightPink,
        LightSalmon,
        LightSeaGreen,
        LightSkyBlue,
        LightSlateGray,
        LightSteelBlue,
        LightYellow,
        Lime,
        LimeGreen,
        Linen,
        Magenta,
        Maroon,
        MediumAquamarine,
        MediumBlue,
        MediumOrchid,
        MediumPurple,
        MediumSeaGreen,
        MediumSlateBlue,
        MediumSpringGreen,
        MediumTurquoise,
        MediumVioletRed,
        MidnightBlue,
        MintCream,
        MistyRose,
        Moccasin,
        NavajoWhite,
        Navy,
        OldLace,
        Olive,
        OliveDrab,
        Orange,
        OrangeRed,
        Orchid,
        PaleGoldenrod,
        PaleGreen,
        PaleTurquoise,
        PaleVioletRed,
        PapayaWhip,
        PeachPuff,
        Peru,
        Pink,
        Plum,
        PowderBlue,
        Purple,
        RebeccaPurple,
        Red,
        RosyBrown,
        RoyalBlue,
        SaddleBrown,
        Salmon,
        SandyBrown,
        SeaGreen,
        SeaShell,
        Sienna,
        Silver,
        SkyBlue,
        SlateBlue,
        SlateGray,
        Snow,
        SpringGreen,
        SteelBlue,
        Tan,
        Teal,
        Thistle,
        Tomato,
        Turquoise,
        Violet,
        Wheat,
        White,
        WhiteSmoke,
        Yellow,
        YellowGreen,
        ButtonFace,
        ButtonHighlight,
        ButtonShadow,
        GradientActiveCaption,
        GradientInactiveCaption,
        MenuBar,
        MenuHighlight
    }
    public readonly struct Color
    {
        private const short StateKnownColorValid = 0x0001;
        private const short StateARGBValueValid = 0x0002;
        private const short StateValueMask = StateARGBValueValid;
        private const short StateNameValid = 0x0008;
        private const long NotDefinedValue = 0;
        internal const int ARGBAlphaShift = 24;
        internal const int ARGBRedShift = 16;
        internal const int ARGBGreenShift = 8;
        internal const int ARGBBlueShift = 0;
        internal const uint ARGBAlphaMask = 0xFFu << ARGBAlphaShift;
        internal const uint ARGBRedMask = 0xFFu << ARGBRedShift;
        internal const uint ARGBGreenMask = 0xFFu << ARGBGreenShift;
        internal const uint ARGBBlueMask = 0xFFu << ARGBBlueShift;
        private readonly long value;
        private readonly short knownColor;
        private readonly short state;

        internal Color(KnownColor knownColor)
        {
            value = 0;
            state = StateKnownColorValid;
            this.knownColor = unchecked((short)knownColor);
        }

        private Color(long value, short state, string? name, KnownColor knownColor)
        {
            this.value = value;
            this.state = state;
            this.knownColor = unchecked((short)knownColor);
        }

        public byte R => unchecked((byte)(Value >> ARGBRedShift));
        public byte G => unchecked((byte)(Value >> ARGBGreenShift));
        public byte B => unchecked((byte)(Value >> ARGBBlueShift));
        public byte A => unchecked((byte)(Value >> ARGBAlphaShift));
        public bool IsKnownColor => (state & StateKnownColorValid) != 0;
        public bool IsEmpty => state == 0;
        public bool IsNamedColor => ((state & StateNameValid) != 0) || IsKnownColor;
        public bool IsSystemColor => IsKnownColor && IsKnownColorSystem((KnownColor)knownColor);
        internal static bool IsKnownColorSystem(KnownColor knownColor)
        {
            var color = Color.FromKnownColor(knownColor);
            return color.IsSystemColor;
        }

        private long Value
        {
            get
            {
                if ((state & StateValueMask) != 0) { return value; }
                if (IsKnownColor) { return Color.FromKnownColor((KnownColor)knownColor).ToArgb(); }
                return NotDefinedValue;
            }
        }

        private static void CheckByte(int value)
        {
            static void ThrowOutOfByteRange() => throw new ArgumentException();
            if (unchecked((uint)value) > byte.MaxValue) { ThrowOutOfByteRange(); }
        }

        private static Color FromArgb(uint argb) => new Color(argb, StateARGBValueValid, null, (KnownColor)0);

        public static Color FromArgb(int alpha, int red, int green, int blue)
        {
            CheckByte(alpha);
            CheckByte(red);
            CheckByte(green);
            CheckByte(blue);
            return FromArgb(
                (uint)alpha << ARGBAlphaShift |
                (uint)red << ARGBRedShift |
                (uint)green << ARGBGreenShift |
                (uint)blue << ARGBBlueShift
            );
        }


        public static Color FromKnownColor(KnownColor color) =>
            color <= 0 || color > KnownColor.RebeccaPurple ? FromName(color.ToString()) : new Color(color);

        public static Color FromName(string name)
        {
            return new Color(NotDefinedValue, StateNameValid, name, (KnownColor)0);
        }

        public int ToArgb() => unchecked((int)Value);
   
    }
    #endregion

    #region HTML Pages
    public static class HtmlStyles
    {
        public const string BackgroundPrimary = "#1a1a2e";
        public const string BackgroundSecondary = "#16213e";
        public const string BackgroundDark = "#0f0f23";
        public const string AccentPrimary = "#4ecdc4";
        public const string AccentSecondary = "#ff6b6b";
        public const string AccentInfo = "#00d9ff";
        public const string TextPrimary = "#eee";
        public const string TextSecondary = "#888";
        public const string TextMuted = "#666";
        public const string Success = "#64ff64";
        public const string Warning = "#ffc107";
        public const string BorderDefault = "#333";
        public static string GetBaseStyles() => @"
* { box-sizing: border-box; margin: 0; padding: 0; }
body {
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
    background: " + BackgroundPrimary + @";
    color: " + TextPrimary + @";
    min-height: 100vh;
    -webkit-text-size-adjust: 100%;
    text-size-adjust: 100%;
}
.container { max-width: 1400px; margin: 0 auto; padding: 12px; }
@media (min-width: 768px) {
    .container { padding: 20px; }
}
h1 { color: " + AccentSecondary + @"; margin-bottom: 16px; font-size: 1.5rem; }
@media (min-width: 768px) { h1 { margin-bottom: 20px; font-size: 2rem; } }
h2 { color: " + AccentPrimary + @"; margin: 0 0 12px; font-size: 1.25rem; }
@media (min-width: 768px) { h2 { margin: 0 0 15px; font-size: 1.5rem; } }
h3 { color: " + AccentSecondary + @"; margin: 12px 0 8px; font-size: 1rem; }
@media (min-width: 768px) { h3 { margin: 15px 0 10px; } }
.card {
    background: " + BackgroundSecondary + @";
    border-radius: 10px;
    padding: 15px;
    margin-bottom: 15px;
}
@media (min-width: 768px) { .card { padding: 20px; margin-bottom: 20px; } }
.subtitle { color: " + TextMuted + @"; margin-bottom: 15px; font-size: 0.875rem; }
.back-link { color: " + AccentPrimary + @"; text-decoration: none; margin-bottom: 15px; display: inline-block; }
.back-link:hover { text-decoration: underline; }
.login-form { max-width: 300px; margin: 60px auto; padding: 15px; }
@media (min-width: 768px) { .login-form { margin: 100px auto; padding: 20px; } }
input[type=""password""], input[type=""text""] {
    width: 100%;
    padding: 14px 12px;
    border: 1px solid " + BorderDefault + @";
    border-radius: 5px;
    background: " + BackgroundDark + @";
    color: " + TextPrimary + @";
    margin-bottom: 10px;
    font-size: 16px;
    min-height: 48px;
}
button, .action-btn {
    width: 100%;
    padding: 14px 12px;
    background: " + AccentSecondary + @";
    border: none;
    border-radius: 5px;
    color: #fff;
    cursor: pointer;
    font-size: 16px;
    font-weight: bold;
    transition: background 0.2s ease;
    min-height: 48px;
    touch-action: manipulation;
    -webkit-tap-highlight-color: transparent;
}
button:hover, .action-btn:hover { background: #ff5252; }
button:active, .action-btn:active { transform: scale(0.98); }
.error { color: " + AccentSecondary + @"; margin-top: 10px; display: none; font-size: 14px; }
.table-wrapper {
    overflow-x: auto;
    -webkit-overflow-scrolling: touch;
    margin: 0 -15px;
    padding: 0 15px;
}
@media (min-width: 768px) { .table-wrapper { margin: 0; padding: 0; } }
table { width: 100%; border-collapse: collapse; margin-top: 10px; }
th, td { padding: 10px 8px; text-align: left; border-bottom: 1px solid " + BorderDefault + @"; font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
@media (min-width: 768px) { th, td { padding: 12px; font-size: 14px; } }
th { background: " + BackgroundDark + @"; color: " + AccentPrimary + @"; position: sticky; top: 0; }
th.wipe-id-col { width: 340px; min-width: 340px; }
td.wipe-id-col { max-width: 340px; min-width: 340px; }
td.wipe-id-col code { display: block; overflow: hidden; text-overflow: ellipsis; font-size: 11px; }
@media (max-width: 767px) {
    th.wipe-id-col { width: 260px; min-width: 260px; }
    td.wipe-id-col { max-width: 260px; min-width: 260px; }
}
tr:hover { background: #1f3050; }
tr.clickable { cursor: pointer; }
.stats {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(100px, 1fr));
    gap: 10px;
    margin-bottom: 15px;
}
@media (min-width: 768px) { .stats { gap: 15px; margin-bottom: 20px; } }
.stat-box {
    background: " + BackgroundDark + @";
    padding: 12px;
    border-radius: 8px;
    text-align: center;
}
@media (min-width: 768px) { .stat-box { padding: 15px; } }
.stat-value { font-size: 20px; font-weight: bold; color: " + AccentPrimary + @"; }
@media (min-width: 768px) { .stat-value { font-size: 24px; } }
.stat-label { font-size: 10px; color: " + TextSecondary + @"; margin-top: 4px; }
@media (min-width: 768px) { .stat-label { font-size: 12px; } }
.refresh-btn { background: " + AccentPrimary + @"; color: #000; }
.refresh-btn:hover { background: #3dbdb5; }
.logout-btn { background: " + TextMuted + @"; margin-top: 15px; }
@media (min-width: 768px) { .logout-btn { margin-top: 20px; } }
.logout-btn:hover { background: #555; }
.last-update { color: " + TextMuted + @"; font-size: 11px; margin-top: 10px; }
.badge {
    display: inline-block;
    padding: 4px 8px;
    border-radius: 4px;
    font-size: 10px;
    margin-right: 4px;
    margin-bottom: 4px;
}
@media (min-width: 768px) { .badge { font-size: 11px; margin-right: 5px; } }
.badge-success { background: rgba(100, 200, 100, 0.2); color: " + Success + @"; }
.badge-warning { background: rgba(255, 193, 7, 0.2); color: " + Warning + @"; }
.badge-info { background: rgba(0, 217, 255, 0.2); color: " + AccentInfo + @"; }
.action-btn.secondary { background: " + TextMuted + @"; color: #fff; }
.action-btn.secondary:hover { background: #555; }
.action-buttons { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 12px; }
@media (min-width: 768px) { .action-buttons { gap: 10px; margin-top: 15px; } }
.action-buttons .action-btn { width: auto; min-width: 100px; flex: 1; }
.loading { text-align: center; padding: 30px; color: " + TextSecondary + @"; }
.error-box {
    background: rgba(255, 107, 107, 0.1);
    border: 1px solid " + AccentSecondary + @";
    border-radius: 8px;
    padding: 12px;
    color: " + AccentSecondary + @";
    font-size: 14px;
}
@media (min-width: 768px) { .error-box { padding: 15px; } }
.info-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
    gap: 8px;
    margin-bottom: 15px;
}
@media (min-width: 768px) { .info-grid { grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 10px; } }
.info-item { background: " + BackgroundDark + @"; padding: 10px; border-radius: 6px; }
@media (min-width: 768px) { .info-item { padding: 12px; } }
.info-item .label { font-size: 10px; color: " + TextMuted + @"; text-transform: uppercase; }
@media (min-width: 768px) { .info-item .label { font-size: 11px; } }
.info-item .value { font-size: 13px; color: " + AccentPrimary + @"; margin-top: 4px; }
@media (min-width: 768px) { .info-item .value { font-size: 14px; } }
.clickable { cursor: pointer; }
.clickable:hover { opacity: 0.8; }
.badge.clickable:hover { background: rgba(0, 217, 255, 0.4); }
.detail-link { color: " + AccentPrimary + @"; text-decoration: none; font-size: 11px; }
@media (min-width: 768px) { .detail-link { font-size: 12px; } }
.detail-link:hover { text-decoration: underline; }
.pos-cell { font-family: monospace; font-size: 10px; word-break: break-word; }
@media (min-width: 768px) { .pos-cell { font-size: 11px; } }
.modal {
    display: none;
    position: fixed;
    top: 0;
    left: 0;
    width: 100%;
    height: 100%;
    background: rgba(0,0,0,0.7);
    z-index: 1000;
    padding: 10px;
}
.modal.show { display: flex; align-items: flex-start; justify-content: center; overflow-y: auto; }
@media (min-width: 768px) { .modal { padding: 0; align-items: center; } }
.modal-content {
    background: " + BackgroundSecondary + @";
    border-radius: 10px;
    padding: 15px;
    max-width: 1400px;
    width: 100%;
    max-height: calc(100vh - 20px);
    overflow-y: auto;
    overflow-x: hidden;
}
@media (min-width: 768px) { .modal-content { padding: 20px; max-height: 90vh; } }
.modal-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px; flex-wrap: wrap; gap: 10px; }
@media (min-width: 768px) { .modal-header { margin-bottom: 15px; } }
.modal-header h2 { margin: 0; }
.close-btn { background: none; border: none; color: " + AccentSecondary + @"; font-size: 28px; cursor: pointer; width: auto; padding: 5px 10px; min-height: 44px; min-width: 44px; }
.close-btn:hover { color: #ff5252; }
.pagination { display: flex; justify-content: center; align-items: center; gap: 8px; margin-top: 12px; flex-wrap: wrap; }
@media (min-width: 768px) { .pagination { gap: 10px; margin-top: 15px; } }
.pagination button {
    padding: 10px 14px;
    background: " + AccentPrimary + @";
    border: none;
    border-radius: 5px;
    color: #000;
    cursor: pointer;
    width: auto;
    min-width: 44px;
    min-height: 44px;
    font-size: 14px;
}
.pagination button:disabled { background: " + TextMuted + @"; cursor: not-allowed; }
.pagination button:hover:not(:disabled) { background: #3dbdb5; }
.pagination span { color: " + TextSecondary + @"; font-size: 12px; }
@media (min-width: 768px) { .pagination span { font-size: 14px; } }
.filter-bar { display: flex; gap: 6px; margin-bottom: 12px; flex-wrap: wrap; overflow-x: auto; -webkit-overflow-scrolling: touch; padding-bottom: 5px; }
@media (min-width: 768px) { .filter-bar { gap: 10px; margin-bottom: 15px; } }
.filter-bar .badge { cursor: pointer; white-space: nowrap; padding: 6px 10px; font-size: 11px; }
@media (min-width: 768px) { .filter-bar .badge { padding: 4px 8px; font-size: 12px; } }
.filter-bar .badge.active { background: " + AccentPrimary + @"; color: #000; }
.download-link { color: " + AccentPrimary + @"; cursor: pointer; text-decoration: underline; font-size: 12px; }
@media (min-width: 768px) { .download-link { font-size: 14px; } }
.download-link:hover { color: #3dbdb5; }
.map-preview {
    display: inline-block;
    margin-bottom: 8px;
    cursor: zoom-in;
}
.map-preview img {
    max-width: 100px;
    border-radius: 6px;
    border: 2px solid " + AccentPrimary + @";
    background: " + BackgroundDark + @";
    transition: transform 0.15s ease;
}
@media (min-width: 768px) { .map-preview img { max-width: 120px; } }
.map-preview:hover img { transform: scale(1.03); }
.map-preview-overlay {
    display: none;
    position: fixed;
    inset: 0;
    background: rgba(0,0,0,0.85);
    z-index: 2000;
    align-items: center;
    justify-content: center;
    padding: 10px;
}
.map-preview-overlay img {
    max-width: 100%;
    max-height: 100%;
    border-radius: 10px;
    box-shadow: 0 0 40px rgba(0,0,0,0.6);
}
p { color: " + TextSecondary + @"; margin-bottom: 12px; font-size: 13px; }
@media (min-width: 768px) { p { margin-bottom: 15px; font-size: 14px; } }
@media (max-width: 767px) {
    .desktop-only { display: none !important; }
    .mobile-friendly { touch-action: manipulation; }
}
@media (min-width: 768px) {
    .mobile-only { display: none !important; }
}
";
        public static string Get3DViewerStyles() => @"
* { margin: 0; padding: 0; box-sizing: border-box; }
html, body {
overflow: hidden;
font-family: 'Segoe UI', Arial, sans-serif;
background: " + BackgroundPrimary + @";
touch-action: none;
position: fixed;
width: 100%;
height: 100%;
}
#canvas-container { width: 100vw; height: 100vh; height: 100dvh; }
#loading {
position: fixed; top: 0; left: 0; width: 100%; height: 100%;
background: linear-gradient(135deg, " + BackgroundPrimary + @" 0%, #16213e 100%);
display: flex; flex-direction: column; align-items: center; justify-content: center;
z-index: 1000; transition: opacity 0.5s ease;
}
#loading.hidden { opacity: 0; pointer-events: none; }
.loader {
width: 60px; height: 60px; border: 4px solid rgba(255,255,255,0.1);
border-top-color: " + AccentInfo + @"; border-radius: 50%;
animation: spin 1s linear infinite;
}
@keyframes spin { to { transform: rotate(360deg); } }
#loading-text { color: " + AccentInfo + @"; margin-top: 20px; font-size: 18px; letter-spacing: 2px; }
#debug-info {
position: fixed; top: 1px; left: 1px; background: rgba(0,0,0,0.85);
padding: 15px; border-radius: 10px; color: " + Success + @"; font-size: 12px;
font-family: monospace; max-width: 500px; max-height: 300px; overflow-y: auto; z-index: 1001;
}
#debug-info.hidden { display: none; }
#debug-info .error { color: #ff4444; }
#debug-info .success { color: " + Success + @"; }
#debug-info .info { color: " + Warning + @"; }
#controls {
position: fixed; bottom: 1px; left: 1px; background: rgba(0,0,0,0.7);
padding: 5px 10px; border-radius: 5px; color: #fff; font-size: 8px;
backdrop-filter: blur(10px); border: 1px solid rgba(0,217,255,0.3);
}
#controls h3 { color: " + AccentInfo + @"; margin-bottom: 10px; font-size: 8px; }
#controls p { margin: 5px 0; opacity: 0.8; }
#controls span { color: " + AccentInfo + @"; font-weight: bold; }
#info {
position: fixed; top: 1px; right: 1px; background: rgba(0,0,0,0.7);
padding: 5px 10px; border-radius: 6px; color: #fff; font-size: 10px;
backdrop-filter: blur(10px); border: 1px solid rgba(0,217,255,0.3);
}
#info .coord { color: " + AccentInfo + @"; font-family: monospace; }
#layer-toggle {
position: fixed; top: 1px; left: 50%; transform: translateX(-50%);
background: rgba(0,0,0,0.7); padding: 10px 20px; border-radius: 10px;
backdrop-filter: blur(10px); border: 1px solid rgba(0,217,255,0.3);
display: flex; gap: 15px; z-index: 1001;
}
#layer-toggle label { font-size: 8px; color: #fff; cursor: pointer; display: flex; align-items: center; gap: 5px; }
#layer-toggle input[type=""checkbox""] { accent-color: " + AccentInfo + @"; }
#joystick-container {
position: fixed;
bottom: 10px;
left: 10px;
width: 120px;
height: 120px;
background: rgba(0, 0, 0, 0.5);
border-radius: 50%;
border: 3px solid rgba(0, 217, 255, 0.5);
touch-action: none;
z-index: 1002;
display: none;
}
#joystick-knob {
position: absolute;
width: 50px;
height: 50px;
background: rgba(0, 217, 255, 0.8);
border-radius: 50%;
top: 50%;
left: 50%;
transform: translate(-50%, -50%);
box-shadow: 0 0 15px rgba(0, 217, 255, 0.5);
}
#joystick-knob::after {
content: '';
position: absolute;
width: 20px;
height: 20px;
background: rgba(255, 255, 255, 0.6);
border-radius: 50%;
top: 50%;
left: 50%;
transform: translate(-50%, -50%);
}
#look-area {
position: fixed;
top: 0;
right: 0;
width: 50%;
height: 100%;
touch-action: none;
z-index: 1001;
display: none;
}
#speed-btn {
position: fixed;
bottom: 160px;
left: 10px;
width: 60px;
height: 60px;
background: rgba(0, 0, 0, 0.5);
border: 3px solid rgba(0, 217, 255, 0.5);
border-radius: 50%;
color: " + AccentInfo + @";
font-size: 11px;
font-weight: bold;
cursor: pointer;
z-index: 1002;
display: none;
align-items: center;
justify-content: center;
touch-action: none;
}
#speed-btn.active {
background: rgba(0, 217, 255, 0.5);
border-color: " + AccentInfo + @";
}
.viewer-btn {
position: fixed; bottom: 10px; right: 10px;
width: 80px; height: 30px;
background: rgba(0, 100, 0, 0.7); border: 2px solid " + Success + @";
border-radius: 8px; color: " + Success + @"; font-size: 12px;
font-weight: bold; cursor: pointer; z-index: 1003;
transition: all 0.2s ease;
}
.viewer-btn:hover { background: rgba(0, 150, 0, 0.8); }
.viewer-btn.active { background: rgba(255, 0, 0, 0.6); color: #fff; border-color: #ff4444; box-shadow: 0 0 10px rgba(255, 68, 68, 0.5); }
@media (hover: none), (pointer: coarse) {
#controls { display: none !important; }
#joystick-container { display: block; }
#look-area { display: block; }
#speed-btn { display: flex; }
#noclip-btn { display: none; }
#layer-toggle { flex-wrap: wrap; justify-content: center; max-width: 90%; padding: 8px 15px; }
#layer-toggle label { font-size: 11px; }
#info { font-size: 11px; padding: 10px 15px; }
}
@media (hover: hover) and (pointer: fine) {
#controls { display: block; }
}
";
    }
    public static class HtmlCache
    {
        private static readonly Dictionary<string, byte[]> _cache = new();
        private static readonly object _lock = new();
        public static void Clear()
        {
            lock (_lock)
            {
                _cache.Clear();
            }
        }
        public static byte[]? Get(string key)
        {
            lock (_lock)
            {
                return _cache.TryGetValue(key, out var cached) ? cached : null;
            }
        }
        public static void Set(string key, string html)
        {
            lock (_lock)
            {
                _cache[key] = System.Text.Encoding.UTF8.GetBytes(html);
            }
        }
        public static int TotalCacheSize
        {
            get
            {
                lock (_lock)
                {
                    return _cache.Values.Sum(x => x.Length);
                }
            }
        }
        public static string IndexKey(bool isAuthenticated) => isAuthenticated ? "index:auth" : "index:guest";
        public static string ServerDetailKey(string wipeId, bool isAuthenticated) => $"server:{wipeId}:{(isAuthenticated ? "auth" : "guest")}";
        public static string Viewer3DKey(string wipeId) => $"viewer3d:{wipeId}";
    }
    public class HTML
    {

        private static string EscapeJavaScriptString(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("'", "\\'");
        }

        public static byte[] GetIndexHtmlBytes(bool isAuthenticated)
        {
            var key = HtmlCache.IndexKey(isAuthenticated);
            var cached = HtmlCache.Get(key);
            if (cached != null) return cached;
            var html = GenerateIndexHtml(isAuthenticated);
            HtmlCache.Set(key, html);
            return System.Text.Encoding.UTF8.GetBytes(html);
        }

        public static byte[] GetServerDetailHtmlBytes(string wipeId, bool isAuthenticated)
        {
            var key = HtmlCache.ServerDetailKey(wipeId, isAuthenticated);
            var cached = HtmlCache.Get(key);
            if (cached != null)
                return cached;
            var html = GenerateServerDetailHtml(wipeId, isAuthenticated);
            HtmlCache.Set(key, html);
            return System.Text.Encoding.UTF8.GetBytes(html);
        }

        public static byte[] Get3DViewerHtmlBytes(string wipeId)
        {
            var key = HtmlCache.Viewer3DKey(wipeId);
            var cached = HtmlCache.Get(key);
            if (cached != null) return cached;
            var html = Generate3DViewerHtml(wipeId);
            HtmlCache.Set(key, html);
            return System.Text.Encoding.UTF8.GetBytes(html);
        }

        public static string Generate3DViewerHtml(string WipeID)
        {
            return @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no, viewport-fit=cover"">
<meta name=""apple-mobile-web-app-capable"" content=""yes"">
<meta name=""apple-mobile-web-app-status-bar-style"" content=""black-translucent"">
<title>3D Map</title>
<link rel=""icon"" type=""image/x-icon"" href=""/favicon.ico"">
<style>
" + HtmlStyles.Get3DViewerStyles() + @"
</style>
</head>
<body>
<div id=""loading"">
<div class=""loader""></div>
<div id=""loading-text"">Loading Terrain...</div>
</div>
<div id=""debug-info"" class=""hidden""></div>
<div id=""canvas-container""></div>
<div id=""joystick-container"">
<div id=""joystick-knob""></div>
</div>
<div id=""look-area""></div>
<button id=""back-btn"" class=""viewer-btn"" onclick=""window.history.back()"">Back</button>
<button id=""speed-btn"">FAST</button>
<button id=""noclip-btn"" class=""viewer-btn"">No Clip</button>
<div id=""layer-toggle"">
<label><input type=""checkbox"" id=""showDebug""> Debug</label>
<label><input type=""checkbox"" id=""showRoads"" checked> Roads</label>
<label><input type=""checkbox"" id=""showRails"" checked> Rails</label>
<label><input type=""checkbox"" id=""showPrefabs"" checked> Prefabs</label>
<label><input type=""checkbox"" id=""showBB"" checked> PreventBuilding</label>
<label><input type=""checkbox"" id=""showWater"" checked> Water</label>
<label><input type=""checkbox"" id=""showLabels"" checked> Labels</label>
<label><input type=""checkbox"" id=""showEntities"" checked> Entities</label>
<label><input type=""checkbox"" id=""showCubes"" checked> Cubes</label>
</div>
<div id=""controls"">
<h3>Navigation Controls</h3>
<p><span>W/A/S/D</span> - Move Forward/Left/Back/Right</p>
<p><span>Mouse</span> - Look Around</p>
<p><span>Shift</span> - Move Fast / Run</p>
<p><span>Space</span> - Jump (NoClip Only)</p>
<p><span>C</span> - Duck (NoClip Only)</p>
<p><span>ESC</span> - Release Pointer Lock</p>
</div>
<div id=""info"">
<p>Position: <span class=""coord"" id=""pos-display"">0, 0, 0</span></p>
<p>Rotation: <span class=""coord"" id=""rot-display"">0, 0, 0</span></p>
<p>Entities: <span class=""coord"" id=""entity-count"">0</span></p>
<p>Visible: <span class=""coord"" id=""visible-count"">0</span></p>
</div>
<script src=""https://cdnjs.cloudflare.com/ajax/libs/three.js/r128/three.min.js""></script>
<script src=""https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/loaders/GLTFLoader.js""></script>
<script>
let scene, camera, renderer, terrain;
let gltfLoader = null;
const loadedGLBModels = {};
let showDebug = false;
let prefabMarkers = [];
let prefabLabels = [];
let collisionMeshes = [];
let BBMarkers = [];
const prefabOffsetCache = new Map();
const prefabOffsetLoading = new Map();
const effectTimers = new Map();
let isPointerLocked = false;
const tempMatrix = new THREE.Matrix4();
const tempPos = new THREE.Vector3();
const tempDir = new THREE.Vector3();
const tempQuat = new THREE.Quaternion();
const tempScale = new THREE.Vector3();
let moveSpeed = 50;
let lookSpeed = 0.002;
const keys = { w: false, a: false, s: false, d: false, shift: false, space: false, c: false };
const euler = new THREE.Euler(0, 0, 0, 'YXZ');
let TERRAIN_SIZE = 1000;
let SEGMENTS = 512;
window.terrainLoaded = false;
let showRoads = true;
let showRails = true;
let showPrefabs = true;
let showWater = true;
let showBB = true;
let showLabels = true;
let showEntities = true;
let showCubes = true;
let showUnlimitedView = true;
let waterMesh = null;
let roadInstancer, railInstancer, riverInstancer, cubeInstancer;
let cubeCount = 0;
const glbFailedOnce = new Set();
const MAX_CUBE_PREFABS = 10000;
const debugEl = document.getElementById('debug-info');
const ROAD_HEIGHT_OFFSET = 0.5;
const RAIL_HEIGHT_OFFSET = 0.8;
const RIVER_HEIGHT_OFFSET = 0.3;
const SEGMENT_FORWARD = new THREE.Vector3(0, 0, 1);
const CHECKBOX_STATE_KEY = 'mapViewer.layerStates';
const isMobile = /Android|webOS|iPhone|iPad|iPod|BlackBerry|IEMobile|Opera Mini/i.test(navigator.userAgent) || ('ontouchstart' in window && navigator.maxTouchPoints > 0);
let mobileSpeedMultiplier = 1;
let joystickActive = false;
let joystickTouchId = null;
let joystickStartX = 0;
let joystickStartY = 0;
let joystickDeltaX = 0;
let joystickDeltaY = 0;
let lookTouchId = null;
let lastLookX = 0;
let lastLookY = 0;
let noClipMode = false;
let noClipSettings = {
walkSpeed: 5,
runSpeed: 10,
jumpHeight: 8.5,
duckHeight: 0.9,
eyeHeight: 1.55,
gravity: 21
};
let playerVelocity = new THREE.Vector3(0, 0, 0);
let isGrounded = true;
let isDucking = false;
let baseCameraY = 1.6;
const collisionRaycaster = new THREE.Raycaster();
const rayDownDirection = new THREE.Vector3(0, -1, 0);
function getVisualConfig(typeClass) {
const defaultConfig = { showAmmo: false, label: '' };
if (!typeClass) return defaultConfig;
const lower = typeClass.toLowerCase();
if (lower.includes('player')) return { showAmmo: false, label: 'Player' };
if (lower.includes('weapon')) return { showAmmo: true, label: 'Weapon' };
if (lower.includes('vehicle')) return { showAmmo: false, label: 'Vehicle' };
if (lower.includes('npc')) return { showAmmo: false, label: 'NPC' };
if (lower.includes('building') || lower.includes('block')) return { showAmmo: false, label: 'Building' };
if (lower.includes('item') || lower.includes('world')) return { showAmmo: false, label: 'Item' };
return defaultConfig;
}
const EXPLOSION_TYPES = {
4089790239: { name: 'F1 Grenade', color: 0xffaa00, size: 10, particles: 20 },
701725454:  { name: 'Beancan Grenade', color: 0xff9900, size: 12, particles: 25 },
3152512321: { name: 'Bee Grenade', color: 0xffcc00, size: 12, particles: 30 },
1872825961: { name: 'Flashbang', color: 0xffffff, size: 8, particles: 15 },
3053816283: { name: 'Rocket', color: 0xff4400, size: 15, particles: 40 },
2253809414: { name: 'Rocket HV', color: 0xff3300, size: 16, particles: 45 },
1437504946: { name: 'Rocket Incendiary', color: 0xff2200, size: 16, particles: 50 },
3591459072: { name: 'SAM Rocket', color: 0xff5500, size: 18, particles: 50 },
2513205737: { name: 'MLRS Air', color: 0xffdd00, size: 22, particles: 65 },
1161374517: { name: 'MLRS Ground', color: 0xffcc00, size: 22, particles: 65 },
3731256754: { name: 'Molotov', color: 0xff6600, size: 14, particles: 40 },
4150666752: { name: 'Fire', color: 0xff3300, size: 12, particles: 35 },
1448730840: { name: 'Gas Canister', color: 0xcccccc, size: 10, particles: 20 },
1289728008: { name: 'Satchel Charge', color: 0xffffff, size: 18, particles: 70 },
3028196029: { name: 'Landmine', color: 0xffbb00, size: 16, particles: 55 },
857997843:  { name: 'C4', color: 0xffffff, size: 18, particles: 80 },
1798302402: { name: 'Explosive Rifle Bullet', color: 0xff8800, size: 8, particles: 15 },
3702991035: { name: 'Generic Explosion', color: 0xffaa00, size: 14, particles: 35 },
1125943349: { name: 'Tube Launch', color: 0xffaa00, size: 10, particles: 20 },
227359892:  { name: 'Attack Helicopter', color: 0xff5500, size: 25, particles: 90 }
};
function isExplosionEffect(entityId) { return !!EXPLOSION_TYPES[entityId];}
function shouldShowEffect(effectId) { return EXPLOSION_TYPES.hasOwnProperty(effectId) || isExplosionEffect(effectId);}
const BBMarkerConfig = {
'underwater_lab': { size: [200, 100, 200], type: 'cube', opacity: 0.2 },
'entrance_bunker': { size: [100, 74, 100], type: 'cube', opacity: 0.2 },
'power_sub_small_2': { radius: 20, type: 'sphere', opacity: 0.2 },
'power_sub_small_1': { radius: 20, type: 'sphere', opacity: 0.2 },
'power_sub_big_2': { radius: 30, type: 'sphere', opacity: 0.2 },
'power_sub_big_1': { radius: 30, type: 'sphere', opacity: 0.2 },
'launch_site_1': { size: [775, 311, 476], type: 'cube', opacity: 0.2 },
'water_well_e': { radius: 24, type: 'sphere', opacity: 0.2 },
'water_well_d': { radius: 24, type: 'sphere', opacity: 0.2 },
'water_well_c': { radius: 24, type: 'sphere', opacity: 0.2 },
'water_well_b': { radius: 24, type: 'sphere', opacity: 0.2 },
'water_well_a': { radius: 24, type: 'sphere', opacity: 0.2 },
'stables_b': { radius: 85, type: 'sphere', opacity: 0.2 },
'stables_a': { radius: 85, type: 'sphere', opacity: 0.2 },
'sphere_tank': { radius: 130, type: 'sphere', opacity: 0.2 },
'satellite_dish': { radius: 170, type: 'sphere', opacity: 0.2 },
'mining_quarry': { radius: 70, type: 'sphere', opacity: 0.2 },
'warehouse': { radius: 100, type: 'sphere', opacity: 0.2 },
'supermarket_1': { radius: 120, type: 'sphere', opacity: 0.2 },
'radtown_1': { radius: 130, type: 'sphere', opacity: 0.2 },
'gas_station_1': { radius: 120, type: 'sphere', opacity: 0.2 },
'oilrig_2': { size: [128, 160, 128], type: 'cube', opacity: 0.2 },
'oilrig_1': { size: [128, 160, 138], type: 'cube', opacity: 0.2 },
'desert_military_base': { radius: 200, type: 'sphere', opacity: 0.2 },
'radtown_small_3': { size: [250, 64, 250], type: 'cube', opacity: 0.2 },
'nuclear_missile_silo': { radius: 170, type: 'sphere', opacity: 0.2 },
'junkyard_1': { radius: 170, type: 'sphere', opacity: 0.2 },
'compound': { radius: 200, type: 'sphere', opacity: 0.2 },
'bandit_town': { radius: 150, type: 'sphere', opacity: 0.2 },
'lighthouse': { radius: 100, type: 'sphere', opacity: 0.2 },
'water_treatment_plant_1': { size: [350, 165, 450], type: 'cube', opacity: 0.2 },
'trainyard_1': { size: [400, 200, 400], type: 'cube', opacity: 0.2 },
'powerplant_1': { radius: 210, type: 'sphere', opacity: 0.2 },
'military_tunnel_1': { radius: 210, type: 'sphere', opacity: 0.2 },
'excavator_1': { radius: 220, type: 'sphere', opacity: 0.2 },
'airfield_1': { size: [512, 85, 384], type: 'cube', opacity: 0.2 },
'jungle_ziggurat_a': { radius: 80, type: 'sphere', opacity: 0.2 },
'jungle_ruins_e': { radius: 32, type: 'sphere', opacity: 0.2 },
'jungle_ruins_d': { radius: 32, type: 'sphere', opacity: 0.2 },
'jungle_ruins_c': { radius: 32, type: 'sphere', opacity: 0.2 },
'jungle_ruins_b': { radius: 32, type: 'sphere', opacity: 0.2 },
'jungle_ruins_a': { radius: 32, type: 'sphere', opacity: 0.2 },
'harbor_2': { radius: 250, type: 'sphere', opacity: 0.2 },
'harbor_1': { radius: 250, type: 'sphere', opacity: 0.2 },
'ferry_terminal_1': { radius: 200, type: 'sphere', opacity: 0.2 },
'fishing_village_c': { radius: 125, type: 'sphere', opacity: 0.2 },
'fishing_village_b': { radius: 100, type: 'sphere', opacity: 0.2 },
'fishing_village_a': { radius: 125, type: 'sphere', opacity: 0.2 },
'cave_small_medium': { size: [60, 64, 60], type: 'cube', opacity: 0.2 },
'cave_small_hard': { size: [60, 64, 60], type: 'cube', opacity: 0.2 },
'cave_small_easy': { size: [60, 64, 60], type: 'cube', opacity: 0.2 },
'cave_medium_medium': { size: [130, 64, 100], type: 'cube', opacity: 0.2 },
'cave_medium_hard': { size: [120, 64, 130], type: 'cube', opacity: 0.2 },
'cave_medium_easy': { size: [100, 64, 80], type: 'cube', opacity: 0.2 },
'cave_large_sewers_hard': { size: [120, 64, 120], type: 'cube', opacity: 0.2 },
'cave_large_medium': { size: [100, 64, 130], type: 'cube', opacity: 0.2 },
'cave_large_hard': { size: [150, 64, 180], type: 'cube', opacity: 0.2 },
'arctic_research_base_a': { radius: 140, type: 'sphere', opacity: 0.2 }
};
const entitiesById = new Map();
const entityMeshes = new Map();
const entityLabels = new Map();
const entityTargets = new Map();
let lastEntityTimestamp = 0;
let entityUpdateInterval = null;
const ENTITY_UPDATE_INTERVAL = 100;
const ENTITY_LERP_SPEED = 10;
const entityPool = {
marker: [],
maxPoolSize: 500
};
const frustum = new THREE.Frustum();
const projScreenMatrix = new THREE.Matrix4();
const visibleEntities = new Set();
let lastFrustumUpdate = 0;
const FRUSTUM_UPDATE_INTERVAL = 100;
let visibilityDirty = true;
let lastVisibilityUpdate = 0;
const VISIBILITY_UPDATE_INTERVAL = 150;
const sharedMarkerMaterial = new THREE.MeshBasicMaterial({ color: 0x00ffff, opacity: 0.9, transparent: true });
const sharedMarkerGeometry = new THREE.BoxGeometry(3, 3, 3);
const MAX_CONCURRENT_EXPLOSIONS = 100;
let activeExplosionCount = 0;
const activeExplosions = new Set();
const explosionMaterialsCache = new Map();
const explosionGeometry = new THREE.SphereGeometry(0.1, 6, 6); 
function getPooledMarker() {
if (entityPool.marker.length > 0) {
return entityPool.marker.pop();
}
const marker = new THREE.Mesh(sharedMarkerGeometry, sharedMarkerMaterial);
return marker;
}
function returnToPool(marker) {
if (entityPool.marker.length < entityPool.maxPoolSize) {
scene.remove(marker);
entityPool.marker.push(marker);
}
}
function isBBMarkerType(prefabName) { return BBMarkerConfig.hasOwnProperty(prefabName); }
function getBBConfig(label) { return BBMarkerConfig[label] || null; }
function createBBSphereMarker(x, y, z, radius, label, opacity) {
opacity = opacity || 0.4;
const geometry = new THREE.SphereGeometry(radius, 16, 16);
const material = new THREE.MeshBasicMaterial({
color: 0xff0000,
transparent: true,
opacity: opacity,
side: THREE.DoubleSide,
depthWrite: false
});
const sphere = new THREE.Mesh(geometry, material);
sphere.position.set(x, y, z);
sphere.renderOrder = 10;
scene.add(sphere);
BBMarkers.push(sphere);
return sphere;
}
function createBBCubeMarker(x, y, z, sizeX, sizeY, sizeZ, rotX, rotY, rotZ, label, opacity) {
opacity = opacity || 0.35;
const geometry = new THREE.BoxGeometry(sizeX, sizeY, sizeZ);
const material = new THREE.MeshBasicMaterial({
color: 0xff0000,
transparent: true,
opacity: opacity,
side: THREE.DoubleSide,
depthWrite: false
});
const cube = new THREE.Mesh(geometry, material);
cube.position.set(x, y, z);
const eulerRot = new THREE.Euler(
THREE.MathUtils.degToRad(-rotX),
THREE.MathUtils.degToRad(-rotY),
THREE.MathUtils.degToRad(-rotZ),
'YXZ'
);
cube.rotation.copy(eulerRot);
cube.renderOrder = 10;
scene.add(cube);
BBMarkers.push(cube);
return cube;
}
function createBBMarkerFromPrefab(prefab) {
const pos = prefab.position || prefab.Position || prefab.pos;
if (!pos) { return; }
const rot = prefab.rotation || prefab.Rotation || prefab.rot || { x: 0, y: 0, z: 0 };
const prefabName = prefab.name || 'Unknown';
const config = getBBConfig(prefabName);
if (!config) { return; }
const x = pos[0] !== undefined ? pos[0] : (pos.x || 0);
const y = pos[1] !== undefined ? pos[1] : (pos.y || 0);
const z = pos[2] !== undefined ? -pos[2] : -(pos.z || 0);
const rotX = rot[0] !== undefined ? rot[0] : (rot.x || 0);
const rotY = rot[1] !== undefined ? rot[1] : (rot.y || 0);
const rotZ = rot[2] !== undefined ? rot[2] : (rot.z || 0);
switch (config.type) {
case 'cube':
let sizeX, sizeY, sizeZ;
if (Array.isArray(config.size)) {
sizeX = config.size[0];
sizeY = config.size[1];
sizeZ = config.size[2];
} else {
const s = config.radius || 50;
sizeX = s; sizeY = s; sizeZ = s;
}
createBBCubeMarker(x, y, z, sizeX, sizeY, sizeZ, rotX, rotY, rotZ, prefabName, config.opacity);
break;
case 'sphere':
default:
createBBSphereMarker(x, y, z, config.radius, prefabName, config.opacity);
break;
}
}
function loadLayerState() { try { return JSON.parse(localStorage.getItem(CHECKBOX_STATE_KEY)) || {}; } catch { return {}; } }
function saveLayerState(state) { localStorage.setItem(CHECKBOX_STATE_KEY, JSON.stringify(state)); }
function applyCheckboxState(id, defaultValue) {
const saved = loadLayerState();
const checkbox = document.getElementById(id);
const value = saved[id] !== undefined ? saved[id] : defaultValue;
checkbox.checked = value;
return value;
}
showRoads = applyCheckboxState('showRoads', true);
showRails = applyCheckboxState('showRails', true);
showPrefabs = applyCheckboxState('showPrefabs', true);
showBB = applyCheckboxState('showBB', true);
showWater = applyCheckboxState('showWater', true);
showLabels = applyCheckboxState('showLabels', true);
showDebug = applyCheckboxState('showDebug', false);
showEntities = applyCheckboxState('showEntities', true);
showCubes = applyCheckboxState('showCubes', true);
if (showDebug) { debugEl.classList.remove('hidden');} else { debugEl.classList.add('hidden');}
function applyInitialVisibility() {
if (roadInstancer) roadInstancer.visible = showRoads;
if (railInstancer) railInstancer.visible = showRails;
if (waterMesh) waterMesh.visible = showWater;
if (riverInstancer) riverInstancer.visible = showWater;
if (cubeInstancer) cubeInstancer.visible = showPrefabs && showCubes;
prefabMarkers.forEach(function(m) { m.visible = showPrefabs; });
BBMarkers.forEach(function(m) { m.visible = showBB; });
prefabLabels.forEach(function(m) { m.visible = showLabels; });
entityMeshes.forEach(function(mesh) { mesh.visible = showEntities; });
entityLabels.forEach(function(label) { label.visible = showEntities && showLabels; });
}
function log(message, type) {
type = type || 'success';
if (showDebug) {
console.log(message);
const className = type === 'error' ? 'error' : type === 'info' ? 'info' : 'success';
debugEl.innerHTML += '<div class=""' + className + '"">' + new Date().toLocaleTimeString() + ' - ' + message + '</div>';
debugEl.scrollTop = debugEl.scrollHeight;
}
}
function updateEntityCountDisplay() {
const count = entityMeshes.size;
document.getElementById('entity-count').textContent = count;
}
function updateVisibleCountDisplay() {
document.getElementById('visible-count').textContent = visibleEntities.size;
}
function base64ToInt16Array(base64) {
try {
const binaryString = atob(base64);
const len = binaryString.length;
const bytes = new Uint8Array(len);
for (let i = 0; i < len; i++) { bytes[i] = binaryString.charCodeAt(i); }
return new Int16Array(bytes.buffer);
} catch (e) {
log('Base64 decode error: ' + e.message, 'error');
return null;
}
}
async function decompressGzipBase64(base64) {
try {
const binaryString = atob(base64);
const len = binaryString.length;
const bytes = new Uint8Array(len);
for (let i = 0; i < len; i++) { bytes[i] = binaryString.charCodeAt(i); }
const ds = new DecompressionStream('gzip');
const decompressedStream = new Response(bytes).body.pipeThrough(ds);
const resultBuffer = await new Response(decompressedStream).arrayBuffer();
return resultBuffer;
} catch (e) { return null; }
}
async function decompressHeightmap(base64) {
const buffer = await decompressGzipBase64(base64);
if (!buffer) return null;
return new Int16Array(buffer);
}
async function decompressData(base64) {
const buffer = await decompressGzipBase64(base64);
if (!buffer) return null;
return new Uint8Array(buffer);
}
function downsampleHeightmap(data, srcRes, dstRes) {
const result = new Float32Array(dstRes * dstRes);
const ratio = (srcRes - 1) / (dstRes - 1);
const WATER_LEVEL_SHORT = 16336;
const MAX_DEVIATION_SHORT = 16336;
const MAX_DEVIATION_METERS = 500;
for (let y = 0; y < dstRes; y++) {
for (let x = 0; x < dstRes; x++) {
const srcX = x * ratio;
const srcY = y * ratio;
const x0 = Math.floor(srcX);
const y0 = Math.floor(srcY);
const x1 = Math.min(x0 + 1, srcRes - 1);
const y1 = Math.min(y0 + 1, srcRes - 1);
const fx = srcX - x0;
const fy = srcY - y0;
const h00 = data[y0 * srcRes + x0];
const h10 = data[y0 * srcRes + x1];
const h01 = data[y1 * srcRes + x0];
const h11 = data[y1 * srcRes + x1];
const rawHeight = (1 - fx) * (1 - fy) * h00 + fx * (1 - fy) * h10 + (1 - fx) * fy * h01 + fx * fy * h11;
result[y * dstRes + x] = (rawHeight - WATER_LEVEL_SHORT) / MAX_DEVIATION_SHORT * MAX_DEVIATION_METERS;
}
}
return result;
}
function createSegmentInstancer(maxSegments, width, height, color) {
const geometry = new THREE.BoxGeometry(width, height, 1);
const material = new THREE.MeshLambertMaterial({ color });
const instanced = new THREE.InstancedMesh(geometry, material, maxSegments);
instanced.instanceMatrix.setUsage(THREE.DynamicDrawUsage);
scene.add(instanced);
return instanced;
}
function createCubeInstancer(maxCubes) {
const geometry = new THREE.BoxGeometry(1, 1, 1);
const material = new THREE.MeshBasicMaterial({ color: 0x00ff00, opacity: 0.5, transparent: true });
const instanced = new THREE.InstancedMesh(geometry, material, maxCubes);
instanced.instanceMatrix.setUsage(THREE.DynamicDrawUsage);
instanced.count = 0;
scene.add(instanced);
return instanced;
}
function initInstancers(terrainData) {
const maxRoadSegs = terrainData.roads ? terrainData.roads.reduce(function(s, r) { return s + r.length; }, 0) : 0;
const maxRailSegs = terrainData.rail ? terrainData.rail.reduce(function(s, r) { return s + r.length; }, 0) : 0;
const maxRiverSegs = terrainData.river ? terrainData.river.reduce(function(s, r) { return s + r.length; }, 0) : 0;
roadInstancer = createSegmentInstancer(maxRoadSegs, 8, 0.5, 0x111111);
railInstancer = createSegmentInstancer(maxRailSegs, 3, 0.5, 0x3d2b1f);
riverInstancer = createSegmentInstancer(maxRiverSegs, 8, 0.2, 0x87CEEB);
cubeInstancer = createCubeInstancer(MAX_CUBE_PREFABS);
}
async function loadEntityOffsets(prefabName) {
const modelUrl = window.location.origin + '/models/' + prefabName + '.json';
try {
const response = await fetch(modelUrl);
if (!response.ok) throw new Error('Status: ' + response.status);
const data = await response.json();
return normalizeOffsets(data);
} catch (e) {
return normalizeOffsets(null);
}
}
function normalizeOffsets(data) {
const d = data || {};
return {
positionOffset: {
x: d.localPositionOffset?.x ?? d.positionOffset?.x ?? 0,
y: d.localPositionOffset?.y ?? d.positionOffset?.y ?? 0,
z: d.localPositionOffset?.z ?? d.positionOffset?.z ?? 0
},
rotationOffset: {
x: d.localRotationOffset?.x ?? d.rotationOffset?.x ?? 0,
y: d.localRotationOffset?.y ?? d.rotationOffset?.y ?? 0,
z: d.localRotationOffset?.z ?? d.rotationOffset?.z ?? 0
},
scaleMultiplier: {
x: d.localScaleOffset?.x ?? d.scaleMultiplier?.x ?? 1,
y: d.localScaleOffset?.y ?? d.scaleMultiplier?.y ?? 1,
z: d.localScaleOffset?.z ?? d.scaleMultiplier?.z ?? 1
}
};
}
function transformPositionFromUnity(x, y, z) {
return new THREE.Vector3(x, y, -z);
}
function transformRotationFromUnity(rotX, rotY, rotZ) {
return { x: -rotX, y: -rotY, z: -rotZ };
}
function updateLabelDisplay(label) {
const info = getEntityLabelInfo(label);
if (label.infoElement) {label.infoElement.textContent = info;}
if (label.userData.healthPct !== undefined) {
const pct = label.userData.healthPct;
label.healthBar.style.width = `${pct * 100}%`;
label.healthBar.style.backgroundColor = getHealthColor(pct);
}
}
function applyEntityTransform(obj, unityX, unityY, unityZ, unityRotX, unityRotY, unityRotZ, offsets) {
const normalizedPos = transformPositionFromUnity(unityX || 0, unityY || 0, unityZ || 0);
const rotOffset = offsets?.rotationOffset ?? offsets?.localRotationOffset ?? { x: 0, y: 0, z: 0 };
const posOffset = offsets?.positionOffset ?? offsets?.localPositionOffset ?? { x: 0, y: 0, z: 0 };
const scaleOff = offsets?.scaleMultiplier ?? offsets?.localScaleOffset ?? { x: 1, y: 1, z: 1 };
const baseRotX = unityRotX || 0;
const baseRotY = unityRotY || 0;
const baseRotZ = unityRotZ || 0;
const combinedRotX = baseRotX + (rotOffset.x || 0);
const combinedRotY = baseRotY + (rotOffset.y || 0);
const combinedRotZ = baseRotZ + (rotOffset.z || 0);
const normalizedRot = {
x: combinedRotX,
y: 180 - combinedRotY,
z: combinedRotZ
};
const normalizedEuler = new THREE.Euler(
THREE.MathUtils.degToRad(normalizedRot.x),
THREE.MathUtils.degToRad(normalizedRot.y),
THREE.MathUtils.degToRad(normalizedRot.z),
'YXZ'
);
const normalizedQuat = new THREE.Quaternion().setFromEuler(normalizedEuler);
let localOffset = new THREE.Vector3(0, 0, 0);
if (posOffset) {
localOffset.set(posOffset.x || 0, posOffset.y || 0, posOffset.z || 0);
localOffset.applyQuaternion(normalizedQuat);
}
const finalPosition = normalizedPos.clone().add(localOffset);
obj.position.copy(finalPosition);
obj.quaternion.copy(normalizedQuat);
const glbScale = new THREE.Vector3();
const glbQuat = new THREE.Quaternion();
const glbPos = new THREE.Vector3();
obj.matrixWorld.decompose(glbPos, glbQuat, glbScale);
const scaleX = glbScale.x * (scaleOff.x || 1);
const scaleY = glbScale.y * (scaleOff.y || 1);
const scaleZ = glbScale.z * (scaleOff.z || 1);
obj.scale.set(scaleX, scaleY, scaleZ);
}
const LABEL_CREATION_DISTANCE = 1000;
function addEntityLabel(prefabName, x, y, z, isPlayer = false) {
const canvas = document.createElement('canvas');
const ctx = canvas.getContext('2d');
canvas.width = 512;
canvas.height = 200;
ctx.fillStyle = isPlayer ? 'rgba(40, 20, 0, 0.9)' : 'rgba(0, 30, 60, 0.9)';
ctx.fillRect(0, 0, 512, 200);
ctx.strokeStyle = isPlayer ? '#ffaa00' : '#00ffff';
ctx.lineWidth = 3;
ctx.strokeRect(1, 1, 510, 198);
ctx.fillStyle = isPlayer ? '#ffaa00' : '#00ffff';
ctx.font = 'bold 32px Arial';
ctx.textAlign = 'center';
ctx.fillText(prefabName, 256, 42);
const healthBarY = 60;
const healthBarHeight = 24;
const healthBarWidth = 400;
const healthBarX = (512 - healthBarWidth) / 2;
ctx.fillStyle = 'rgba(50, 50, 50, 0.8)';
ctx.fillRect(healthBarX, healthBarY, healthBarWidth, healthBarHeight);
ctx.fillStyle = '#4CAF50';
ctx.fillRect(healthBarX, healthBarY, 0, healthBarHeight);
ctx.strokeStyle = '#ffffff';
ctx.lineWidth = 2;
ctx.strokeRect(healthBarX, healthBarY, healthBarWidth, healthBarHeight);
ctx.fillStyle = '#ffffff';
ctx.font = 'bold 18px Arial';
ctx.textAlign = 'center';
ctx.fillText('HP: --/--', 256, healthBarY + 18);
ctx.font = '20px Arial';
ctx.textAlign = 'left';
const statsStartY = 105;
const lineHeight = 28;
if (isPlayer) {
ctx.textAlign = 'center';
ctx.fillText('Stamina: --', 256, statsStartY);
ctx.fillText('Food: --  |  Water: --', 256, statsStartY + lineHeight);
}
ctx.textAlign = 'right';
ctx.fillText('Grade: --', 492, statsStartY);
ctx.fillText('Stack: --', 492, statsStartY + lineHeight);
const texture = new THREE.CanvasTexture(canvas);
texture.minFilter = THREE.LinearFilter;
const spriteMat = new THREE.SpriteMaterial({ map: texture, depthTest: false, sizeAttenuation: true, transparent: true });
const sprite = new THREE.Sprite(spriteMat);
sprite.position.set(x, y + 8, z);
sprite.scale.set(14, 5.5, 1);
sprite.visible = showLabels && showEntities;
sprite.userData.entityName = prefabName;
sprite.userData.labelOffset = 8;
sprite.userData.isPlayer = isPlayer;
sprite.userData.healthPct = 0;
sprite.userData.health = 0;
sprite.userData.maxHealth = 0;
sprite.userData.stamina = null;
sprite.userData.food = null;
sprite.userData.water = null;
sprite.userData.stackCount = null;
sprite.userData.construction = null;
sprite.userData.canvas = canvas;
sprite.userData.ctx = ctx;
scene.add(sprite);
return sprite;
}
function updateEntityLabelInfo(sprite) {
if (!sprite || !sprite.userData) return;
const ud = sprite.userData;
const canvas = ud.canvas;
const ctx = ud.ctx;
const isPlayer = ud.isPlayer;
if (!canvas || !ctx) return;
ctx.clearRect(0, 0, canvas.width, canvas.height);
ctx.fillStyle = isPlayer ? 'rgba(40, 20, 0, 0.9)' : 'rgba(0, 30, 60, 0.9)';
ctx.fillRect(0, 0, 512, 200);
ctx.strokeStyle = isPlayer ? '#ffaa00' : '#00ffff';
ctx.lineWidth = 3;
ctx.strokeRect(1, 1, 510, 198);
const name = ud.entityName || 'Entity';
ctx.fillStyle = isPlayer ? '#ffaa00' : '#00ffff';
ctx.font = 'bold 32px Arial';
ctx.textAlign = 'center';
ctx.fillText(name, 256, 42);
const healthBarY = 60;
const healthBarHeight = 24;
const healthBarWidth = 400;
const healthBarX = (512 - healthBarWidth) / 2;
ctx.fillStyle = 'rgba(50, 50, 50, 0.8)';
ctx.fillRect(healthBarX, healthBarY, healthBarWidth, healthBarHeight);
const healthPct = Math.max(0, Math.min(1, ud.healthPct || 0));
let barColor = '#4CAF50';
if (healthPct <= 0.3) barColor = '#F44336';
else if (healthPct <= 0.6) barColor = '#FF9800';
ctx.fillStyle = barColor;
ctx.fillRect(healthBarX, healthBarY, healthBarWidth * healthPct, healthBarHeight);
ctx.strokeStyle = '#ffffff';
ctx.lineWidth = 2;
ctx.strokeRect(healthBarX, healthBarY, healthBarWidth, healthBarHeight);
const health = Math.round(ud.health || 0);
const maxHealth = Math.round(ud.maxHealth || 0);
ctx.fillStyle = '#ffffff';
ctx.font = 'bold 18px Arial';
ctx.textAlign = 'center';
if (maxHealth > 0) {
ctx.fillText('HP: ' + health + '/' + maxHealth + ' (' + Math.round(healthPct * 100) + '%)', 256, healthBarY + 18);
} else if (health > 0) {
ctx.fillText('HP: ' + health, 256, healthBarY + 18);
} else {
ctx.fillText('HP: N/A', 256, healthBarY + 18);
}
ctx.font = '20px Arial';
const statsStartY = 105;
const lineHeight = 28;
ctx.textAlign = 'left';
if (isPlayer) {
ctx.textAlign = 'center';
const stamina = ud.stamina !== null && ud.stamina !== undefined ? Math.round(ud.stamina) : '--';
ctx.fillText('Stamina: ' + stamina, 256, statsStartY);
const food = ud.food !== null && ud.food !== undefined ? Math.round(ud.food) : '--';
const water = ud.water !== null && ud.water !== undefined ? Math.round(ud.water) : '--';
ctx.fillText('Food: ' + food + '  |  Water: ' + water, 256, statsStartY + lineHeight);
}
if (ud.construction !== null && ud.construction !== undefined) {
ctx.textAlign = 'right';
const gradeNames = ['Twigs', 'Wood', 'Stone', 'Metal', 'HQM'];
const grade = Math.round(ud.construction);
const gradeName = grade >= 0 && grade < gradeNames.length ? gradeNames[grade] : 'Grade ' + grade;
ctx.fillText('Grade: ' + gradeName, 492, statsStartY);
}
if (ud.stackCount !== null && ud.stackCount !== undefined) {
ctx.textAlign = 'right';
ctx.fillText('Stack: ' + ud.stackCount, 492, statsStartY + lineHeight);
}
if (sprite.material.map) {
sprite.material.map.needsUpdate = true;
}
}
function getEntityLabelInfo(label) {
if (!label || !label.userData) {return '';}
const ud = label.userData;
const lines = [];
if (ud.maxHealth > 0) {
const healthNum = Math.round(ud.health || 0);
const maxHealthNum = Math.round(ud.maxHealth || 0);
lines.push(`HP: ${healthNum}/${maxHealthNum}`);
}
if (ud.stamina !== null && ud.stamina !== undefined) {
lines.push(`Stamina: ${Math.round(ud.stamina)}`);
}
if (ud.food !== null && ud.food !== undefined) {
lines.push(`Food: ${Math.round(ud.food)}`);
}
if (ud.water !== null && ud.water !== undefined) {
lines.push(`Water: ${Math.round(ud.water)}`);
}
if (ud.stackCount !== null && ud.stackCount !== undefined) {
lines.push(`Stack: ${ud.stackCount}`);
}
if (ud.construction !== null && ud.construction !== undefined) {
const gradeNames = ['Twigs', 'Wood', 'Stone', 'Metal', 'HQM'];
const grade = Math.round(ud.construction);
const gradeName = grade >= 0 && grade < gradeNames.length ? gradeNames[grade] : `Grade ${grade}`;
lines.push(`Grade: ${gradeName}`);
}
if (ud.npcClass) {
lines.push(`Type: ${ud.npcClass}`);
}
return lines.join('\n');
}
function getHealthColor(pct) {
if (pct > 0.6) return '#4CAF50';
if (pct > 0.3) return '#FF9800';
return '#F44336';
}
async function spawnEntity(entityData) {
const entityId = entityData.id ?? entityData.Id;
const prefabName = (entityData.PrefabName || entityData.Prefab || entityData.prefabName || 'Unknown').replace(/\.prefab$/i, '');
const isDestroyed = entityData.isdestroyed === true || entityData.isdestroyed === 1 || entityData.isdestroyed === ""true"";
if (isDestroyed) {
if (entityMeshes.has(entityId)) {
despawnEntity(entityId);
}
return;
}
if (entityMeshes.has(entityId)) {
updateEntityPosition(entityData);
return;
}
const pos = entityData.pos || entityData.Position || entityData.position || { x: 0, y: 0, z: 0 };
const rot = entityData.rot || entityData.Rotation || entityData.rotation || { x: 0, y: 0, z: 0 };
const unityX = pos.X !== undefined ? pos.X : (pos.x !== undefined ? pos.x : (pos[0] || 0));
const unityY = pos.Y !== undefined ? pos.Y : (pos.y !== undefined ? pos.y : (pos[1] || 0));
const unityZ = pos.Z !== undefined ? pos.Z : (pos.z !== undefined ? pos.z : (pos[2] || 0));
const unityRotX = rot.X !== undefined ? rot.X : (rot.x !== undefined ? rot.x : (rot[0] || 0));
const unityRotY = rot.Y !== undefined ? rot.Y : (rot.y !== undefined ? rot.y : (rot[1] || 0));
const unityRotZ = rot.Z !== undefined ? rot.Z : (rot.z !== undefined ? rot.z : (rot[2] || 0));
const typeClass = entityData.typeClass || entityData.TypeClass || '';
const maxHealth = entityData.maxHealth ?? entityData.MaxHealth ?? 0;
const ownerId = entityData.ownerId ?? entityData.OwnerId ?? 0;
const playerName = entityData.playerName || entityData.Playername || '';
const visual = getVisualConfig(typeClass);
const healthPct = maxHealth > 0 ? Math.min(entityData.health / maxHealth, 1) : (entityData.health > 0 ? 1 : 0);
const currentHealth = entityData.health ?? 0;
let labelName = playerName || visual.label || prefabName;
const stamina = entityData.extra?.stamina ?? entityData.stamina ?? null;
const food = entityData.extra?.food ?? entityData.food ?? null;
const water = entityData.extra?.water ?? entityData.water ?? null;
const itemId = entityData.extra?.itemID ?? entityData.itemID ?? null;
const stackCount = entityData.extra?.stackCount ?? entityData.stackCount ?? null;
const construction = entityData.extra?.construction ?? entityData.construction ?? null;
const npcClass = entityData.extra?.npcClass ?? entityData.npcClass ?? null;
const modelUrl = window.location.origin + '/models/' + prefabName + '.glb';
let offsets;
try {
offsets = await loadPrefabOffset(prefabName);
} catch (err) {
console.error('Failed to load offsets for ' + prefabName + ', using defaults.', err);
offsets = { positionOffset: { x: 0, y: 0, z: 0 }, rotationOffset: { x: 0, y: 0, z: 0 }, scaleMultiplier: { x: 1, y: 1, z: 1 } };
}
const isPlayerEntity = playerName.length > 0 || typeClass.toLowerCase().includes('player') || typeClass.toLowerCase().includes('human');
if (loadedGLBModels[modelUrl]) {
const cached = loadedGLBModels[modelUrl];
const model = cached.scene.clone();
applyEntityTransform(model, unityX, unityY, unityZ, unityRotX, unityRotY, unityRotZ, offsets);
model.visible = showEntities;
model.userData.isEntity = true;
model.userData.needsLabel = true;
model.userData.isPlayer = isPlayerEntity;
scene.add(model);
entityMeshes.set(entityId, model);
entitiesById.set(entityId, entityData);
const markerPos = transformPositionFromUnity(unityX, unityY, unityZ);
const label = addEntityLabel(labelName, markerPos.x, markerPos.y, markerPos.z, isPlayerEntity);
label.userData.healthPct = healthPct;
label.userData.health = currentHealth;
label.userData.maxHealth = maxHealth;
label.userData.stamina = stamina;
label.userData.food = food;
label.userData.water = water;
label.userData.stackCount = stackCount;
label.userData.construction = construction;
label.userData.npcClass = npcClass;
label.userData.entityName = labelName;
entityLabels.set(entityId, label);
updateEntityLabelInfo(label);
updateEntityCountDisplay();
visibilityDirty = true;
return;
}
await new Promise((resolve, reject) => {
loadGLBModel(modelUrl, function(gltf) {
const model = gltf.scene;
loadedGLBModels[modelUrl] = gltf;
applyEntityTransform(model, unityX, unityY, unityZ, unityRotX, unityRotY, unityRotZ, offsets);
model.visible = showEntities;
model.userData.isEntity = true;
model.userData.needsLabel = true;
model.userData.isPlayer = isPlayerEntity;
scene.add(model);
entityMeshes.set(entityId, model);
entitiesById.set(entityId, entityData);
const markerPos = transformPositionFromUnity(unityX, unityY, unityZ);
const label = addEntityLabel(labelName, markerPos.x, markerPos.y, markerPos.z, isPlayerEntity);
label.userData.healthPct = healthPct;
label.userData.health = currentHealth;
label.userData.maxHealth = maxHealth;
label.userData.stamina = stamina;
label.userData.food = food;
label.userData.water = water;
label.userData.stackCount = stackCount;
label.userData.construction = construction;
label.userData.npcClass = npcClass;
label.userData.entityName = labelName;
entityLabels.set(entityId, label);
updateEntityLabelInfo(label);
updateEntityCountDisplay();
visibilityDirty = true;
resolve();
}, function(error) {
const marker = getPooledMarker();
marker.visible = showEntities;
marker.userData.isEntity = true;
marker.userData.needsLabel = true;
marker.userData.isPlayer = isPlayerEntity;
scene.add(marker);
entityMeshes.set(entityId, marker);
const markerPos = transformPositionFromUnity(unityX, unityY, unityZ);
const label = addEntityLabel(labelName, markerPos.x, markerPos.y, markerPos.z, isPlayerEntity);
label.userData.healthPct = healthPct;
label.userData.health = currentHealth;
label.userData.maxHealth = maxHealth;
label.userData.stamina = stamina;
label.userData.food = food;
label.userData.water = water;
label.userData.stackCount = stackCount;
label.userData.construction = construction;
label.userData.npcClass = npcClass;
label.userData.entityName = labelName;
entityLabels.set(entityId, label);
updateEntityLabelInfo(label);
entitiesById.set(entityId, entityData);
updateEntityCountDisplay();
marker.position.copy(markerPos);
visibilityDirty = true;
resolve();
});
});
}
function updateEntityPosition(entityData) {
const entityId = entityData.Id || entityData.id;
const mesh = entityMeshes.get(entityId);
const label = entityLabels.get(entityId);
if (!mesh) return;
const isDestroyed = entityData.isdestroyed === true || entityData.isdestroyed === 1 || entityData.isdestroyed === ""true"";
if (isDestroyed) {
despawnEntity(entityId);
return;
}
const prefabName = (entityData.PrefabName || entityData.Prefab || entityData.prefabName || 'Unknown').replace(/(\.corpse)?(\.prefab)$/i, '');
const offsets = prefabOffsetCache.get(prefabName) || {positionOffset: { x: 0, y: 0, z: 0 },rotationOffset: { x: 0, y: 0, z: 0 },scaleMultiplier: { x: 1, y: 1, z: 1 }};
const pos = entityData.pos || entityData.Position || entityData.position || { x: 0, y: 0, z: 0 };
const rot = entityData.rot || entityData.Rotation || entityData.rotation || { x: 0, y: 0, z: 0 };
const unityX = pos.X !== undefined ? pos.X : (pos.x !== undefined ? pos.x : (pos[0] || 0));
const unityY = pos.Y !== undefined ? pos.Y : (pos.y !== undefined ? pos.y : (pos[1] || 0));
const unityZ = pos.Z !== undefined ? pos.Z : (pos.z !== undefined ? pos.z : (pos[2] || 0));
const unityRotX = rot.X !== undefined ? rot.X : (rot.x !== undefined ? rot.x : (rot[0] || 0));
const unityRotY = rot.Y !== undefined ? rot.Y : (rot.y !== undefined ? rot.y : (rot[1] || 0));
const unityRotZ = rot.Z !== undefined ? rot.Z : (rot.z !== undefined ? rot.z : (rot[2] || 0));
const posOffset = offsets?.positionOffset ?? offsets?.localPositionOffset ?? { x: 0, y: 0, z: 0 };
const adjustedX = unityX + (posOffset.x || 0);
const adjustedY = unityY + (posOffset.y || 0);
const adjustedZ = unityZ + (posOffset.z || 0);
const targetPos = transformPositionFromUnity(adjustedX, adjustedY, adjustedZ);
const combinedRotX = unityRotX;
const combinedRotY = unityRotY;
const combinedRotZ = unityRotZ;
const normalizedRot = {
x: combinedRotX,
y: 180 - combinedRotY,
z: combinedRotZ
};
const normalizedEuler = new THREE.Euler(
THREE.MathUtils.degToRad(normalizedRot.x),
THREE.MathUtils.degToRad(normalizedRot.y),
THREE.MathUtils.degToRad(normalizedRot.z),
'YXZ'
);
const targetQuat = new THREE.Quaternion().setFromEuler(normalizedEuler);
entityTargets.set(entityId, {
position: targetPos,
quaternion: targetQuat,
labelOffset: 5
});
entitiesById.set(entityId, entityData);
if (label) {
const maxHealth = entityData.maxHealth ?? entityData.MaxHealth ?? 0;
const healthPct = maxHealth > 0 ? Math.min((entityData.health ?? 0) / maxHealth, 1)  : ((entityData.health ?? 0) > 0 ? 1 : 0);
const stamina = entityData.extra?.stamina ?? entityData.stamina ?? null;
const food = entityData.extra?.food ?? entityData.food ?? null;
const water = entityData.extra?.water ?? entityData.water ?? null;
const stackCount = entityData.extra?.stackCount ?? entityData.stackCount ?? null;
const construction = entityData.extra?.construction ?? entityData.construction ?? null;
label.userData.healthPct = healthPct;
label.userData.health = entityData.health ?? 0;
label.userData.maxHealth = maxHealth;
label.userData.stamina = stamina;
label.userData.food = food;
label.userData.water = water;
label.userData.stackCount = stackCount;
label.userData.construction = construction;
updateEntityLabelInfo(label);
}
visibilityDirty = true;
}
function despawnEntity(entityId) {
const mesh = entityMeshes.get(entityId);
if (mesh && mesh.userData && mesh.userData.isExplosion) {
activeExplosions.delete(entityId);
activeExplosionCount = Math.max(0, activeExplosionCount - 1);
if (mesh.userData) mesh.userData.expired = true;
}
const label = entityLabels.get(entityId);
if (mesh || entityMeshes.has(entityId)) {
if (mesh && mesh.userData && mesh.userData.isEntity) {
if (mesh.geometry === sharedMarkerGeometry) {
returnToPool(mesh);
} else {
scene.remove(mesh);
if (mesh.geometry && mesh.geometry !== sharedMarkerGeometry && mesh.geometry !== explosionGeometry) mesh.geometry.dispose();
if (mesh.material) {
if (Array.isArray(mesh.material)) mesh.material.forEach(m => m.dispose());
else if (mesh.material && !mesh.material.isSharedMaterial) mesh.material.dispose();
}
}
}
}
entityMeshes.delete(entityId);
if (label || entityLabels.has(entityId)) {
if (label) {
scene.remove(label);
if (label.material && label.material.map) label.material.map.dispose();
if (label.material) label.material.dispose();
}
entityLabels.delete(entityId);
}
entityTargets.delete(entityId);
entitiesById.delete(entityId);
visibleEntities.delete(entityId);
effectTimers.delete(entityId);
updateEntityCountDisplay();
visibilityDirty = true;
}

function updateFrustumCulling() {
if (Date.now() - lastFrustumUpdate < FRUSTUM_UPDATE_INTERVAL) return;
lastFrustumUpdate = Date.now();
camera.updateMatrixWorld();
projScreenMatrix.multiplyMatrices(camera.projectionMatrix, camera.matrixWorldInverse);
frustum.setFromProjectionMatrix(projScreenMatrix);
}
function isInFrustum(position) {
return frustum.containsPoint(position);
}
function updateEntityVisibility() {
if (!visibilityDirty && Date.now() - lastVisibilityUpdate < VISIBILITY_UPDATE_INTERVAL) return;
lastVisibilityUpdate = Date.now();
visibilityDirty = false;
visibleEntities.clear();
entityMeshes.forEach(function(mesh, entityId) {
const target = entityTargets.get(entityId);
const entityData = entitiesById.get(entityId);
const pos = target ? target.position : mesh.position;
const inFrustum = isInFrustum(pos);
const distanceToCamera = camera.position.distanceTo(pos);
const isPlayer = mesh.userData?.isPlayer || false;
if (!isPlayer) {
const playerName = entityData?.playerName || entityData?.Playername || entityData?.playername || '';
const typeClass = entityData?.typeClass || entityData?.TypeClass || '';
if (playerName.length > 0 || typeClass.toLowerCase().includes('player') || typeClass.toLowerCase().includes('human')) {
mesh.userData.isPlayer = true;
}
}
const isVisible = showEntities && inFrustum && (isPlayer || distanceToCamera < 800);
mesh.visible = isVisible;
if (isVisible) {
visibleEntities.add(entityId);
const label = entityLabels.get(entityId);
if (label) {
label.visible = showLabels && (isPlayer || distanceToCamera < LABEL_CREATION_DISTANCE);
}
} else {
const label = entityLabels.get(entityId);
if (label) {label.visible = false;}
}
});
updateVisibleCountDisplay();
}
async function loadAllEntities() {
try {
const response = await fetch('/3dmap/entities/" + WipeID + @"');
if (!response.ok) throw new Error('HTTP ' + response.status);
const data = await response.json();
if (data.entities && Array.isArray(data.entities)) {
lastEntityTimestamp = data.timestamp || 0;
log('Loading ' + data.entities.length + ' entities...', 'info');
for (const entity of data.entities) {
await spawnEntity(entity);
}
log('Loaded ' + entityMeshes.size + ' entities', 'success');
}
if (data.effects && Array.isArray(data.effects)) {
log('Processing ' + data.effects.length + ' effects...', 'info');
for (const effect of data.effects) {
await spawnEffectWithTTL(effect);
}
}
} catch (error) {
log('Failed to load entities: ' + error.message, 'error');
}
}
function getSharedExplosionMaterial(color) {
if (!explosionMaterialsCache.has(color)) {
explosionMaterialsCache.set(color, new THREE.MeshBasicMaterial({
color: color,
transparent: true,
opacity: 1
}));
}
return explosionMaterialsCache.get(color);
}
function spawnEffectWithTTL(effect, ttlMs = 1000) {
const effectId = effect.id ?? effect.Id;
if (!effectId){return;}
if (entityMeshes.has(effectId)) { return; }
if (activeExplosionCount >= MAX_CONCURRENT_EXPLOSIONS) {
const oldestId = activeExplosions.values().next().value;
if (oldestId) despawnEntity(oldestId);
}
if (effectTimers.has(effectId)) {
clearTimeout(effectTimers.get(effectId));
effectTimers.delete(effectId);
}
const config = EXPLOSION_TYPES[effectId] || { color: 0xffff00, size: 2, particles: 15 };
const explosionGroup = createExplosionParticles(config);
const pos = effect.pos || { x: 0, y: 0, z: 0 };
const worldPos = transformPositionFromUnity(pos.x, pos.y, pos.z);
explosionGroup.position.set(worldPos.x, worldPos.y, worldPos.z);
scene.add(explosionGroup);
entityMeshes.set(effectId, explosionGroup);
activeExplosions.add(effectId);
activeExplosionCount++;
const timer = setTimeout(() => {despawnEntity(effectId);}, ttlMs);
effectTimers.set(effectId, timer);
}
function createExplosionParticles(config) {
const group = new THREE.Group();
const particleCount = config.particles;
const sharedMaterial = getSharedExplosionMaterial(config.color);
for (let i = 0; i < particleCount; i++) {
const p = new THREE.Mesh(explosionGeometry, sharedMaterial);
p.userData.velocity = new THREE.Vector3(
(Math.random() - 0.5) * config.size * 0.2,
(Math.random() - 0.5) * config.size * 0.2,
(Math.random() - 0.5) * config.size * 0.2
);
group.add(p);
}
group.userData.isEntity = true;
group.userData.isExplosion = true;
group.userData.startTime = Date.now();
group.userData.config = config;
group.userData.expired = false;
return group;
}
function animateEffects() {
if (activeExplosions.size === 0) return;
const now = Date.now();
activeExplosions.forEach(function(effectId) {
const group = entityMeshes.get(effectId);
if (!group || !group.userData || group.userData.expired) return;
if (!(group instanceof THREE.Group) || group.children.length === 0) return;
let allFaded = true;
const opacityDecay = 0.04;
const scaleDecay = 0.98;
for (let i = 0; i < group.children.length; i++) {
const p = group.children[i];
p.position.add(p.userData.velocity);
if (p.material.opacity > 0.02) {
p.material.opacity *= (1 - opacityDecay);
p.scale.multiplyScalar(scaleDecay);
allFaded = false;
}
}
if (allFaded) {
group.userData.expired = true;
}
});
}
async function pollEntityUpdates() {
try {
const cx = camera.position.x, cy = camera.position.y, cz = -camera.position.z;
const url = '/3dmap/update/" + WipeID + @"?cx=' + cx + '&cy=' + cy + '&cz=' + cz + '&unlimited=' + showUnlimitedView;
const response = await fetch(url);
if (!response.ok) throw new Error('HTTP ' + response.status);
const data = await response.json();
if (data.entities && Array.isArray(data.entities)) {
for (const entity of data.entities) {
const entityId = entity.id ?? entity.Id;
const isDestroyed = entity.isdestroyed === true || entity.isdestroyed === 1 || String(entity.isdestroyed) === ""true"";
if (isDestroyed) { despawnEntity(entityId); continue; }
spawnEntity(entity);
}
}
if (data.removedIds && Array.isArray(data.removedIds)) {
for (const removedId of data.removedIds) despawnEntity(removedId);
}
if (data.effects && Array.isArray(data.effects)) {
for (const effect of data.effects) {
const effectId = effect.id ?? effect.Id;
const isDestroyed = effect.isdestroyed === true || String(effect.isdestroyed) === ""true"";
if (isDestroyed) despawnEntity(effectId);
else if (shouldShowEffect(effectId)) spawnEffectWithTTL(effect, 5000);
}
}
visibilityDirty = true;
} catch (error) { log('Entity update error: ' + error.message, 'error'); }
}
function startEntityUpdates() {
if (entityUpdateInterval) return;
loadAllEntities().then(() => {
entityUpdateInterval = setInterval(pollEntityUpdates, ENTITY_UPDATE_INTERVAL);
});
}
function stopEntityUpdates() {
if (entityUpdateInterval) {
clearInterval(entityUpdateInterval);
entityUpdateInterval = null;
}
}
async function init() {
scene = new THREE.Scene();
scene.background = new THREE.Color(0x87CEEB);
scene.fog = new THREE.Fog(0x87CEEB, 1000, 4000);
camera = new THREE.PerspectiveCamera(75, window.innerWidth / window.innerHeight, 0.1, 9999);
camera.position.set(0, 300, 0);
camera.lookAt(0, 0, -1);
renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setSize(window.innerWidth, window.innerHeight);
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 1.5));
renderer.info.autoReset = false;
document.getElementById('canvas-container').appendChild(renderer.domElement);
const ambientLight = new THREE.AmbientLight(0xffffff, 0.6);
scene.add(ambientLight);
const directionalLight = new THREE.DirectionalLight(0xffffff, 0.8);
directionalLight.position.set(100, 200, 100);
scene.add(directionalLight);
initGLTFLoader();
await loadTerrain();
applyInitialVisibility();
camera.position.y = Math.max(300, TERRAIN_SIZE * 0.15);
setupControls();
setupMobileControls();
setupNoclipButton();
setupLayerToggleHandlers();
if (isMobile) {
const debugLabel = document.querySelector('#layer-toggle label input#showDebug')?.parentElement;
if (debugLabel) debugLabel.style.display = 'none';
}
startEntityUpdates();
checkLoadingComplete();
animate();
}
function checkLoadingComplete() {
if (window.terrainLoaded) {
document.getElementById('loading').classList.add('hidden');
} else {
setTimeout(checkLoadingComplete, 60);
}
}
async function loadTerrain() {
try {
const response = await fetch('/3dmap/data/" + WipeID + @"');
if (!response.ok) { throw new Error('HTTP ' + response.status); }
const terrainData = await response.json();
log('Received: worldSize=' + terrainData.worldSize, 'info');
TERRAIN_SIZE = terrainData.worldSize || 1000;
SEGMENTS = terrainData.heightMapResolution || 512;
if (SEGMENTS < 512) SEGMENTS = 512;
let rawHeights = await decompressHeightmap(terrainData.heightmap);
if (!rawHeights) {
rawHeights = base64ToInt16Array(terrainData.heightmap);
window.terrainLoaded = true;
createDemoTerrain();
return;
}
log('Decoded ' + rawHeights.length + ' height values');
initInstancers(terrainData);
const srcRes = terrainData.heightMapResolution;
const dstRes = SEGMENTS + 1;
let heights;
if (srcRes !== dstRes) {
heights = downsampleHeightmap(rawHeights, srcRes, dstRes);
} else {
const WATER_LEVEL_SHORT = 16336;
const MAX_DEVIATION_SHORT = 16336;
const MAX_DEVIATION_METERS = 500;
heights = new Float32Array(rawHeights.length);
for (let i = 0; i < rawHeights.length; i++) { heights[i] = (rawHeights[i] - WATER_LEVEL_SHORT) / MAX_DEVIATION_SHORT * MAX_DEVIATION_METERS; }
}
let splatData = await decompressData(terrainData.splatmap);
window.terrainHeights = heights;
window.terrainResolution = dstRes;
window.splatData = splatData;
window.splatResolution = terrainData.splatMapResolution;
window.terrainLoaded = true;
createTerrainMesh(heights, terrainData.splatColors);
createWaterMesh();
if (terrainData.roads && terrainData.roads.length > 0) { drawRoadsFromServer(terrainData.roads); }
if (terrainData.rail && terrainData.rail.length > 0) { drawRailsFromServer(terrainData.rail); }
if (terrainData.prefabs && terrainData.prefabs.length > 0) { drawPrefabs(terrainData.prefabs); }
if (terrainData.river && terrainData.river.length > 0) { drawRiverFromServer(terrainData.river); }
} catch (error) {
log('Failed to load terrain: ' + error.message, 'error');
createDemoTerrain();
window.terrainLoaded = true;
}
}
function createTerrainMesh(heights, splatColors) {
const colorMap = {
dirt: splatColors && splatColors.dirt,
snow: splatColors && splatColors.snow,
sand: splatColors && splatColors.sand,
rock: splatColors && splatColors.rock,
grass: splatColors && splatColors.grass,
forest: splatColors && splatColors.forest,
stones: splatColors && splatColors.stones,
gravel: splatColors && splatColors.gravel
};
const channelColors = [
colorMap.dirt || [0.6, 0.479, 0.33],
colorMap.snow || [0.862, 0.929, 0.941],
colorMap.sand || [0.7, 0.659, 0.527],
colorMap.rock || [0.4, 0.393, 0.375],
colorMap.grass || [0.354, 0.37, 0.203],
colorMap.forest || [0.248, 0.3, 0.07],
colorMap.stones || [0.137, 0.278, 0.276],
colorMap.gravel || [0.25, 0.243, 0.22]
];
function getSplatChannel(x, y, channel, splatRes) {
if (!window.splatData) return 0;
const channelOffset = channel * splatRes * splatRes;
const index = channelOffset + y * splatRes + x;
return index < window.splatData.length ? window.splatData[index] : 0;
}
const geometry = new THREE.PlaneGeometry(TERRAIN_SIZE, TERRAIN_SIZE, SEGMENTS, SEGMENTS);
const vertices = geometry.attributes.position.array;
const colors = new Float32Array(vertices.length);
const res = SEGMENTS + 1;
const splatRes = window.splatResolution || res;
for (let i = 0; i < vertices.length / 3; i++) {
const x = i % res;
const y = Math.floor(i / res);
const flippedY = (res - 1) - y;
vertices[i * 3 + 2] = heights[flippedY * res + x] || 0;
const sx = Math.floor((x / (res - 1)) * (splatRes - 1));
const sy = Math.floor((flippedY / (res - 1)) * (splatRes - 1));
let r, g, b;
let totalWeight = 0;
const weights = [];
for (let c = 0; c < 8; c++) {
weights.push(getSplatChannel(sx, sy, c, splatRes) / 255.0);
totalWeight += weights[c];
}
if (totalWeight > 0) {
r = g = b = 0;
for (let c = 0; c < 8; c++) {
const w = weights[c] / totalWeight;
r += channelColors[c][0] * w;
g += channelColors[c][1] * w;
b += channelColors[c][2] * w;
}
} else {r = colorMap.grass[0]; g = colorMap.grass[1]; b = colorMap.grass[2];}
colors[i * 3] = r;
colors[i * 3 + 1] = g;
colors[i * 3 + 2] = b;
}
geometry.setAttribute('color', new THREE.BufferAttribute(colors, 3));
geometry.computeVertexNormals();
terrain = new THREE.Mesh(geometry, new THREE.MeshLambertMaterial({ vertexColors: true }));
terrain.rotation.x = -Math.PI / 2;
terrain.name = 'terrain';
scene.add(terrain);
collisionMeshes.push(terrain);
log('Terrain mesh created', 'success');
}
function createWaterMesh() {
const waterHeight = 1.32;
const waterGeo = new THREE.PlaneGeometry(TERRAIN_SIZE, TERRAIN_SIZE, 96, 96);
const waterMat = new THREE.MeshPhysicalMaterial({
color: 0x1e6a8a,
transparent: true,
opacity: 0.85,
roughness: 0.1,
metalness: 0.2,
clearcoat: 0.5,
clearcoatRoughness: 0.2,
reflectivity: 0.8,
side: THREE.DoubleSide
});
waterMesh = new THREE.Mesh(waterGeo, waterMat);
waterMesh.rotation.x = -Math.PI / 2;
waterMesh.position.y = waterHeight;
waterMesh.renderOrder = 1;
waterMesh.userData.isAnimatedWater = true;
waterMesh.userData.waveOffset = 0;
scene.add(waterMesh);
}
function updateWaterAnimation(delta) {
if (!waterMesh || !waterMesh.userData.isAnimatedWater) return;
waterMesh.userData.waveOffset += delta * 0.8;
const positions = waterMesh.geometry.attributes.position;
const count = positions.count;
const t = waterMesh.userData.waveOffset;
for (let i = 0; i < count; i++) {
const x = positions.getX(i);
const y = positions.getY(i);
const wave = Math.sin(x * 0.015 + t) * 0.5 +
Math.sin(y * 0.012 + t * 0.7) * 0.4;
positions.setZ(i, wave);
}
positions.needsUpdate = true;
waterMesh.geometry.computeVertexNormals();
}
function getTerrainHeightAt(worldX, worldZ) {
if (!window.terrainHeights || !window.terrainResolution) return 0;
const res = window.terrainResolution;
const halfSize = TERRAIN_SIZE / 2;
const percentX = (worldX + halfSize) / TERRAIN_SIZE;
const percentZ = (worldZ + halfSize) / TERRAIN_SIZE;
const gridX = Math.floor(percentX * (res - 1));
const gridZ = Math.floor(percentZ * (res - 1));
const flippedGridZ = (res - 1) - gridZ;
if (gridX < 0 || gridX >= res || flippedGridZ < 0 || flippedGridZ >= res) return 0;
return window.terrainHeights[flippedGridZ * res + gridX] || 0;
}
function getHeightAtPositionRaycast(worldX, worldY, worldZ, maxDistance = 100) {
if(!noClipMode){return null;}
const rayOrigin = new THREE.Vector3(worldX, worldY + 50, worldZ);
collisionRaycaster.set(rayOrigin, rayDownDirection);
collisionRaycaster.far = maxDistance;
const raycastTargets = [];
for (const marker of prefabMarkers) {
if (!marker || !marker.visible) continue;
if (marker.isSprite) continue;
if (!marker.geometry) continue;
raycastTargets.push(marker);
}
const intersects = collisionRaycaster.intersectObjects(raycastTargets, true);
if (intersects.length > 0) { return intersects[0].point.y; }
return null;
}
function collectCollisionMeshes(object, meshes) {
object.traverse((child) => {
if (child.isMesh && child.geometry) {
if (child.geometry.attributes.position && child.geometry.attributes.position.count > 0) { meshes.push(child); }
}
});
}
function getGroundHeightAt(worldX, worldZ) {
const raycastHeight = getHeightAtPositionRaycast(worldX, 0, worldZ, 150);
let maxHeight = getTerrainHeightAt(worldX, worldZ);
if (raycastHeight !== null) { maxHeight = Math.max(maxHeight, raycastHeight); }
return maxHeight;
}
function drawRoadsFromServer(roadsData) {
let index = 0;
roadsData.forEach(function(path) {
for (let i = 0; i < path.length - 1; i++) {
const p1 = path[i], p2 = path[i + 1];
const x1 = p1[0] || 0, z1 = -(p1[2] || 0);
const x2 = p2[0] || 0, z2 = -(p2[2] || 0);
const h1 = getTerrainHeightAt(x1, z1) + ROAD_HEIGHT_OFFSET;
const h2 = getTerrainHeightAt(x2, z2) + ROAD_HEIGHT_OFFSET;
const v1 = new THREE.Vector3(x1, h1, z1);
const v2 = new THREE.Vector3(x2, h2, z2);
addSegmentInstance(roadInstancer, index++, v1, v2);
}
});
roadInstancer.count = index;
roadInstancer.instanceMatrix.needsUpdate = true;
}
function drawRiverFromServer(riverData) {
let index = 0;
riverData.forEach(function(path) {
for (let i = 0; i < path.length - 1; i++) {
const p1 = path[i], p2 = path[i + 1];
const x1 = p1[0] || 0, z1 = -(p1[2] || 0);
const x2 = p2[0] || 0, z2 = -(p2[2] || 0);
const h1 = getTerrainHeightAt(x1, z1) + RIVER_HEIGHT_OFFSET;
const h2 = getTerrainHeightAt(x2, z2) + RIVER_HEIGHT_OFFSET;
const v1 = new THREE.Vector3(x1, h1, z1);
const v2 = new THREE.Vector3(x2, h2, z2);
addSegmentInstance(riverInstancer, index++, v1, v2);
}
});
riverInstancer.count = index;
riverInstancer.instanceMatrix.needsUpdate = true;
}
function drawRailsFromServer(railsData) {
let index = 0;
railsData.forEach(function(path) {
for (let i = 0; i < path.length - 1; i++) {
const p1 = path[i], p2 = path[i + 1];
const x1 = p1[0] || 0, z1 = -(p1[2] || 0);
const x2 = p2[0] || 0, z2 = -(p2[2] || 0);
const h1 = getTerrainHeightAt(x1, z1) + RAIL_HEIGHT_OFFSET;
const h2 = getTerrainHeightAt(x2, z2) + RAIL_HEIGHT_OFFSET;
const v1 = new THREE.Vector3(x1, h1, z1);
const v2 = new THREE.Vector3(x2, h2, z2);
addSegmentInstance(railInstancer, index++, v1, v2);
}
});
railInstancer.count = index;
railInstancer.instanceMatrix.needsUpdate = true;
}
function addSegmentInstance(instancer, index, v1, v2) {
const length = v1.distanceTo(v2);
tempPos.copy(v1).add(v2).multiplyScalar(0.5);
tempDir.subVectors(v2, v1).normalize();
tempQuat.setFromUnitVectors(SEGMENT_FORWARD, tempDir);
tempScale.set(1, 1, length);
tempMatrix.compose(tempPos, tempQuat, tempScale);
instancer.setMatrixAt(index, tempMatrix);
}
function addCubeInstance(index, x, y, z, scaleX, scaleY, scaleZ, rotX, rotY, rotZ) {
tempPos.set(x, y, z);
const normalizedRot = {
x: rotX || 0,
y: 180 - (rotY || 0),
z: rotZ || 0
};
const eulerRot = new THREE.Euler(
THREE.MathUtils.degToRad(normalizedRot.x),
THREE.MathUtils.degToRad(normalizedRot.y),
THREE.MathUtils.degToRad(normalizedRot.z),
'YXZ'
);
tempQuat.setFromEuler(eulerRot);
tempScale.set(scaleX, scaleY, scaleZ);
tempMatrix.compose(tempPos, tempQuat, tempScale);
cubeInstancer.setMatrixAt(index, tempMatrix);
}
function finalizeCubeInstancer() {
if (cubeInstancer) {
cubeInstancer.count = cubeCount;
cubeInstancer.instanceMatrix.needsUpdate = true;
log('Created ' + cubeCount + ' instanced cube prefabs', 'success');
}
}
function initGLTFLoader() { if (typeof THREE.GLTFLoader !== 'undefined') { gltfLoader = new THREE.GLTFLoader(); } else { log('GLTFLoader not available, will use cube markers', 'error'); } }
function loadGLBModel(url, onLoad, onError) {
if (glbFailedOnce.has(url)) {return;}
if (!gltfLoader) { if (onError) onError('GLTFLoader not initialized'); return; }
gltfLoader.load(url, function(gltf) { if (onLoad) onLoad(gltf); }, function(progress) { }, function(error) {glbFailedOnce.add(url); if (onError) onError(error); });
}
async function loadPrefabOffset(prefabName) {
if (prefabOffsetCache.has(prefabName)) {return prefabOffsetCache.get(prefabName);}
if (prefabOffsetLoading.has(prefabName)) {return await prefabOffsetLoading.get(prefabName);}
const promise = (async () => {
try {
const offsets = await loadEntityOffsets(prefabName);
prefabOffsetCache.set(prefabName, offsets);
return offsets;
} catch (err) {
const fallback = {
positionOffset: { x: 0, y: 0, z: 0 },
rotationOffset: { x: 0, y: 0, z: 0 },
scaleMultiplier: { x: 1, y: 1, z: 1 }
};
prefabOffsetCache.set(prefabName, fallback);
return fallback;
} finally {
prefabOffsetLoading.delete(prefabName);
}
})();
prefabOffsetLoading.set(prefabName, promise);
return await promise;
}
function clearPrefabOffsetCache(prefabName) {
if (prefabName) {
prefabOffsetCache.delete(prefabName);
console.log('Cleared cache for: ' + prefabName);
} else {
prefabOffsetCache.clear();
console.log('Cleared all offset caches');
}
}
function transformPositionFromUnity(x, y, z) { return new THREE.Vector3(x, y, -z); }
function transformRotationFromUnity(rotX, rotY, rotZ) { return { x: -rotX, y: -rotY, z: -rotZ }; }
function applyTransformWithLocalOffsets(obj, unityX, unityY, unityZ, unityRot, baseScale, offsets) {
const normalizedPos = transformPositionFromUnity(unityX || 0, unityY || 0, unityZ || 0);
const normalizedRot = transformRotationFromUnity(unityRot && unityRot.x !== undefined ? unityRot.x : 0, unityRot && unityRot.y !== undefined ? unityRot.y : 0, unityRot && unityRot.z !== undefined ? unityRot.z : 0);
obj.updateMatrixWorld(true);
const glbScale = new THREE.Vector3();
const glbQuat = new THREE.Quaternion();
const glbPos = new THREE.Vector3();
obj.matrixWorld.decompose(glbPos, glbQuat, glbScale);
const posOffset = offsets && offsets.positionOffset ? offsets.positionOffset : (offsets && offsets.localPositionOffset ? offsets.localPositionOffset : null);
const rotOffset = offsets && offsets.rotationOffset ? offsets.rotationOffset : (offsets && offsets.localRotationOffset ? offsets.localRotationOffset : null);
const scaleOff = offsets && offsets.scaleMultiplier ? offsets.scaleMultiplier : (offsets && offsets.localScaleOffset ? offsets.localScaleOffset : null);
const normalizedQuat = new THREE.Quaternion();
const normalizedEuler = new THREE.Euler(THREE.MathUtils.degToRad(normalizedRot.x), THREE.MathUtils.degToRad(normalizedRot.y), THREE.MathUtils.degToRad(normalizedRot.z), 'YXZ');
normalizedQuat.setFromEuler(normalizedEuler);
let localOffset = new THREE.Vector3(0, 0, 0);
if (posOffset) { localOffset.set(posOffset.x || 0, posOffset.y || 0, posOffset.z || 0); }
localOffset.applyQuaternion(normalizedQuat);
const finalPosition = normalizedPos.clone().add(localOffset);
obj.position.copy(finalPosition);
let finalQuat = normalizedQuat.clone();
if (rotOffset) {
const offsetEuler = new THREE.Euler(THREE.MathUtils.degToRad(rotOffset.x || 0), THREE.MathUtils.degToRad(rotOffset.y || 0), THREE.MathUtils.degToRad(rotOffset.z || 0), 'YXZ');
const offsetQuat = new THREE.Quaternion().setFromEuler(offsetEuler);
finalQuat.multiply(offsetQuat);
}
obj.quaternion.copy(finalQuat);
let scaleX = baseScale && baseScale.x !== undefined ? baseScale.x : 1, scaleY = baseScale && baseScale.y !== undefined ? baseScale.y : 1, scaleZ = baseScale && baseScale.z !== undefined ? baseScale.z : 1;
if (scaleOff) { scaleX *= (scaleOff.x || 1); scaleY *= (scaleOff.y || 1); scaleZ *= (scaleOff.z || 1); }
obj.scale.set(glbScale.x * scaleX, glbScale.y * scaleY, glbScale.z * scaleZ);
}
async function processPrefab(prefab, index) {
const pos = prefab.position || prefab.Position || prefab.pos;
if (!pos) { return; }
const unityX = pos[0] !== undefined ? pos[0] : (pos.x || 0);
const unityY = pos[1] !== undefined ? pos[1] : (pos.y || 0);
const unityZ = pos[2] !== undefined ? pos[2] : (pos.z || 0);
const rot = prefab.rotation || prefab.Rotation || prefab.rot || { x: 0, y: 0, z: 0 };
const unityRot = { x: rot[0] !== undefined ? rot[0] : (rot.x || 0), y: rot[1] !== undefined ? rot[1] : (rot.y || 0), z: rot[2] !== undefined ? rot[2] : (rot.z || 0) };
const scaleData = prefab.scale || prefab.Scale || { x: 1, y: 1, z: 1 };
const baseScale = { x: scaleData[0] !== undefined ? scaleData[0] : (scaleData.x || 1), y: scaleData[1] !== undefined ? scaleData[1] : (scaleData.y || 1), z: scaleData[2] !== undefined ? scaleData[2] : (scaleData.z || 1) };
const prefabName = prefab.name || 'Unknown';
if (isBBMarkerType(prefabName)) { createBBMarkerFromPrefab(prefab); }
if (prefabName.includes('cube') || prefabName.includes('invisible_collider')) {
if (cubeInstancer && cubeCount < MAX_CUBE_PREFABS) {
const markerPos = transformPositionFromUnity(unityX, unityY + 2, unityZ);
addCubeInstance(cubeCount, markerPos.x, markerPos.y, markerPos.z, baseScale.x, baseScale.y, baseScale.z, unityRot.x, unityRot.y, unityRot.z);
cubeCount++;
} else if (!cubeInstancer) {
const markerPos = transformPositionFromUnity(unityX, unityY, unityZ);
const markerGeo = new THREE.BoxGeometry(baseScale.x, baseScale.y, baseScale.z);
const markerMat = new THREE.MeshBasicMaterial({ color: 0x00ff00, opacity: 0.5, transparent: true });
const marker = new THREE.Mesh(markerGeo, markerMat);
marker.position.set(markerPos.x, markerPos.y, markerPos.z);
const normalizedRot = {
x: unityRot.x || 0,
y: 180 - (unityRot.y || 0),
z: unityRot.z || 0
};
const eulerRot = new THREE.Euler(
THREE.MathUtils.degToRad(normalizedRot.x),
THREE.MathUtils.degToRad(normalizedRot.y),
THREE.MathUtils.degToRad(normalizedRot.z),
'YXZ'
);
marker.rotation.copy(eulerRot);
marker.visible = showPrefabs && showCubes;
scene.add(marker);
prefabMarkers.push(marker);
}
return;
}
const shouldSkipModel = prefabName.includes('cave_') || prefabName.includes('swamp_') || prefabName.includes('ice_lake') || prefabName.includes('water_well') || prefabName.includes('ue_jungle_swamp') || prefabName.includes('ue_lake') || prefabName.includes('ue_oasis') || prefabName.includes('ue_canyon');
if (shouldSkipModel) {
const markerPos = transformPositionFromUnity(unityX, unityY, unityZ);
createCubeMarker(prefabName, markerPos.x, markerPos.y, markerPos.z);
return;
}
const modelUrl = window.location.origin + '/models/' + prefabName + '.glb';
let offsets;
try { offsets = await loadPrefabOffset(prefabName); } catch (err) { console.error('Failed to load offsets for ' + prefabName + ', using defaults.', err); offsets = { positionOffset: { x: 0, y: 0, z: 0 }, rotationOffset: { x: 0, y: 0, z: 0 }, scaleMultiplier: { x: 1, y: 1, z: 1 } }; }
if (loadedGLBModels[modelUrl]) {
const cached = loadedGLBModels[modelUrl];
const model = cached.scene.clone();
applyTransformWithLocalOffsets(model, unityX, unityY, unityZ, unityRot, baseScale, offsets);
model.visible = showPrefabs;
scene.add(model);
prefabMarkers.push(model);
collectCollisionMeshes(model, prefabMarkers);
addPrefabLabel(prefabName, model.position.x, model.position.y, model.position.z);
return;
}
loadGLBModel(modelUrl, function(gltf) {
const model = gltf.scene;
loadedGLBModels[modelUrl] = gltf;
applyTransformWithLocalOffsets(model, unityX, unityY, unityZ, unityRot, baseScale, offsets);
model.visible = showPrefabs;
scene.add(model);
prefabMarkers.push(model);
collectCollisionMeshes(model, prefabMarkers);
addPrefabLabel(prefabName, model.position.x, model.position.y, model.position.z);
}, function(error) {
log('Failed to load model: ' + prefabName, 'error');
createCubeMarker(prefabName, unityX, unityY, -unityZ);
});
}
function drawPrefabs(prefabsData) {
const maxPrefabs = prefabsData.length;
let bbCount = 0;
for (let i = 0; i < maxPrefabs; i++) {
const prefab = prefabsData[i];
const prefabName = prefab.name || 'Unknown';
if (isBBMarkerType(prefabName)) { bbCount++; }
processPrefab(prefabsData[i], i);
}
finalizeCubeInstancer();
if (bbCount > 0) { log('Created ' + bbCount + ' PreventBuilding markers', 'success'); }
log('Started processing ' + maxPrefabs + ' prefabs', 'success');
}
function addPrefabLabel(name, x, y, z) {
const canvas = document.createElement('canvas');
const ctx = canvas.getContext('2d');
canvas.width = 512; canvas.height = 200;
ctx.fillStyle = 'rgba(0, 0, 0, 0.7)';
ctx.fillRect(0, 0, 512, 128);
ctx.fillStyle = '#00ff00';
ctx.font = 'bold 32px Arial';
ctx.textAlign = 'center';
ctx.fillText(name, 256, 64);
const texture = new THREE.CanvasTexture(canvas);
const spriteMat = new THREE.SpriteMaterial({ map: texture, depthTest: false, sizeAttenuation: true });
const sprite = new THREE.Sprite(spriteMat);
sprite.position.set(x, y + 10, z);
sprite.scale.set(16, 4, 1);
sprite.visible = showLabels;
scene.add(sprite);
prefabLabels.push(sprite);
}
function createCubeMarker(name, x, y, z) {
const markerGeo = new THREE.BoxGeometry(5, 5, 5);
const markerMat = new THREE.MeshBasicMaterial({ color: 0xff6600, opacity: 0.9, transparent: true });
const marker = new THREE.Mesh(markerGeo, markerMat);
marker.position.set(x, y, z);
marker.visible = showPrefabs;
scene.add(marker);
prefabMarkers.push(marker);
addPrefabLabel(name, x, y, z);
}
function createDemoTerrain() {
log('Generating demo terrain');
const res = SEGMENTS + 1;
const geometry = new THREE.PlaneGeometry(TERRAIN_SIZE, TERRAIN_SIZE, SEGMENTS, SEGMENTS);
const vertices = geometry.attributes.position.array;
const colors = new Float32Array(vertices.length);
const waterColor = [0.169, 0.317, 0.362];
const sandColor = [0.7, 0.659, 0.527];
const halfRes = res / 2;
for (let i = 0; i < vertices.length / 3; i++) {
const x = i % res;
const y = Math.floor(i / res);
const distFromCenter = Math.sqrt(Math.pow(x - halfRes, 2) + Math.pow(y - halfRes, 2));
const maxDist = halfRes;
const h = distFromCenter < maxDist * 0.8 ? 20 : -50;
vertices[i * 3 + 2] = h;
const activeColor = h > 0 ? sandColor : waterColor;
colors[i * 3] = activeColor[0];
colors[i * 3 + 1] = activeColor[1];
colors[i * 3 + 2] = activeColor[2];
}
geometry.setAttribute('color', new THREE.BufferAttribute(colors, 3));
geometry.computeVertexNormals();
terrain = new THREE.Mesh(geometry, new THREE.MeshLambertMaterial({ vertexColors: true }));
terrain.rotation.x = -Math.PI / 2;
scene.add(terrain);
collisionMeshes.push(terrain);
}
function setupLayerToggleHandlers() {
document.getElementById('showDebug').addEventListener('change', function(e) {
showDebug = e.target.checked;
const state = loadLayerState();
state['showDebug'] = showDebug;
saveLayerState(state);
if (showDebug) {
debugEl.classList.remove('hidden');
} else {
debugEl.classList.add('hidden');
}
});
document.getElementById('showRoads').addEventListener('change', function(e) {
showRoads = e.target.checked;
const state = loadLayerState();
state['showRoads'] = showRoads;
saveLayerState(state);
if (roadInstancer) roadInstancer.visible = showRoads;
});
document.getElementById('showRails').addEventListener('change', function(e) {
showRails = e.target.checked;
const state = loadLayerState();
state['showRails'] = showRails;
saveLayerState(state);
if (railInstancer) railInstancer.visible = showRails;
});
document.getElementById('showPrefabs').addEventListener('change', function(e) {
showPrefabs = e.target.checked;
const state = loadLayerState();
state['showPrefabs'] = showPrefabs;
saveLayerState(state);
prefabMarkers.forEach(function(m) { m.visible = showPrefabs; });
});
document.getElementById('showBB').addEventListener('change', function(e) {
showBB = e.target.checked;
const state = loadLayerState();
state['showBB'] = showBB;
saveLayerState(state);
BBMarkers.forEach(function(m) { m.visible = showBB; });
});
document.getElementById('showWater').addEventListener('change', function(e) {
showWater = e.target.checked;
const state = loadLayerState();
state['showWater'] = showWater;
saveLayerState(state);
if (waterMesh) waterMesh.visible = showWater;
if (riverInstancer) riverInstancer.visible = showWater;
});
document.getElementById('showLabels').addEventListener('change', function(e) {
showLabels = e.target.checked;
const state = loadLayerState();
state['showLabels'] = showLabels;
saveLayerState(state);
prefabLabels.forEach(function(m) { m.visible = showLabels; });
entityLabels.forEach(function(m) { m.visible = showLabels && showEntities; });
});
document.getElementById('showEntities').addEventListener('change', function(e) {
showEntities = e.target.checked;
const state = loadLayerState();
state['showEntities'] = showEntities;
saveLayerState(state);
entityMeshes.forEach(function(m) { m.visible = showEntities; });
entityLabels.forEach(function(m) { m.visible = showLabels && showEntities; });
visibilityDirty = true;
});
document.getElementById('showCubes').addEventListener('change', function(e) {
showCubes = e.target.checked;
const state = loadLayerState();
state['showCubes'] = showCubes;
saveLayerState(state);
if (cubeInstancer) cubeInstancer.visible = showPrefabs && showCubes;
});
}
function setupControls() {
document.addEventListener('keydown', function(e) {
switch (e.code) {
case 'KeyW': keys.w = true; break;
case 'KeyA': keys.a = true; break;
case 'KeyS': keys.s = true; break;
case 'KeyD': keys.d = true; break;
case 'ShiftLeft': case 'ShiftRight': keys.shift = true; break;
case 'Space':
keys.space = true;
if (noClipMode && isGrounded) {
playerVelocity.y = noClipSettings.jumpHeight;
isGrounded = false;
}
e.preventDefault();
break;
case 'KeyC':
keys.c = true;
if (noClipMode) { isDucking = true; }
e.preventDefault();
break;
}
});
document.addEventListener('keyup', function(e) {
switch (e.code) {
case 'KeyW': keys.w = false; break;
case 'KeyA': keys.a = false; break;
case 'KeyS': keys.s = false; break;
case 'KeyD': keys.d = false; break;
case 'ShiftLeft': case 'ShiftRight': keys.shift = false; break;
case 'Space': keys.space = false; break;
case 'KeyC':
keys.c = false;
isDucking = false;
break;
}
});
document.addEventListener('click', function() { renderer.domElement.requestPointerLock(); });
document.addEventListener('pointerlockchange', function() { isPointerLocked = document.pointerLockElement === renderer.domElement; });
document.addEventListener('mousemove', function(e) {
if (!isPointerLocked) return;
euler.setFromQuaternion(camera.quaternion);
euler.y -= e.movementX * lookSpeed;
euler.x -= e.movementY * lookSpeed;
euler.x = Math.max(-Math.PI / 2, Math.min(Math.PI / 2, euler.x));
camera.quaternion.setFromEuler(euler);
});
window.addEventListener('resize', function() {
camera.aspect = window.innerWidth / window.innerHeight;
camera.updateProjectionMatrix();
renderer.setSize(window.innerWidth, window.innerHeight);
});
}
function setupMobileControls() {
if (!isMobile) return;
function requestFullscreen() {
const elem = document.documentElement;
if (elem.requestFullscreen) {
elem.requestFullscreen().catch(() => {});
} else if (elem.webkitRequestFullscreen) {
elem.webkitRequestFullscreen();
} else if (elem.msRequestFullscreen) {
elem.msRequestFullscreen();
}
}
let fullscreenRequested = false;
function tryFullscreen() {
if (!fullscreenRequested) {
fullscreenRequested = true;
requestFullscreen();
}
}
document.addEventListener('touchstart', tryFullscreen, { once: true, passive: true });
const joystickContainer = document.getElementById('joystick-container');
const joystickKnob = document.getElementById('joystick-knob');
const lookArea = document.getElementById('look-area');
const speedBtn = document.getElementById('speed-btn');
joystickContainer.addEventListener('touchstart', (e) => {
e.preventDefault();
if (joystickTouchId !== null) return;
const touch = e.changedTouches[0];
joystickTouchId = touch.identifier;
const rect = joystickContainer.getBoundingClientRect();
joystickStartX = rect.left + rect.width / 2;
joystickStartY = rect.top + rect.height / 2;
joystickActive = true;
}, { passive: false });
joystickContainer.addEventListener('touchmove', (e) => {
e.preventDefault();
for (const touch of e.changedTouches) {
if (touch.identifier === joystickTouchId) {
const dx = touch.clientX - joystickStartX;
const dy = touch.clientY - joystickStartY;
const maxDist = 40;
const dist = Math.sqrt(dx * dx + dy * dy);
const clampedDist = Math.min(dist, maxDist);
const angle = Math.atan2(dy, dx);
const clampedX = Math.cos(angle) * clampedDist;
const clampedY = Math.sin(angle) * clampedDist;
joystickKnob.style.transform = 'translate(calc(-50% + ' + clampedX + 'px), calc(-50% + ' + clampedY + 'px))';
joystickDeltaX = clampedX / maxDist;
joystickDeltaY = clampedY / maxDist;
}
}
}, { passive: false });
joystickContainer.addEventListener('touchend', (e) => {
for (const touch of e.changedTouches) {
if (touch.identifier === joystickTouchId) {
joystickTouchId = null;
joystickActive = false;
joystickKnob.style.transform = 'translate(-50%, -50%)';
joystickDeltaX = 0;
joystickDeltaY = 0;
}
}
});
joystickContainer.addEventListener('touchcancel', (e) => {
joystickTouchId = null;
joystickActive = false;
joystickKnob.style.transform = 'translate(-50%, -50%)';
joystickDeltaX = 0;
joystickDeltaY = 0;
});
lookArea.addEventListener('touchstart', (e) => {
e.preventDefault();
if (lookTouchId !== null) return;
const touch = e.changedTouches[0];
lookTouchId = touch.identifier;
lastLookX = touch.clientX;
lastLookY = touch.clientY;
}, { passive: false });
lookArea.addEventListener('touchmove', (e) => {
e.preventDefault();
for (const touch of e.changedTouches) {
if (touch.identifier === lookTouchId) {
const dx = touch.clientX - lastLookX;
const dy = touch.clientY - lastLookY;
lastLookX = touch.clientX;
lastLookY = touch.clientY;
euler.setFromQuaternion(camera.quaternion);
euler.y -= dx * lookSpeed * 2;
euler.x -= dy * lookSpeed * 2;
euler.x = Math.max(-Math.PI / 2, Math.min(Math.PI / 2, euler.x));
camera.quaternion.setFromEuler(euler);
}
}
}, { passive: false });
lookArea.addEventListener('touchend', (e) => {
for (const touch of e.changedTouches) {
if (touch.identifier === lookTouchId) {
lookTouchId = null;
}
}
});
lookArea.addEventListener('touchcancel', (e) => { lookTouchId = null; });
speedBtn.addEventListener('touchstart', (e) => {
e.preventDefault();
e.stopPropagation();
mobileSpeedMultiplier = mobileSpeedMultiplier === 1 ? 6 : 1;
speedBtn.classList.toggle('active', mobileSpeedMultiplier > 1);
}, { passive: false });
speedBtn.addEventListener('click', (e) => {
e.preventDefault();
e.stopPropagation();
mobileSpeedMultiplier = mobileSpeedMultiplier === 1 ? 6 : 1;
speedBtn.classList.toggle('active', mobileSpeedMultiplier > 1);
});
document.getElementById('canvas-container').addEventListener('touchstart', (e) => {
if (e.target.id === 'canvas-container' && lookTouchId === null) {
const touch = e.changedTouches[0];
const rect = joystickContainer.getBoundingClientRect();
const isInJoystick = touch.clientX < rect.right + 20 && touch.clientY > rect.top - 20;
if (!isInJoystick) {
lookTouchId = touch.identifier;
lastLookX = touch.clientX;
lastLookY = touch.clientY;
}
}
}, { passive: false });
document.getElementById('canvas-container').addEventListener('touchmove', (e) => {
for (const touch of e.changedTouches) {
if (touch.identifier === lookTouchId) {
const dx = touch.clientX - lastLookX;
const dy = touch.clientY - lastLookY;
lastLookX = touch.clientX;
lastLookY = touch.clientY;
euler.setFromQuaternion(camera.quaternion);
euler.y -= dx * lookSpeed * 2;
euler.x -= dy * lookSpeed * 2;
euler.x = Math.max(-Math.PI / 2, Math.min(Math.PI / 2, euler.x));
camera.quaternion.setFromEuler(euler);
}
}
}, { passive: false });
document.getElementById('canvas-container').addEventListener('touchend', (e) => {
for (const touch of e.changedTouches) {
if (touch.identifier === lookTouchId) {
lookTouchId = null;
}
}
});
}
function setupNoclipButton() {
document.getElementById('noclip-btn').addEventListener('click', function() {
noClipMode = !noClipMode;
const btn = document.getElementById('noclip-btn');
btn.classList.toggle('active', noClipMode);
if (noClipMode) {
renderer.domElement.requestPointerLock();
playerVelocity.set(0, 0, 0);
isGrounded = true;
log('Noclip OFF', 'info');
} else {
const groundHeight = getGroundHeightAt(camera.position.x, camera.position.z);
camera.position.y = groundHeight + baseCameraY;
playerVelocity.set(0, 0, 0);
isDucking = false;
log('Noclip ON', 'info');
}
});
}
function updateMovement(delta) {
if (!isPointerLocked && !joystickActive) return;
let moveX = 0;
let moveZ = 0;
let currentSpeed = moveSpeed;
if (noClipMode) {
const isRunning = keys.shift;
moveZ = Number(keys.w) - Number(keys.s);
moveX = Number(keys.d) - Number(keys.a);
currentSpeed = isRunning ? noClipSettings.runSpeed : noClipSettings.walkSpeed;
baseCameraY = isDucking ? noClipSettings.duckHeight : noClipSettings.eyeHeight;
const targetGroundHeight = getGroundHeightAt(camera.position.x, camera.position.z) + baseCameraY;
const feetPosition = camera.position.y - baseCameraY;
if (!isGrounded) {
playerVelocity.y -= noClipSettings.gravity * delta;
}
camera.position.y += playerVelocity.y * delta;
const currentFeetPos = camera.position.y - baseCameraY;
if (camera.position.y - baseCameraY <= targetGroundHeight) {
camera.position.y = targetGroundHeight + baseCameraY;
playerVelocity.y = 0;
isGrounded = true;
} else if (currentFeetPos > targetGroundHeight) {
isGrounded = false;
}
if (moveX !== 0 || moveZ !== 0) {
const speed = currentSpeed * delta;
const forward = new THREE.Vector3();
const right = new THREE.Vector3();
camera.getWorldDirection(forward);
forward.normalize();
right.crossVectors(forward, new THREE.Vector3(0, 1, 0));
const movement = new THREE.Vector3();
movement.addScaledVector(right, moveX * speed);
movement.addScaledVector(forward, moveZ * speed);
camera.position.add(movement);
}
} else {
if (isMobile && joystickActive) {
moveX = joystickDeltaX;
moveZ = -joystickDeltaY;
currentSpeed = moveSpeed * mobileSpeedMultiplier;
}
if (!isMobile) {
if (!isPointerLocked) { return; }
moveZ = Number(keys.w) - Number(keys.s);
moveX = Number(keys.d) - Number(keys.a);
currentSpeed = keys.shift ? moveSpeed * 6 : moveSpeed;
}
if (moveX !== 0 || moveZ !== 0) {
const speed = currentSpeed * delta;
const forward = new THREE.Vector3();
const right = new THREE.Vector3();
camera.getWorldDirection(forward);
forward.normalize();
right.crossVectors(forward, new THREE.Vector3(0, 1, 0));
const movement = new THREE.Vector3();
movement.addScaledVector(right, moveX * speed);
movement.addScaledVector(forward, moveZ * speed);
camera.position.add(movement);
}
const terrainHeight = getGroundHeightAt(camera.position.x, camera.position.z);
if(noClipMode){camera.position.y = Math.max(terrainHeight + baseCameraY, camera.position.y);}
}
const x = camera.position.x.toFixed(0);
const y = camera.position.y.toFixed(0);
const z = (camera.position.z * -1).toFixed(0);
document.getElementById('pos-display').textContent = x + ', ' + y + ', ' + z;
}
function updateRotationDisplay() {
const eulerRot = new THREE.Euler().setFromQuaternion(camera.quaternion, 'YXZ');
const x = THREE.MathUtils.radToDeg(eulerRot.x);
const y = THREE.MathUtils.radToDeg(eulerRot.y);
const z = THREE.MathUtils.radToDeg(eulerRot.z);
document.getElementById('rot-display').textContent = x.toFixed(1) + ', ' + y.toFixed(1) + ', ' + z.toFixed(1);
}
let lastTime = performance.now();
function animate() {
requestAnimationFrame(animate);
const delta = (performance.now() - lastTime) / 1000;
lastTime = performance.now();
animateEffects();
updateWaterAnimation(delta);
updateMovement(delta);
updateRotationDisplay();
updateFrustumCulling();
updateEntityVisibility();
updateEntityLerp(delta);
renderer.render(scene, camera);
}
const _tempQuat = new THREE.Quaternion();
function updateEntityLerp(delta) {
const lerpFactor = Math.min(1, ENTITY_LERP_SPEED * delta);
entityTargets.forEach(function(target, entityId) {
const mesh = entityMeshes.get(entityId);
if (!mesh) return;
if (mesh.userData && mesh.userData.isExplosion) return;
const isPlayer = mesh.userData && mesh.userData.isPlayer;
if (!mesh.visible && !isPlayer) return;
const distance = mesh.position.distanceTo(target.position);
if (distance >= 0.01) {
mesh.position.lerp(target.position, lerpFactor);
}
const currentQuat = new THREE.Quaternion();
mesh.getWorldQuaternion(currentQuat);
const targetQuat = target.quaternion.clone();
const angleDiff = currentQuat.angleTo(targetQuat);
if (angleDiff > 0.001) {
const slerpQuat = currentQuat.slerp(targetQuat, lerpFactor);
mesh.quaternion.copy(slerpQuat);
}
const label = entityLabels.get(entityId);
if (label) {
if (isPlayer || label.visible) {
label.position.set(mesh.position.x, mesh.position.y + target.labelOffset, mesh.position.z);
}
}
});
}
init();
</script>
</body>
</html>";
        }
        public static string GenerateServerDetailHtml(string wipeId, bool isAuthenticated)
        {
            string html;
            if (!isAuthenticated)
            {
                html = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>Server Details - " + wipeId + @"</title>
<link rel=""icon"" type=""image/x-icon"" href=""/favicon.ico"">
<style>
" + HtmlStyles.GetBaseStyles() + @"
</style>
</head>
<body>
<div class=""container"">
<div class=""card"" style=""max-width: 300px; margin: 100px auto;"">
<h1 class=""title"">Authentication Required</h1>
<p class=""subtitle"">Please login to view server details</p>
<a href=""/"" class=""action-btn"" style=""display: block; text-align: center; margin-top: 20px;"">Go to Login</a>
</div>
</div>
</body>
</html>";
            }
            else
            {
                html = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>Server Details - " + wipeId + @"</title>
<link rel=""icon"" type=""image/x-icon"" href=""/favicon.ico"">
<style>
" + HtmlStyles.GetBaseStyles() + @"
</style>
</head>
<body>
<div class=""container"">
<a href=""/"" class=""back-link"">Back to Dashboard</a>
<h1 class=""title"">Server: " + wipeId + @"</h1>
<p class=""subtitle"">Server Details and Map Information</p>
<div id=""loading"" class=""loading"">Loading server information...</div>
<div id=""error"" class=""error-box"" style=""display: none;""></div>
<div id=""content"" style=""display: none;"">
<div class=""stats"" id=""stats""></div>
<div class=""card"">
<h2>Server Status</h2>
<div class=""info-grid"" id=""serverStatus""></div>
</div>
<div class=""card"">
<h2>Map Information</h2>
<p>Map data received from this server</p>
<div class=""map-preview"" onclick=""openMapPreview()"">
<img id=""mapPreviewThumb"" alt=""Map Preview"" style=""display:none;"">
</div>
<h3>World Size: <span id=""worldSize"" class=""highlight""></span></h3>
<div id=""mapInfo""></div>
</div>
<div id=""mapPreviewOverlay"" class=""map-preview-overlay"">
<img id=""mapPreviewFull"">
</div>
<div class=""card"">
<h2>Available Files</h2>
<div id=""filesSection""></div>
</div>
<div class=""card"">
<h2>Explore</h2>
<p>Tools to explore the server data</p>
<div class=""action-buttons"">
<a href=""#"" id=""explore3dBtn"" class=""action-btn secondary"">Explore in 3D View</a>
<a href=""#"" id=""exploreSaveBtn"" class=""action-btn secondary"">Explore Save File</a>
<a href=""#"" id=""viewPlayersBtn"" class=""action-btn secondary"">View Players</a>
<a href=""#"" id=""viewEntsBtn"" class=""action-btn secondary"">View Entities</a>
</div>
</div>
</div>
<div id=""pathsModal"" class=""modal"">
<div class=""modal-content"">
<div class=""modal-header"">
<h2>Paths</h2>
<button class=""close-btn"" onclick=""closeModal('pathsModal')"">&times;</button>
</div>
<div id=""pathsContent""></div>
</div>
</div>
<div id=""prefabsModal"" class=""modal"">
<div class=""modal-content"">
<div class=""modal-header"">
<h2>Prefabs</h2>
<button class=""close-btn"" onclick=""closeModal('prefabsModal')"">&times;</button>
</div>
<div id=""prefabsFilterBar"" class=""filter-bar""></div>
<div id=""prefabsContent""></div>
</div>
</div>
<script>
const WIPE_ID = """ + EscapeJavaScriptString(wipeId) + @""";
" + GetServerDetailScript() + @"
</script>
</div>
</body>
</html>";
            }
            return html;
        }
        private static string GetServerDetailScript()
        {
            return @"
async function loadServerDetail() {
try {
const res = await fetch('/api/server/' + WIPE_ID);
if (res.status === 401) { window.location.href = '/'; return; }
if (res.status === 404) {
document.getElementById('error').textContent = 'Server not found';
document.getElementById('error').style.display = 'block';
document.getElementById('loading').style.display = 'none';
return;
}
const data = await res.json();
displayServerDetail(data);
} catch (err) {
document.getElementById('error').textContent = 'Failed to load: ' + err.message;
document.getElementById('error').style.display = 'block';
document.getElementById('loading').style.display = 'none';
}
}
function openMapPreview() {
const overlay = document.getElementById('mapPreviewOverlay');
const thumb = document.getElementById('mapPreviewThumb');
const full = document.getElementById('mapPreviewFull');
if (!thumb || !thumb.src) return;
full.src = thumb.src;
overlay.style.display = 'flex';
document.addEventListener('keydown', handlePreviewKeydown);
overlay.addEventListener('click', handlePreviewClick);
}
function closeMapPreview() {
const overlay = document.getElementById('mapPreviewOverlay');
if (overlay) overlay.style.display = 'none';
document.removeEventListener('keydown', handlePreviewKeydown);
overlay.removeEventListener('click', handlePreviewClick);
}
function handlePreviewKeydown(e) { closeMapPreview(); }
function handlePreviewClick(e) { closeMapPreview(); }
async function decompressGzipBase64(base64) {
try {
const binaryString = atob(base64);
const len = binaryString.length;
const bytes = new Uint8Array(len);
for (let i = 0; i < len; i++) { bytes[i] = binaryString.charCodeAt(i); }
const ds = new DecompressionStream('gzip');
const decompressedStream = new Response(bytes).body.pipeThrough(ds);
const resultBuffer = await new Response(decompressedStream).arrayBuffer();
return resultBuffer;
} catch (e) { return null; }
}
async function decompressData(base64) {
const buffer = await decompressGzipBase64(base64);
if (!buffer) return null;
return new Uint8Array(buffer);
}
async function displayServerDetail(data) {
document.getElementById('loading').style.display = 'none';
document.getElementById('content').style.display = 'block';
const s = data.server;
const totalBytes = s.bytesReceived || 0;
const mapInfo = data.mapInfo;
const worldSize = mapInfo.worldSize || mapInfo.worldsize || mapInfo.size || 'Unknown';
let pngBytes = null;
if (mapInfo.png) {pngBytes = await decompressData(mapInfo.png);}
if (pngBytes) {
const blob = new Blob([pngBytes], { type: 'image/png' });
const url = URL.createObjectURL(blob);
const thumb = document.getElementById('mapPreviewThumb');
thumb.src = url;
thumb.style.display = 'block';
} else if (mapInfo.mapPng) {
const thumb = document.getElementById('mapPreviewThumb');
thumb.src = 'data:image/png;base64,' + mapInfo.mapPng;
thumb.style.display = 'block';
}
document.getElementById('worldSize').textContent = worldSize;
document.getElementById('stats').innerHTML = '<div class=""stat-box""><div class=""stat-value"">' + s.packetsReceived.toLocaleString() + '</div><div class=""stat-label"">Packets</div></div><div class=""stat-box""><div class=""stat-value"">' + formatBytes(totalBytes) + '</div><div class=""stat-label"">Data Received</div></div><div class=""stat-box clickable"" onclick=""showPrefabs()""><div class=""stat-value"">' + (mapInfo.prefabCount || 0).toLocaleString() + '</div><div class=""stat-label"">Prefabs (click to view)</div></div><div class=""stat-box clickable"" onclick=""showPaths()""><div class=""stat-value"">' + (mapInfo.pathCount || 0).toLocaleString() + '</div><div class=""stat-label"">Paths (click to view)</div></div>';
document.getElementById('serverStatus').innerHTML = '<div class=""info-item""><div class=""label"">Connected At</div><div class=""value"">' + formatDate(s.connectedAt) + '</div></div><div class=""info-item""><div class=""label"">Last Activity</div><div class=""value"">' + formatDate(s.lastActivity) + '</div></div>';
let mapInfoHtml = '<p>No map files available yet.</p>';
if (data.files.maps.length > 0) {
mapInfoHtml = '<div class=""stats"" style=""margin-bottom:15px;""><div class=""stat-box""><div class=""stat-value"">' + (mapInfo.mapCount || 0) + '</div><div class=""stat-label"">Map Layers</div></div><div class=""stat-box""><div class=""stat-value"">' + (mapInfo.prefabCount || 0) + '</div><div class=""stat-label"">Prefabs</div></div><div class=""stat-box""><div class=""stat-value"">' + (mapInfo.pathCount || 0) + '</div><div class=""stat-label"">Paths</div></div><div class=""stat-box""><div class=""stat-value"">' + (mapInfo.customMonumentCount || 0) + '</div><div class=""stat-label"">Custom Monuments</div></div></div>';
if (mapInfo.mapNames && mapInfo.mapNames.length > 0) {
mapInfoHtml += '<h3>Map Layers <span class=""hint"">click to download</span></h3><div class=""badge-container"">';
mapInfo.mapNames.forEach(function(name) { mapInfoHtml += '<span class=""badge badge-info clickable"" onclick=""downloadMapLayer(\'' + name + '\')"">' + name + '</span>'; });
mapInfoHtml += '</div>';
}
if (mapInfo.prefabCategories && mapInfo.prefabCategories.length > 0) {
mapInfoHtml += '<h3>Prefab Categories <span class=""hint"">click to filter</span></h3><div class=""badge-container"">';
mapInfo.prefabCategories.forEach(function(cat) {
const count = mapInfo.prefabCategoryCounts[cat] || 0;
mapInfoHtml += '<span class=""badge badge-info clickable' + (cat === currentPrefabCategory ? ' active' : '') + '"" onclick=""showPrefabs(\'' + cat + '\')"">' + cat + ' (' + count + ')</span>';
});
mapInfoHtml += '</div>';
}
if (mapInfo.pathNames && mapInfo.pathNames.length > 0) {
mapInfoHtml += '<h3>Paths</h3><div class=""badge-container"">';
mapInfo.pathNames.forEach(function(name) { mapInfoHtml += '<span class=""badge badge-warning"">' + name + '</span>'; });
mapInfoHtml += '</div>';
}
if (mapInfo.customMonuments && mapInfo.customMonuments.length > 0) {
mapInfoHtml += '<h3>Custom Monuments</h3><table class=""data-table""><thead><tr><th>Name</th><th>Size</th></tr></thead><tbody>';
mapInfo.customMonuments.forEach(function(m) { mapInfoHtml += '<tr><td>' + m.name + '</td><td>' + formatBytes(m.size || 0) + '</td></tr>'; });
mapInfoHtml += '</tbody></table>';
}
mapInfoHtml += '<h3>Map Files <span class=""hint"">click to download</span></h3><table class=""data-table""><thead><tr><th>File Name</th><th>Size</th><th>Created</th></tr></thead><tbody>';
data.files.maps.forEach(function(f) { mapInfoHtml += '<tr class=""clickable"" onclick=""downloadFile(\'' + f.name + '\', \'map\')""><td>' + f.name + '</td><td>' + formatBytes(f.size) + '</td><td>' + formatDate(f.created) + '</td></tr>'; });
mapInfoHtml += '</tbody></table>';
}
document.getElementById('mapInfo').innerHTML = mapInfoHtml;
let filesHtml = '';
if (data.files.snapshots.length > 0) {
filesHtml += '<h3>Save Snapshots <span class=""hint"">click to download</span></h3><table class=""data-table""><thead><tr><th>File Name</th><th>Size</th><th>Created</th></tr></thead><tbody>';
data.files.snapshots.forEach(function(f) { filesHtml += '<tr class=""clickable"" onclick=""downloadFile(\'' + f.name + '\', \'snapshot\')""><td>' + f.name + '</td><td>' + formatBytes(f.size) + '</td><td>' + formatDate(f.created) + '</td></tr>'; });
filesHtml += '</tbody></table>';
}
if (data.files.stringPools.length > 0) {
filesHtml += '<h3>String Pool Files <span class=""hint"">click to download</span></h3><table class=""data-table""><thead><tr><th>File Name</th><th>Size</th><th>Created</th></tr></thead><tbody>';
data.files.stringPools.forEach(function(f) { filesHtml += '<tr class=""clickable"" onclick=""downloadFile(\'' + f.name + '\', \'stringpool\')""><td>' + f.name + '</td><td>' + formatBytes(f.size) + '</td><td>' + formatDate(f.created) + '</td></tr>'; });
filesHtml += '</tbody></table>';
}
if (data.files.manifests.length > 0) {
filesHtml += '<h3>Manifest Files <span class=""hint"">click to download</span></h3><table class=""data-table""><thead><tr><th>File Name</th><th>Size</th><th>Created</th></tr></thead><tbody>';
data.files.manifests.forEach(function(f) { filesHtml += '<tr class=""clickable"" onclick=""downloadFile(\'' + f.name + '\', \'manifest\')""><td>' + f.name + '</td><td>' + formatBytes(f.size) + '</td><td>' + formatDate(f.created) + '</td></tr>'; });
filesHtml += '</tbody></table>';
}
if (filesHtml === '') filesHtml = '<p>No additional files available yet.</p>';
document.getElementById('filesSection').innerHTML = filesHtml;
const hasMapData = data.files.maps.length > 0;
document.getElementById('explore3dBtn').href = hasMapData ? '/3dviewer/' + WIPE_ID : '#';
document.getElementById('explore3dBtn').addEventListener('click', function(e) { if (!hasMapData) { e.preventDefault(); alert('No map data available for 3D view.'); } });
const hasSnapshot = data.files.snapshots.length > 0;
document.getElementById('exploreSaveBtn').href = hasSnapshot ? '/savefile/' + WIPE_ID : '#';
document.getElementById('exploreSaveBtn').addEventListener('click', function(e) { if (!hasSnapshot) { e.preventDefault(); alert('No snapshot data available to explore.'); } });
document.getElementById('viewPlayersBtn').href = '/viewplayers/' + WIPE_ID;
document.getElementById('viewEntsBtn').href = '/viewents/' + WIPE_ID;
}
function formatBytes(bytes) {
if (bytes === 0) return '0 B';
const k = 1024;
const sizes = ['B', 'KB', 'MB', 'GB'];
const i = Math.floor(Math.log(bytes) / Math.log(k));
return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
}
function formatDate(isoString) {
if (!isoString) return 'N/A';
const d = new Date(isoString);
return d.toLocaleString();
}
function closeModal(modalId) { document.getElementById(modalId).classList.remove('show'); }
document.addEventListener('click', function(e) { if (e.target.classList.contains('modal')) e.target.classList.remove('show'); });
let currentPrefabCategory = 'all';
async function showPaths(page) {
page = page || 1;
const modal = document.getElementById('pathsModal');
modal.classList.add('show');
const content = document.getElementById('pathsContent');
content.innerHTML = '<p class=""centered"">Loading...</p>';
try {
const res = await fetch('/api/paths/' + WIPE_ID + '?page=' + page);
if (!res.ok) throw new Error('Failed to load paths');
const data = await res.json();
let html = '<p style=""margin-bottom:10px;"">Total: ' + data.total + ' paths</p>';
html += '<table class=""data-table""><thead><tr><th>Name</th><th>Spline</th><th>Width</th><th>Nodes</th></tr></thead><tbody>';
data.paths.forEach(function(p) { html += '<tr><td>' + (p.name || 'Unnamed') + '</td><td>' + (p.spline ? 'Yes' : 'No') + '</td><td>' + (p.width ? p.width.toFixed(2) : 'N/A') + '</td><td>' + p.nodes + '</td></tr>'; });
html += '</tbody></table>';
html += '<div class=""pagination"">';
html += '<button onclick=""showPaths(' + (page - 1) + ')' + '""' + (page <= 1 ? ' disabled' : '') + '>Previous</button>';
html += '<span>Page ' + page + ' of ' + data.totalPages + '</span>';
html += '<button onclick=""showPaths(' + (page + 1) + ')' + '""' + (page >= data.totalPages ? ' disabled' : '') + '>Next</button>';
html += '</div>';
content.innerHTML = html;
} catch (err) {content.innerHTML = '<p class=""error-text"">Error: ' + err.message + '</p>';}
}
async function showPrefabs(category, page) {
category = category || 'all';
page = page || 1;
currentPrefabCategory = category;
const modal = document.getElementById('prefabsModal');
modal.classList.add('show');
const filterBar = document.getElementById('prefabsFilterBar');
const content = document.getElementById('prefabsContent');
content.innerHTML = '<p class=""centered"">Loading...</p>';
const res = await fetch('/api/server/' + WIPE_ID);
const serverData = await res.json();
const mapInfo = serverData.mapInfo || {};
let filterHtml = '<span class=""badge badge-info clickable' + (category === 'all' ? ' active' : '') + '"" onclick=""showPrefabs(\'all\', 1)"">All</span>';
if (mapInfo.prefabCategories) {
mapInfo.prefabCategories.forEach(function(cat) {
const count = mapInfo.prefabCategoryCounts[cat] || 0;
filterHtml += '<span class=""badge badge-info clickable' + (category === cat ? ' active' : '') + '"" onclick=""showPrefabs(\'' + cat + '\', 1)"">' + cat + ' (' + count + ')</span>';
});
}
filterBar.innerHTML = filterHtml;
try {
const catParam = category !== 'all' ? '&category=' + encodeURIComponent(category) : '';
const res2 = await fetch('/api/prefabs/' + WIPE_ID + '?page=' + page + catParam);
if (!res2.ok) throw new Error('Failed to load prefabs');
const data = await res2.json();
let html = '<p style=""margin-bottom:10px;"">Showing ' + data.prefabs.length + ' of ' + data.total + ' prefabs';
if (category !== 'all') html += ' (filtered by: ' + category + ')';
html += '</p>';
html += '<table class=""data-table""><thead><tr><th>Category</th><th>ID</th><th>Name</th><th>Position</th></tr></thead><tbody>';
data.prefabs.forEach(function(p) { html += '<tr><td>' + p.category + '</td><td>' + p.id + '</td><td>' + (p.name || 'Unknown') + '</td><td class=""pos-cell"">' + p.position + '</td></tr>'; });
html += '</tbody></table>';
html += '<div class=""pagination"">';
html += '<button onclick=""showPrefabs(\'' + category + '\', ' + (page - 1) + ')' + '""' + (page <= 1 ? ' disabled' : '') + '>Previous</button>';
html += '<span>Page ' + page + ' of ' + data.totalPages + '</span>';
html += '<button onclick=""showPrefabs(\'' + category + '\', ' + (page + 1) + ')' + '""' + (page >= data.totalPages ? ' disabled' : '') + '>Next</button>';
html += '</div>';
content.innerHTML = html;
} catch (err) {content.innerHTML = '<p class=""error-text"">Error: ' + err.message + '</p>';}
}
async function downloadMapLayer(layerName) {
try {
const res = await fetch('/api/mapdata/' + WIPE_ID + '/' + encodeURIComponent(layerName));
if (!res.ok) throw new Error('Failed to download map layer');
const data = await res.json();
const bytes = atob(data.data);
const buffer = new ArrayBuffer(bytes.length);
const arr = new Uint8Array(buffer);
for (let i = 0; i < bytes.length; i++) arr[i] = bytes.charCodeAt(i);
const blob = new Blob([buffer]);
const url = URL.createObjectURL(blob);
const a = document.createElement('a');
a.href = url;
a.download = layerName + '.bin';
a.click();
URL.revokeObjectURL(url);
} catch (err) { alert('Error downloading: ' + err.message); }
}
function downloadFile(filename, fileType) {window.location.href = '/api/download/' + WIPE_ID + '/' + fileType + '/' + encodeURIComponent(filename);}
loadServerDetail();
";
        }
        public static string GenerateIndexHtml(bool isAuthenticated)
        {
            string html;
            if (!isAuthenticated)
            {
                html = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0, maximum-scale=5.0, user-scalable=yes"">
<meta name=""apple-mobile-web-app-capable"" content=""yes"">
<meta name=""apple-mobile-web-app-status-bar-style"" content=""black-translucent"">
<title>Rust Relay Admin</title>
<link rel=""icon"" type=""image/x-icon"" href=""/favicon.ico"">
<style>
" + HtmlStyles.GetBaseStyles() + @"
</style>
</head>
<body>
<div class=""container"">
<div class=""card login-form"">
<h1 class=""title"">Rust Relay Admin</h1>
<form id=""loginForm"">
<input type=""password"" id=""password"" placeholder=""Enter Password"" required autocomplete=""current-password"">
<button type=""submit"">Login</button>
<p class=""error"" id=""errorMsg"">Invalid password</p>
</form>
</div>
</div>
<script>
document.getElementById('loginForm').addEventListener('submit', async function(e) {
e.preventDefault();
const password = document.getElementById('password').value;
const formData = new URLSearchParams();
formData.append('password', password);
try {
const res = await fetch('/api/login', { method: 'POST', body: formData });
const data = await res.json();
if (data.success) { window.location.href = '/'; }
else { document.getElementById('errorMsg').style.display = 'block'; }
} catch (err) { document.getElementById('errorMsg').style.display = 'block'; }
});
</script>
</body>
</html>";
            }
            else
            {
                html = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0, maximum-scale=5.0, user-scalable=yes"">
<meta name=""apple-mobile-web-app-capable"" content=""yes"">
<meta name=""apple-mobile-web-app-status-bar-style"" content=""black-translucent"">
<title>Rust Relay Admin</title>
<link rel=""icon"" type=""image/x-icon"" href=""/favicon.ico"">
<style>
" + HtmlStyles.GetBaseStyles() + @"
</style>
</head>
<body>
<div class=""container"">
<h1 class=""title"">Rust Relay Admin</h1>
<button class=""refresh-btn"" onclick=""loadServers()"">Refresh</button>
<div class=""stats"" id=""stats""></div>
<div class=""card"">
<h2>Connected Servers</h2>
<p class=""subtitle"">Click on a server to view details</p>
<div class=""table-wrapper"">
<table class=""data-table"" id=""serversTable"">
<thead><tr><th class=""wipe-id-col"">Wipe ID</th><th class=""desktop-only"">Connected</th><th class=""desktop-only"">Last Activity</th><th class=""desktop-only"">Packets</th><th class=""desktop-only"">Bytes</th><th>Status</th></tr></thead>
<tbody id=""serversBody""></tbody>
</table>
</div>
<p class=""last-update"" id=""lastUpdate""></p>
</div>
<button class=""logout-btn"" onclick=""logout()"">Logout</button>
</div>
<script>
async function loadServers() {
try {
const res = await fetch('/api/servers');
if (res.status === 401) { window.location.reload(); return; }
const data = await res.json();
const servers = data.servers || [];
const totalPackets = servers.reduce(function(a, s) { return a + (s.packetsReceived || 0); }, 0);
const totalBytes = servers.reduce(function(a, s) { return a + (s.bytesReceived || 0); }, 0);
document.getElementById('stats').innerHTML = '<div class=""stat-box""><div class=""stat-value"">' + servers.length + '</div><div class=""stat-label"">Servers</div></div><div class=""stat-box""><div class=""stat-value"">' + totalPackets.toLocaleString() + '</div><div class=""stat-label"">Total Packets</div></div><div class=""stat-box""><div class=""stat-value"">' + formatBytes(totalBytes) + '</div><div class=""stat-label"">Total Data</div></div>';
const tbody = document.getElementById('serversBody');
if (servers.length === 0) {
tbody.innerHTML = '<tr><td colspan=""6"" class=""centered"">No servers connected</td></tr>';
} else {
tbody.innerHTML = servers.map(function(s) {
const hasActivity = s.lastActivity && Date.parse(s.lastActivity) > (Date.now() - 5000);
const statusBadges = [];
if (hasActivity) statusBadges.push('<span class=""badge badge-success"">Online</span>');
if (s.hasMapData) statusBadges.push('<span class=""badge badge-info"">Map</span>');
if (s.hasSnapshot) statusBadges.push('<span class=""badge badge-info"">Snapshot</span>');
if (!hasActivity && s.packetsReceived > 0) statusBadges.push('<span class=""badge badge-warning"">Offline</span>');
return '<tr class=""clickable"" onclick=""window.location=\'/server/' + s.wipeId + '\'""><td class=""wipe-id-col""><code>' + s.wipeId + '</code></td><td class=""desktop-only"">' + formatDate(s.connectedAt) + '</td><td class=""desktop-only"">' + formatDate(s.lastActivity) + '</td><td class=""desktop-only"">' + (s.packetsReceived || 0).toLocaleString() + '</td><td class=""desktop-only"">' + formatBytes(s.bytesReceived || 0) + '</td><td>' + statusBadges.join(' ') + '</td></tr>';
}).join('');
}
document.getElementById('lastUpdate').textContent = 'Last updated: ' + new Date().toLocaleTimeString();
} catch (err) { console.error(err); }
}
function formatBytes(bytes) {
if (bytes === 0) return '0 B';
const k = 1024;
const sizes = ['B', 'KB', 'MB', 'GB'];
const i = Math.floor(Math.log(bytes) / Math.log(k));
return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
}
function formatDate(isoString) {
if (!isoString) return 'N/A';
const d = new Date(isoString);
return d.toLocaleString();
}
async function logout() {
document.cookie = 'auth=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
window.location.reload();
}
loadServers();
setInterval(loadServers, 1000);
</script>
</body>
</html>";
            }
            return html;
        }
    }
    #endregion
}
