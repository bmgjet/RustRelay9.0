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
using Newtonsoft.Json;
using ProtoBuf;
using RustRelayReceiver.ProtoBuf;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace RustRelayReceiver
{
    class Program
    {
        // Configuration
        private static string _baseUrl = "http://localhost:8080";
        private static string _authToken = "demotoken";
        private static readonly HttpListener _httpListener = new HttpListener();
        private static readonly List<WebSocketClient> _webSocketClients = new List<WebSocketClient>();
        private static readonly List<ServerInfo> _connectedServers = new List<ServerInfo>();
        private static readonly Dictionary<string, (WorldSerialization ws, DateTime loadedAt, string filePath)> _mapCache = new();
        private static readonly object _clientsLock = new object();
        private static readonly object _serversLock = new object();
        private static readonly object _mapCacheLock = new();
        private const int MAP_CACHE_TTL_MINUTES = 10;
        private static readonly Dictionary<uint, string> _globalStringPool = new Dictionary<uint, string>();
        private static readonly object _stringPoolLock = new object();
        private static long _totalPacketsReceived = 0;
        private static long _startTime = Stopwatch.GetTimestamp();
        private const int MarkerMagic = 1398035026;
        private const int MarkerLength = 12;
        public static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1);
        private static readonly object _consoleLock = new object();
        private static int _headerHeight = 0;
        private static readonly string DataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RelayData");
        private static readonly object FileWriteLock = new object();
        public static readonly string[] MapLayers = new string[] { "terrain", "height", "splat", "biome", "topology", "alpha", "water" };

        #region Server
        static void Main(string[] args)
        {
            PrintStatistics();
            ParseArguments(args);
            LogDebug($"Starting relay server on {_baseUrl}");
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
            LogDebug("\nShutting down...");
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
                Environment.Exit(1);
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
            public System.Net.WebSockets.WebSocket Socket { get; set; }
            public string WipeId { get; set; }
            public DateTime ConnectedAt { get; set; }
        }

        public static async Task SafeWrite(HttpListenerResponse resp, byte[] data, string contentType = "application/octet-stream")
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
   
        private static void OnStringPoolReceived(string wipeId, Dictionary<uint, string> stringPool)
        {
            SaveStringPoolToFile(wipeId, stringPool);
        }

        private static void OnManifestReceived(string wipeId, Dictionary<uint, string> manifest)
        {
            SaveManifestToFile(wipeId, manifest);
        }

        private static void OnSnapshotReceived(string wipeId, byte[] data)
        {
            SaveSnapshotToFile(wipeId, data);
            string checksum = ComputeChecksum(data);
            LogDebug($"[Snapshot] SHA256: {checksum}");
        }

        private static void OnMapFileReceived(string wipeId, string filename, byte[] data)
        {
            SaveMapFileToDirectory(wipeId, filename, data);
            string checksum = ComputeChecksum(data);
            LogDebug($"[MapFile] {filename} - {data.Length} bytes, SHA256: {checksum}");
        }
        #endregion

        #region Processing
        private static async Task ProcessRequestAsync(HttpListenerContext context)
        {
            try
            {
                string serverid = context.Request.Headers["X-Wipe-Id"];
                if (!string.IsNullOrEmpty(serverid)) { CreateDirectories(serverid); }
                if (context.Request.IsWebSocketRequest){ await HandleWebSocketRequest(context); }
                else { _ = Task.Run(() => HandleHttpRequest(context));  }
            }
            catch (Exception ex)
            {
                LogDebug($"Processing error: {ex.Message}");
            }
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
            string boundary = contentType.Substring(boundaryIndex + 9);
            if (boundary.StartsWith("\""))
            {
                int endQuote = boundary.IndexOf('"', 1);
                if (endQuote > 0) { boundary = boundary.Substring(1, endQuote - 1); } else { boundary = boundary.Substring(1); }
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
                boundary = boundary.Substring(0, endIndex);
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
                byte[] crlf = Encoding.UTF8.GetBytes("\r\n");
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
                        string filename = ExtractFilename(headers);
                        string formName = ExtractFormName(headers);
                        if (!string.IsNullOrEmpty(filename))
                        {
                            byte[] fileContent = new byte[contentLength];
                            Array.Copy(bodyBytes, pos, fileContent, 0, contentLength);
                            files[filename] = fileContent;
                        }
                        else if (!string.IsNullOrEmpty(formName) && formName == "map")
                        {
                            string originalFilename = ExtractOriginalFilename(headers);
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
            string wipeId = "";
            var query = context.Request.QueryString;
            foreach (string key in query.AllKeys) { if (key == "wipeId") { wipeId = query[key]; } }
            try
            {
                var wsContext = await context.AcceptWebSocketAsync(null);
                var webSocket = wsContext.WebSocket;
                LogDebug($"[WS] Client connected - wipeId={wipeId}, token={(!string.IsNullOrEmpty(_authToken))}");
                TrackServerActivity(wipeId);
                var client = new WebSocketClient
                {
                    Socket = webSocket,
                    WipeId = wipeId,
                    ConnectedAt = DateTime.UtcNow
                };
                lock (_clientsLock) { _webSocketClients.Add(client); }
                var buffer = new byte[65536];
                while (webSocket.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    try
                    {
                        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                        {
                            LogDebug($"[WS] Client disconnected - wipeId={wipeId}");
                            break;
                        }
                        else if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Binary)
                        {
                            ProcessBinaryPacket(buffer, result.Count, wipeId);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"[WS] Receive error: {ex.Message}");
                        break;
                    }
                }
                lock (_clientsLock) { _webSocketClients.Remove(client); }
                await webSocket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch (Exception ex) { LogDebug($"[WS] Connection error: {ex.Message}"); }
        }

        private static async Task HandleHttpRequest(HttpListenerContext context)
        {
            var response = context.Response;
            var request = context.Request;
            string serverTime = request.Headers["X-Server-Time"];
            string wipeId = request.Headers["X-Wipe-Id"];
            string auth = request.Headers["Authorization"];
            try
            {
                string path = request.Url.AbsolutePath.ToLower();
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
                else if (path == "/favicon.ico")
                {
                    if (File.Exists("favicon.ico")) { await SafeWrite(context.Response, File.ReadAllBytes("favicon.ico"), "image/x-icon"); }
                }
                else if (path == "/api/servers")
                {
                    await HandleServersApi(context);
                }
                else if (path == "/api/server/")
                {
                    await HandleServerDetailApi(context);
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
                    await Handle3DMapData(context);
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

        private static void ProcessBinaryPacket(byte[] buffer, int length, string wipeId)
        {
            Interlocked.Increment(ref _totalPacketsReceived);
            UpdateServerStats(wipeId, length);
            if (length < 4) { return; }
            Span<byte> span = buffer.AsSpan(0, length);
            if (length == 12)
            {
                int magic = BinaryPrimitives.ReadInt32LittleEndian(span);
                if (magic == MarkerMagic)
                {
                    long serverTicks = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(4));
                    HandleMarker(wipeId, serverTicks);
                    return;
                }
            }
            int packetId = span[0];
            if (packetId < 140) { return; }
            MessageType type = (MessageType)(packetId - 140);
            HandleNetworkPacket(type, span, wipeId);
        }

        private static async Task HandleModels(HttpListenerContext ctx)
        {
            if (!ctx.Request.Url.AbsolutePath.StartsWith("/models/", StringComparison.OrdinalIgnoreCase)) { return; }
            string rawFileName = ctx.Request.Url.AbsolutePath.Substring("/models/".Length);
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
            if (!File.Exists(canonicalPath))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
            string extension = Path.GetExtension(fileName).ToLowerInvariant();
            string contentType = "";
            switch (extension)
            {
                case ".glb": contentType = "model/gltf-binary"; break;
                case ".gltf": contentType = "model/gltf+json"; break;
                case ".json": contentType = "application/json"; break;
                case ".bin": contentType = "application/octet-stream"; break;
                default: contentType = "application/octet-stream"; break;
            };
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
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.OutputStream.Close();
            }
        }

        private static void HandleMarker(string wipeId, long serverTicks)
        {

        }

        private static void HandleNetworkPacket(MessageType type, Span<byte> packet, string wipeId)
        {
            switch (type)
            {
                case MessageType.Entities:
                    //DecodeEntities(packet, wipeId);
                    break;
                case MessageType.EntityPosition:
                    // DecodeEntityPosition(packet, wipeId);
                    break;
                case MessageType.RPCMessage:
                case MessageType.EntityDestroy:
                case MessageType.Effect:
                case MessageType.VoiceData:
                case MessageType.EntityFlags:
                    break;
                default:
                    break;
            }
        }

        private static async Task HandleIndexPage(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            string authCookie = null;
            CookieCollection cookies = request.Cookies;
            foreach (Cookie cookie in cookies)
            {
                if (cookie.Name == "auth")
                {
                    authCookie = cookie.Value;
                    break;
                }
            }
            bool isAuthenticated = !string.IsNullOrEmpty(authCookie) && authCookie == _authToken;
            await SendHtmlResponse(response, 200, HTML.GetIndexHtmlBytes(isAuthenticated));
        }

        private static async Task HandleServersApi(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            string authCookie = null;
            foreach (Cookie cookie in request.Cookies)
            {
                if (cookie.Name == "auth")
                {
                    authCookie = cookie.Value;
                    break;
                }
            }
            if (string.IsNullOrEmpty(authCookie) || authCookie != _authToken)
            {
                await SendJsonResponse(response, 401, new { error = "Unauthorized" });
                return;
            }
            await SendJsonResponse(response, 200, new { servers = _connectedServers });
        }

        private static async Task HandleServerDetailApi(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            string authCookie = null;
            foreach (Cookie cookie in request.Cookies)
            {
                if (cookie.Name == "auth")
                {
                    authCookie = cookie.Value;
                    break;
                }
            }
            if (string.IsNullOrEmpty(authCookie) || authCookie != _authToken)
            {
                await SendJsonResponse(response, 401, new { error = "Unauthorized" });
                return;
            }
            string path = request.Url.AbsolutePath;
            string wipeId = path.Replace("/api/server/", "").Replace("/api/server", "");
            if (string.IsNullOrEmpty(wipeId))
            {
                await SendJsonResponse(response, 400, new { error = "Server ID required" });
                return;
            }
            ServerInfo server = null;
            lock (_serversLock) { server = _connectedServers.FirstOrDefault(s => s.wipeId == wipeId); }
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
                    wipeId = server.wipeId,
                    connectedAt = server.connectedAt,
                    lastActivity = server.lastActivity,
                    packetsReceived = server.packetsReceived,
                    bytesReceived = server.bytesReceived,
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
            string authCookie = null;
            foreach (Cookie cookie in request.Cookies)
            {
                if (cookie.Name == "auth")
                {
                    authCookie = cookie.Value;
                    break;
                }
            }
            if (string.IsNullOrEmpty(authCookie) || authCookie != _authToken)
            {
                await SendJsonResponse(response, 401, new { error = "Unauthorized" });
                return;
            }
            string path = request.Url.AbsolutePath;
            string wipeId = path.Replace("/api/paths/", "").Split('/')[0];
            int page = 1;
            int.TryParse(request.QueryString["page"], out page);
            if (page < 1) page = 1;
            const int pageSize = 50;
            try
            {
                WorldSerialization worldSerialization = GetCachedWorldSerialization(wipeId);
                if (worldSerialization == null)
                {
                    await SendJsonResponse(response, 404, new { error = "No maps found" });
                    return;
                }
                var allPaths = worldSerialization.world.paths.Select((p, idx) => new { index = idx, name = p.name, spline = p.spline, start = p.start, end = p.end, width = p.width, nodes = p.nodes?.Count ?? 0 }).ToList();
                var totalPaths = allPaths.Count;
                var pagedPaths = allPaths.Skip((page - 1) * pageSize).Take(pageSize).ToList();
                await SendJsonResponse(response, 200, new { paths = pagedPaths, total = totalPaths, page = page, pageSize = pageSize, totalPages = (int)Math.Ceiling((double)totalPaths / pageSize) });
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
            string authCookie = null;
            foreach (Cookie cookie in request.Cookies)
            {
                if (cookie.Name == "auth")
                {
                    authCookie = cookie.Value;
                    break;
                }
            }
            if (string.IsNullOrEmpty(authCookie) || authCookie != _authToken)
            {
                await SendJsonResponse(response, 401, new { error = "Unauthorized" });
                return;
            }
            string path = request.Url.AbsolutePath;
            string wipeId = path.Replace("/api/prefabs/", "").Split('/')[0];
            int page = 1;
            int.TryParse(request.QueryString["page"], out page);
            if (page < 1) page = 1;
            string category = request.QueryString["category"];
            const int pageSize = 100;
            try
            {
                WorldSerialization worldSerialization = GetCachedWorldSerialization(wipeId);
                if (worldSerialization == null)
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
                        id = p.id,
                        name = GetStringFromPool(wipeId, p.id) ?? "Unknown",
                        position = $"{p.position.x:F2}, {p.position.y:F2}, {p.position.z:F2}"
                    })
                    .ToList();
                var totalPrefabs = prefabList.Count;
                var pagedPrefabs = prefabList.Skip((page - 1) * pageSize).Take(pageSize).ToList();
                await SendJsonResponse(response, 200, new { prefabs = pagedPrefabs, total = totalPrefabs, page = page, pageSize = pageSize, totalPages = (int)Math.Ceiling((double)totalPrefabs / pageSize), filterCategory = category ?? "all" });
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
            string authCookie = null;
            foreach (Cookie cookie in request.Cookies)
            {
                if (cookie.Name == "auth")
                {
                    authCookie = cookie.Value;
                    break;
                }
            }
            if (string.IsNullOrEmpty(authCookie) || authCookie != _authToken)
            {
                await SendJsonResponse(response, 401, new { error = "Unauthorized" });
                return;
            }
            string path = request.Url.AbsolutePath;
            var parts = path.Replace("/api/mapdata/", "").Split('/');
            string wipeId = parts[0];
            string layerName = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            try
            {
                WorldSerialization worldSerialization = GetCachedWorldSerialization(wipeId);
                if (worldSerialization == null)
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

        private static async Task HandleFileDownload(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            string authCookie = null;
            foreach (Cookie cookie in request.Cookies)
            {
                if (cookie.Name == "auth")
                {
                    authCookie = cookie.Value;
                    break;
                }
            }
            if (string.IsNullOrEmpty(authCookie) || authCookie != _authToken)
            {
                response.StatusCode = 401;
                byte[] buffer = Encoding.UTF8.GetBytes("Unauthorized");
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                return;
            }
            string path = request.Url.AbsolutePath;
            var parts = path.Replace("/api/download/", "").Split('/');
            if (parts.Length < 2)
            {
                response.StatusCode = 400;
                byte[] buffer = Encoding.UTF8.GetBytes("Invalid path");
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                return;
            }
            string wipeId = parts[0];
            string fileType = parts[1];
            string filename = parts.Length > 2 ? Uri.UnescapeDataString(string.Join("/", parts.Skip(2))) : "";
            try
            {
                string filePath = "";
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

        private static async Task Handle3DMapData(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            string authCookie = null;
            CookieCollection cookies = request.Cookies;
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
                await SendJsonResponse(response, 401, new { error = "Unauthorized" });
                return;
            }
            string wipeId = request.Url.AbsolutePath.Replace("/3dmap/data/", "").Replace("/3dmap/data/", "");
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
                string latestMapFile = mapFiles.Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).First().FullName;
                List<object[]> mapRoads = new List<object[]>();
                List<object[]> mapRails = new List<object[]>();
                List<object[]> mapRivers = new List<object[]>();
                List<object> prefabs = new List<object>();
                byte[] heightBytes = null;
                byte[] splatBytes = null;
                int heightRes = 0;
                int splatRes = 0;
                int worldsize = 4500;
                try
                {
                    WorldSerialization worldSerialization = new WorldSerialization();
                    worldSerialization.Load(latestMapFile);
                    worldsize = (int)worldSerialization.world.size;
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
                            string name = GetStringFromPool(wipeId, pd.id);
                            if (string.IsNullOrEmpty(name))
                                continue;

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
                            else if (name.Contains("monument") || name.Contains("unique_environment") || name.Contains("tunnel-entrance") || name.Contains("platform") || name.Contains("power substations") || name.Contains("iceberg") || name.Contains("ice_lakes"))
                            {
                                ProtoBuf.Vector3 postion = pd.position;
                                if (postion.y <= -499) { postion.y = 0; }
                                name = Path.GetFileNameWithoutExtension(name);
                                if (name.Contains("desert_military_base")) { name = "desert_military_base"; }
                                else if (name.Contains("mining_quarry")) { name = "mining_quarry"; }
                                else if (name.Contains("powerlineplatform")) { name = "powerlineplatform"; }
                                else if (name.Contains("entrance_bunker")) { name = "entrance_bunker"; }
                                else if (name.Contains("underwater_lab")) { name = "underwater_lab"; }
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
                        heightRes = (int)Math.Sqrt(heightBytes.Length / 2);
                    }
                    mapData = worldSerialization.GetMap("splat");
                    if (mapData?.data != null)
                    {
                        splatBytes = DownsampleMap(mapData.data, 8);
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
                    prefabs = prefabs,
                    river = mapRivers
                };

                response.AddHeader("Content-Encoding", "gzip");
                using (var ms = new MemoryStream())
                {
                    using (var compressionStream = new GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
                    using (var writer = new StreamWriter(compressionStream, new UTF8Encoding(false)))
                    {
                        var settings = new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore };
                        var serializer = JsonSerializer.Create(settings);
                        serializer.Serialize(writer, terrainData);
                        writer.Flush();
                    }
                    byte[] compressedBytes = ms.ToArray();
                    await SafeWrite(response, compressedBytes, "application/json");
                }
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
            var request = context.Request;
            var response = context.Response;
            string authCookie = null;
            CookieCollection cookies = request.Cookies;
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
                await SendJsonResponse(response, 401, new { error = "Unauthorized" });
                return;
            }
            string wipeId = request.Url.AbsolutePath.Replace("/3dviewer/", "").Replace("/3dviewer/", "");
            if (string.IsNullOrEmpty(wipeId))
            {
                await SendHtmlResponse(response, 302, "", new Dictionary<string, string> { { "Location", "/" } });
                return;
            }
            await SendHtmlResponse(context.Response, 200, HTML.Get3DViewerHtmlBytes(wipeId));
        }

        private static async Task HandleServerDetailPage(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            string authCookie = null;
            CookieCollection cookies = request.Cookies;
            foreach (Cookie cookie in cookies)
            {
                if (cookie.Name == "auth")
                {
                    authCookie = cookie.Value;
                    break;
                }
            }
            bool isAuthenticated = !string.IsNullOrEmpty(authCookie) && authCookie == _authToken;
            if (!isAuthenticated)
            {
                await SendHtmlResponse(response, 200, HTML.GetIndexHtmlBytes(isAuthenticated));
                return;
            }
            string path = request.Url.AbsolutePath;
            string wipeId = path.Replace("/server/", "").Replace("/server", "");
            if (string.IsNullOrEmpty(wipeId))
            {
                await SendHtmlResponse(response, 302, "", new Dictionary<string, string> { { "Location", "/" } });
                return;
            }
            await SendHtmlResponse(response, 200, HTML.GetServerDetailHtmlBytes(wipeId, isAuthenticated));
        }

        public static string DecryptMapDataName(int PreFabCount, string EncryptedData)
        {
            try
            {
                using (var aes = Aes.Create())
                {
                    var rfc2898DeriveBytes = new Rfc2898DeriveBytes(PreFabCount.ToString(), new byte[] { 73, 118, 97, 110, 32, 77, 101, 100, 118, 101, 100, 101, 118 });
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
                    var rfc2898DeriveBytes = new Rfc2898DeriveBytes(PreFabCount.ToString(), new byte[] { 73, 118, 97, 110, 32, 77, 101, 100, 118, 101, 100, 101, 118 });
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
            string password = null;
            if (body.Contains("password="))
            {
                int idx = body.IndexOf("password=") + 9;
                int end = body.IndexOf('&', idx);
                if (end < 0) end = body.Length;
                password = Uri.UnescapeDataString(body.Substring(idx, end - idx));
            }
            bool success = password == _authToken;
            if (success)
            {
                Cookie authCookie = new Cookie("auth", password);
                authCookie.Expires = DateTime.Now.AddDays(7);
                authCookie.Path = "/";
                response.Cookies.Add(authCookie);
            }
            await SendJsonResponse(response, success ? 200 : 401, new { success = success });
        }

        private static async Task SendHtmlResponse(HttpListenerResponse response, int statusCode, string html, Dictionary<string, string> additionalHeaders = null)
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

        private static async Task SendHtmlResponse(HttpListenerResponse response, int statusCode, byte[] html, Dictionary<string, string> additionalHeaders = null)
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

        private static async Task HandleStringPoolUpload(HttpListenerContext context, string wipeId)
        {
            var request = context.Request;
            var response = context.Response;

            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                string json = await reader.ReadToEndAsync();
                try
                {
                    var stringPool = SimpleJsonParser.ParseUintStringDict(json);
                    LogDebug($"[StringPool] Received {stringPool.Count} string entries");
                    OnStringPoolReceived(wipeId, stringPool);
                    MarkServerFileReceived(wipeId, "stringpool");
                    await SendJsonResponse(response, 200, new { success = true, count = stringPool.Count });
                }
                catch (Exception ex)
                {
                    LogDebug($"[StringPool] Parse error: {ex.Message}");
                    await SendJsonResponse(response, 400, new { error = "Invalid JSON format" });
                }
            }
        }

        private static async Task HandleManifestUpload(HttpListenerContext context, string wipeId)
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

        private static async Task HandleSnapshotUpload(HttpListenerContext context, string wipeId)
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

        private static async Task HandleMapSnapshotUpload(HttpListenerContext context, string wipeId)
        {
            var request = context.Request;
            var response = context.Response;
            try
            {
                var files = await ParseMultipartFormData(request);
                LogDebug($"[MapSnapshot] wipeId={wipeId}, files={files.Count}");
                foreach (var file in files)
                {
                    LogDebug($"  - {file.Key}: {file.Value.Length} bytes");
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
        private static MapInfo LoadMapInfo(string wipeId)
        {
            var mapInfo = new MapInfo();
            try
            {
                WorldSerialization worldSerialization = GetCachedWorldSerialization(wipeId);
                if (worldSerialization == null) { return mapInfo; }
                var tempdata = worldSerialization.GetMap("topology").data;
                var Topology = new int[tempdata.Length];
                Buffer.BlockCopy(tempdata, 0, Topology, 0, Topology.Length);
                MapRender mapRender = new MapRender(worldSerialization.GetMap("splat").data, Topology);
                mapInfo.png = CompressAndEncode(mapRender.Render());
                mapInfo.worldsize = (int)worldSerialization.world.size;
                mapInfo.mapCount = worldSerialization.world.maps.Count;
                mapInfo.prefabCount = worldSerialization.world.prefabs.Count;
                mapInfo.pathCount = worldSerialization.world.paths.Count;
                mapInfo.timestamp = worldSerialization.Timestamp;
                foreach (var map in worldSerialization.world.maps)
                {
                    if (MapLayers.Contains(map.name)) { mapInfo.mapNames.Add(map.name ?? "Unnamed"); }
                    else { mapInfo.mapNames.Add(DecryptMapDataName(mapInfo.prefabCount, map.name) ?? "Unnamed"); }
                }
                var categoryGroups = worldSerialization.world.prefabs.GroupBy(p => string.IsNullOrEmpty(p.category) ? "Unknown" : p.category).ToDictionary(g => g.Key, g => g.Count());
                mapInfo.prefabCategoryCounts = categoryGroups;
                mapInfo.prefabCategories = categoryGroups.Keys.OrderBy(k => k).ToList();
                foreach (var path in worldSerialization.world.paths) { if (!string.IsNullOrEmpty(path.name)) { mapInfo.pathNames.Add(path.name); } }
                var monuments = worldSerialization.GetCustomMonuments();
                mapInfo.customMonumentCount = monuments.Count();
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
            catch (Exception ex) { LogDebug($"[MAP INFO] Error loading map info: {ex.Message}"); }
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
            using (WebClient client = new())
            {
                try
                {
                    Console.WriteLine("Downloading Models...");
                    client.DownloadFile(url, zipPath);
                    Console.WriteLine("Extracting Models...");
                    ZipFile.ExtractToDirectory(zipPath, rootPath);
                    File.Delete(zipPath);
                }
                catch (Exception ex) { Console.WriteLine("Error: " + ex.Message); }
            }
        }

        private static async Task SendJsonResponse(HttpListenerResponse response, int statusCode, object data)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            string json = JsonConvert.SerializeObject(data);
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }

        private static void PrintStatistics()
        {
            lock (_consoleLock)
            {
                DateTime now = DateTime.UtcNow;
                int row = 0;
                Console.SetCursorPosition(0, row++);
                string globalLine = $"[STATS] Uptime: {GetUptimeSeconds():F0}s | WebSockets: {_webSocketClients.Count} | Packets: {Interlocked.Read(ref _totalPacketsReceived)}";
                Console.Write(globalLine.PadRight(Console.WindowWidth));
                lock (_serversLock)
                {
                    foreach (var server in _connectedServers)
                    {
                        Console.SetCursorPosition(0, row++);
                        string connectedStr = FormatReadableTime(now - server.connectedAt);
                        string activeStr = FormatReadableTime(now - server.lastActivity);
                        string serverLine = $"[Server] ID: {server.wipeId} | Up: {connectedStr} | Active: {activeStr} | Data: {FormatBytes(server.bytesReceived)}";
                        Console.Write(serverLine.PadRight(Console.WindowWidth - 1));
                    }
                }
                Console.SetCursorPosition(0, row++);
                Console.Write("".PadRight(Console.WindowWidth - 1));
                int newHeight = row;
                if (_headerHeight > newHeight)
                {
                    for (int i = newHeight; i <= _headerHeight; i++)
                    {
                        Console.SetCursorPosition(0, i);
                        Console.Write("".PadRight(Console.WindowWidth - 1));
                    }
                }
                _headerHeight = newHeight;
            }
        }

        public static void LogDebug(string message)
        {
            lock (_consoleLock)
            {
                if (Console.CursorTop < _headerHeight) { Console.SetCursorPosition(0, _headerHeight); }
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            }
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
            lock (_clientsLock)
            {
                foreach (var client in _webSocketClients)
                {
                    try
                    {
                        client.Socket.CloseAsync(
                            System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                            "Server shutdown",
                            CancellationToken.None).Wait(1000);
                    }
                    catch { }
                }
                _webSocketClients.Clear();
            }
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

        private static void TrackServerActivity(string wipeId)
        {
            if (string.IsNullOrEmpty(wipeId)) return;
            lock (_serversLock)
            {
                var server = _connectedServers.FirstOrDefault(s => s.wipeId == wipeId);
                if (server == null)
                {
                    server = new ServerInfo
                    {
                        wipeId = wipeId,
                        connectedAt = DateTime.UtcNow,
                        lastActivity = DateTime.UtcNow
                    };
                    _connectedServers.Add(server);
                    LogDebug($"[SERVER] New server tracked: {wipeId}");
                }
                else
                {
                    server.lastActivity = DateTime.UtcNow;
                }
            }
        }

        private static void UpdateServerStats(string wipeId, int bytesReceived)
        {
            if (string.IsNullOrEmpty(wipeId)) return;
            lock (_serversLock)
            {
                var server = _connectedServers.FirstOrDefault(s => s.wipeId == wipeId);
                if (server != null)
                {
                    server.packetsReceived++;
                    server.bytesReceived += bytesReceived;
                    server.lastActivity = DateTime.UtcNow;
                }
            }
        }

        private static void MarkServerFileReceived(string wipeId, string fileType)
        {
            if (string.IsNullOrEmpty(wipeId)) return;

            lock (_serversLock)
            {
                var server = _connectedServers.FirstOrDefault(s => s.wipeId == wipeId);
                if (server != null)
                {
                    if (!server.receivedFiles.Contains(fileType)) { server.receivedFiles.Add(fileType); }
                    server.lastActivity = DateTime.UtcNow;
                }
            }
        }

        private static string GetStringFromPool(string wipeId, uint id)
        {
            lock (_stringPoolLock)
            {
                if (_globalStringPool.TryGetValue(id, out string cachedValue)) { return cachedValue; }
                LoadWipeStringToGlobalPool(wipeId);
                if (_globalStringPool.TryGetValue(id, out string newValue)) { return newValue; }
                return SearchAllWipesForId(id);
            }
        }

        private static void LoadWipeStringToGlobalPool(string wipeId)
        {
            string stringPoolsDir = Path.Combine(DataDirectory, wipeId, "StringPools");
            if (!Directory.Exists(stringPoolsDir)) return;
            var latestFile = Directory.GetFiles(stringPoolsDir, "*.json").Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (latestFile == null) { return; }
            try
            {
                foreach (string line in File.ReadAllLines(latestFile.FullName))
                {
                    int colonIndex = line.IndexOf(':');
                    if (colonIndex <= 0) { continue; }
                    string keyPart = line.Substring(0, colonIndex).Trim(' ', '"');
                    string valuePart = line.Substring(colonIndex + 1).Trim(' ', '"', ',', '\r', '\n');
                    if (uint.TryParse(keyPart, out uint parsedId) && !string.IsNullOrEmpty(valuePart))
                    {
                        if (!_globalStringPool.ContainsKey(parsedId)) { _globalStringPool[parsedId] = valuePart; }
                    }
                }
            }
            catch (Exception ex) { LogDebug($"[StringPool] Error loading {latestFile.Name}: {ex.Message}"); }
        }

        private static string SearchAllWipesForId(uint id)
        {
            var wipeDirs = Directory.GetDirectories(DataDirectory);
            foreach (var dir in wipeDirs)
            {
                string currentWipeId = Path.GetFileName(dir);
                LoadWipeStringToGlobalPool(currentWipeId);
                if (_globalStringPool.TryGetValue(id, out string found)) { return found; }
            }
            return null;
        }

        public static string CompressAndEncode(byte[] data)
        {
            if (data == null || data.Length == 0) { return string.Empty; }
            using (var outputStream = new MemoryStream())
            {
                using (var gZipStream = new GZipStream(outputStream, System.IO.Compression.CompressionLevel.Optimal, true)) { gZipStream.Write(data, 0, data.Length); }
                return Convert.ToBase64String(outputStream.GetBuffer(), 0, (int)outputStream.Length);
            }
        }

        private static byte[] DownsampleMap(byte[] data, int stepSize)
        {
            if (data == null || data.Length == 0) return null;
            int srcRes = (int)Math.Sqrt(data.Length / stepSize);
            int dstRes = srcRes / 2;
            if (dstRes < 64) dstRes = 64;
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
        private static string ExtractFormName(string headers)
        {
            int nameIndex = headers.IndexOf("name=\"");
            if (nameIndex >= 0)
            {
                int start = nameIndex + 6;
                int end = headers.IndexOf("\"", start);
                if (end > start) { return headers.Substring(start, end - start); }
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
                if (end > start) { return headers.Substring(start, end - start); }
            }
            return null;
        }

        private static string ExtractOriginalFilename(string headers)
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
                if (end > start) { return headers.Substring(start, end - start); }
            }
            return null;
        }

        private static int FindBytes(byte[] haystack, byte[] needle, int startPos)
        {
            if (haystack == null || needle == null) { return -1; }
            if (needle.Length == 0) { return startPos; }
            if (haystack.Length == 0) { return -1; }
            if (startPos < 0) { startPos = 0; }
            if (startPos >= haystack.Length) { return -1; }
            for (int i = startPos; i <= haystack.Length - needle.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        found = false;
                        break;
                    }
                }
                if (found) return i;
            }
            return -1;
        }

        private static void SaveMapFileToDirectory(string wipeId, string filename, byte[] data)
        {
            try
            {
                string mapDir = Path.Combine(DataDirectory, wipeId, "Maps");
                Directory.CreateDirectory(mapDir);
                string filepath = Path.Combine(mapDir, SanitizeFilename(filename));
                lock (FileWriteLock) { File.WriteAllBytes(filepath, data); }
                LogDebug($"[STORAGE] Map file saved: {filepath}");
            }
            catch (Exception ex) { LogDebug($"[STORAGE] Failed to save Map file: {ex.Message}"); }
        }

        private static WorldSerialization GetCachedWorldSerialization(string wipeId)
        {
            lock (_mapCacheLock)
            {
                string mapsDir = Path.Combine(DataDirectory, wipeId, "Maps");
                if (!Directory.Exists(mapsDir)) return null;
                var mapFiles = Directory.GetFiles(mapsDir, "*.map");
                if (mapFiles.Length == 0) return null;
                string latestMapFile = mapFiles.Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).First().FullName;
                string cacheKey = $"{wipeId}:{latestMapFile}";
                if (_mapCache.TryGetValue(cacheKey, out var cached) && cached.loadedAt > DateTime.UtcNow.AddMinutes(-MAP_CACHE_TTL_MINUTES) && cached.filePath == latestMapFile)
                {
                    LogDebug($"[MAP CACHE] Hit for {wipeId}");
                    return cached.ws;
                }
                LogDebug($"[MAP CACHE] Miss for {wipeId}, loading: {latestMapFile}");
                var ws = new WorldSerialization();
                ws.Load(latestMapFile);
                _mapCache[cacheKey] = (ws, DateTime.UtcNow, latestMapFile);
                return ws;
            }
        }

        private static string ExtractFilename(string headers)
        {
            int nameIndex = headers.IndexOf("filename=\"");
            if (nameIndex < 0) { return null; }
            int start = nameIndex + 10;
            int end = headers.IndexOf("\"", start);
            if (end < 0) { return null; }
            return headers.Substring(start, end - start);
        }

        private static void SaveStringPoolToFile(string wipeId, Dictionary<uint, string> stringPool)
        {
            try
            {
                string filename = $"stringpool.json";
                string filepath = Path.Combine(DataDirectory, wipeId, "StringPools", SanitizeFilename(filename));
                var sb = new StringBuilder();
                sb.AppendLine("{");
                bool first = true;
                foreach (var kvp in stringPool.OrderBy(x => x.Key))
                {
                    if (!first) sb.AppendLine(",");
                    first = false;
                    sb.Append($"  \"{kvp.Key}\": \"{EscapeJsonString(kvp.Value)}\"");
                }
                sb.AppendLine();
                sb.AppendLine("}");
                lock (FileWriteLock) { File.WriteAllText(filepath, sb.ToString()); }
                LogDebug($"[STORAGE] StringPool saved: {filepath}");
            }
            catch (Exception ex) { LogDebug($"[STORAGE] Failed to save StringPool: {ex.Message}"); }
        }

        private static void SaveManifestToFile(string wipeId, Dictionary<uint, string> manifest)
        {
            try
            {
                string filename = $"manifest.json";
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
                LogDebug($"[STORAGE] Manifest saved: {filepath}");
            }
            catch (Exception ex) { LogDebug($"[STORAGE] Failed to save Manifest: {ex.Message}"); }
        }

        private static void SaveSnapshotToFile(string wipeId, byte[] data)
        {
            try
            {
                string snapshotDir = Path.Combine(DataDirectory, wipeId, "Snapshots");
                Directory.CreateDirectory(snapshotDir);
                var existingFiles = new DirectoryInfo(snapshotDir).GetFiles("snapshot_*.sav").OrderByDescending(f => f.CreationTimeUtc).ToList();
                foreach (var oldFile in existingFiles.Skip(3)) { try { oldFile.Delete(); } catch { } }
                string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                string filename = $"snapshot_{timestamp}.sav";
                string filepath = Path.Combine(snapshotDir, SanitizeFilename(filename));
                lock (FileWriteLock) { File.WriteAllBytes(filepath, data); }
                LogDebug($"[STORAGE] Snapshot saved: {filepath} ({data.Length} bytes)");
            }
            catch (Exception ex) { LogDebug($"[STORAGE] Failed to save Snapshot: {ex.Message}"); }
        }

        private static string ComputeChecksum(byte[] data)
        {
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(data);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
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
     #endregion
    }

    #region Classes
    public static class SimpleJsonParser
    {
        public static Dictionary<uint, string> ParseUintStringDict(string json)
        {
            var result = new Dictionary<uint, string>();
            json = json.Trim();
            if (json.StartsWith("{") && json.EndsWith("}"))
            {
                json = json.Substring(1, json.Length - 2);
                var pairs = SplitJsonObjects(json);
                foreach (var pair in pairs)
                {
                    int colonIndex = pair.IndexOf(':');
                    if (colonIndex > 0)
                    {
                        string keyStr = pair.Substring(0, colonIndex).Trim().Trim('"');
                        string valueStr = pair.Substring(colonIndex + 1).Trim().Trim('"');
                        uint key;
                        if (uint.TryParse(keyStr, out key)) { result[key] = valueStr; }
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
                        result.Add(json.Substring(start, i - start));
                        start = i + 1;
                    }
                }
            }
            if (start < json.Length) { result.Add(json.Substring(start)); }
            return result;
        }
    }

    public enum MessageType : byte
    {
        First,
        Welcome,
        Auth,
        Approved,
        Ready,
        Entities,
        EntityDestroy,
        GroupChange,
        GroupDestroy,
        RPCMessage,
        EntityPosition,
        ConsoleMessage,
        ConsoleCommand,
        Effect,
        DisconnectReason,
        Tick,
        Message,
        RequestUserInformation,
        GiveUserInformation,
        GroupEnter,
        GroupLeave,
        VoiceData,
        EAC,
        EntityFlags,
        World,
        ConsoleReplicatedVars,
        QueueUpdate,
        SyncVar,
        PackedSyncVar,
        Last = 28,
        Count,
        DemoDisconnection = 50,
        DemoTransientEntities
    }

    public class ServerInfo
    {
        public string wipeId;
        public DateTime connectedAt;
        public DateTime lastActivity;
        public long packetsReceived;
        public long bytesReceived;
        public List<string> receivedFiles = new List<string>();
    }

    public class MapInfo
    {
        public int worldsize;
        public int mapCount;
        public int prefabCount;
        public int pathCount;
        public int customMonumentCount;
        public List<string> mapNames = new List<string>();
        public List<string> prefabCategories = new List<string>();
        public Dictionary<string, int> prefabCategoryCounts = new Dictionary<string, int>();
        public List<string> pathNames = new List<string>();
        public List<object> customMonuments = new List<object>();
        public long timestamp;
        public string png;
    }
    #endregion

    #region Networking
    namespace ProtoBuf
    {
        [ProtoContract]
        public class Entity
        {
            [ProtoMember(1)]
            public BaseEntity baseEntity { get; set; }

            [ProtoMember(2)]
            public BaseNetworkable baseNetworkable { get; set; }
        }

        [ProtoContract]
        public class BaseNetworkable
        {
            [ProtoMember(1)]
            public string prefabName { get; set; }

            [ProtoMember(2)]
            public uint netID { get; set; }
        }

        [ProtoContract]
        public class BaseEntity
        {
            [ProtoMember(1)]
            public ulong OwnerID { get; set; }

            [ProtoMember(2)]
            public Vector position { get; set; }

            [ProtoMember(3)]
            public Vector rotation { get; set; }

            [ProtoMember(4)]
            public string displayName { get; set; }
        }

        [ProtoContract]
        public class Vector
        {
            [ProtoMember(1)]
            public float x { get; set; }

            [ProtoMember(2)]
            public float y { get; set; }

            [ProtoMember(3)]
            public float z { get; set; }
        }
    }
    #endregion

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
        public MapData GetMap(string name)
        {
            for (int i = 0; i < world.maps.Count; i++)
            {
                if (world.maps[i].name == name) { return world.maps[i]; }
            }
            return null;
        }

        public List<MapData> GetCustomMonuments() { return world.maps.Where((MapData x) => x.name.StartsWith("CustomMonument_") || x.name.StartsWith(":")).ToList(); }

        public void AddMap(string name, byte[] data)
        {
            MapData mapData = new MapData();
            mapData.name = name;
            mapData.data = data;
            world.maps.Add(mapData);
        }

        public IEnumerable<PrefabData> GetPrefabs(string category) { return world.prefabs.Where((PrefabData p) => p.category == category); }

        public IEnumerable<PathData> GetPaths(string name) { return world.paths.Where((PathData p) => p.name.Contains(name)); }

        public PathData GetPath(string name)
        {
            for (int i = 0; i < world.paths.Count; i++) { if (world.paths[i].name == name) { return world.paths[i]; } }
            return null;
        }

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
                        using (LZ4Stream lz4Stream = new LZ4Stream(fileStream, LZ4StreamMode.Decompress, LZ4StreamFlags.None, 1048576))
                        {
                            try
                            {
                                using (MemoryStream memoryStream = new MemoryStream())
                                {
                                    lz4Stream.CopyTo(memoryStream);
                                    memoryStream.Position = 0L;
                                    world = WorldData.Deserialize(memoryStream);
                                }
                            }
                            catch
                            {
                                Console.WriteLine("Failed to deserialize map. Falling back to ProtoBuf.Deserialize");
                                world = OldSerialization.Deserialize(lz4Stream);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { RustRelayReceiver.Program.LogDebug(ex.Message); }
        }

        public WorldData world = new WorldData();

        [ProtoContract]
        [Serializable]
        public class ExportData
        {
            [ProtoMember(1)]
            public uint size = 4000U;

            [ProtoMember(2)]
            public string name;

            [ProtoMember(3)]
            public byte[] data;

            [ProtoMember(4)]
            public int res;
        }
    }

    namespace ProtoBuf
    {
        [ProtoContract]
        [Serializable]
        public class MapData
        {
            [ProtoMember(1)]
            public string name;

            [ProtoMember(2)]
            public byte[] data;
        }

        [ProtoContract]
        [Serializable]
        public class PathData
        {
            public PathData() { }

            public PathData(PathData pathData)
            {
                name = pathData.name;
                spline = pathData.spline;
                start = pathData.start;
                end = pathData.end;
                innerPadding = pathData.innerPadding;
                outerPadding = pathData.outerPadding;
                innerFade = pathData.innerFade;
                outerFade = pathData.outerFade;
                randomScale = pathData.randomScale;
                width = pathData.width;
                meshOffset = pathData.meshOffset;
                terrainOffset = pathData.terrainOffset;
                splat = pathData.splat;
                topology = pathData.topology;
                nodes = new List<VectorData>();
            }

            [ProtoMember(1)]
            public string name;

            [ProtoMember(2)]
            public bool spline;

            [ProtoMember(3)]
            public bool start;

            [ProtoMember(4)]
            public bool end;

            [ProtoMember(5)]
            public float width;

            [ProtoMember(6)]
            public float innerPadding;

            [ProtoMember(7)]
            public float outerPadding;

            [ProtoMember(8)]
            public float innerFade;

            [ProtoMember(9)]
            public float outerFade;

            [ProtoMember(10)]
            public float randomScale;

            [ProtoMember(11)]
            public float meshOffset;

            [ProtoMember(12)]
            public float terrainOffset;

            [ProtoMember(13)]
            public int splat;

            [ProtoMember(14)]
            public int topology;

            [ProtoMember(15)]
            public List<VectorData> nodes;

            [ProtoMember(16)]
            public int hierarchy;
        }

        [ProtoContract]
        [Serializable]
        public class PrefabData
        {
            [ProtoMember(1)]
            public string category;

            [ProtoMember(2)]
            public uint id;

            [ProtoMember(3)]
            public VectorData position;

            [ProtoMember(4)]
            public VectorData rotation;

            [ProtoMember(5)]
            public VectorData scale;
        }

        public class Vector3
        {
            public float x;
            public float y;
            public float z;

            public Vector3(float x, float y, float z)
            {
                this.x = x;
                this.y = y;
                this.z = z;
            }
        }

        public class Quaternion
        {
            public float x;
            public float y;
            public float z;
            public float w;

            public Quaternion(float x, float y, float z, float w)
            {
                this.x = x;
                this.y = y;
                this.z = z;
                this.w = w;
            }
        }

        [ProtoContract]
        [Serializable]
        public struct VectorData
        {
            public VectorData(float x, float y, float z)
            {
                this.x = x;
                this.y = y;
                this.z = z;
            }

            public static implicit operator VectorData(Vector3 v)
            {
                return new VectorData(v.x, v.y, v.z);
            }

            public static implicit operator VectorData(Quaternion q)
            {
                return q;
            }

            public static implicit operator Vector3(VectorData v)
            {
                return new Vector3(v.x, v.y, v.z);
            }

            [ProtoMember(1)]
            public float x;

            [ProtoMember(2)]
            public float y;

            [ProtoMember(3)]
            public float z;

        }

        [ProtoContract]
        [Serializable]
        public class WorldData
        {
            public static void Serialize(Stream stream, WorldData data) { Serializer.Serialize<WorldData>(stream, data); }

            public static WorldData Deserialize(Stream stream) { return Serializer.Deserialize<WorldData>(stream); }

            [ProtoMember(1)]
            public uint size = 4000U;

            [ProtoMember(2)]
            public List<MapData> maps = new List<MapData>();

            [ProtoMember(3)]
            public List<PrefabData> prefabs = new List<PrefabData>();

            [ProtoMember(4)]
            public List<PathData> paths = new List<PathData>();
        }

        public class OldSerialization
        {
            public static WorldData Deserialize(Stream stream) { return Serializer.Deserialize<WorldData>(stream); }
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

        public static Array2D<Color> output;

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

        public double ScaleValue(double value, double fromMin, double fromMax, double toMin, double toMax)
        {
            if (fromMax == fromMin) return toMin;
            double ratio = (value - fromMin) / (fromMax - fromMin);
            return ratio * (toMax - toMin) + toMin;
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

        public struct Vec4
        {
            public float x, y, z, w;
            public Vec4(float x, float y, float z, float w = 1f) { this.x = x; this.y = y; this.z = z; this.w = w; }
            public static Vec4 operator *(Vec4 v, float f) => new Vec4(v.x * f, v.y * f, v.z * f, v.w * f);
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

        public byte[] Render()
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
        }
        .container { max-width: 1400px; margin: 0 auto; padding: 20px; }
        h1 { color: " + AccentSecondary + @"; margin-bottom: 20px; }
        h2 { color: " + AccentPrimary + @"; margin: 0 0 15px; }
        h3 { color: " + AccentSecondary + @"; margin: 15px 0 10px; }
        .card {
            background: " + BackgroundSecondary + @";
            border-radius: 10px;
            padding: 20px;
            margin-bottom: 20px;
        }
        .subtitle { color: " + TextMuted + @"; margin-bottom: 20px; }
        .back-link { color: " + AccentPrimary + @"; text-decoration: none; margin-bottom: 20px; display: inline-block; }
        .back-link:hover { text-decoration: underline; }
        .login-form { max-width: 300px; margin: 100px auto; }
        input[type=""password""], input[type=""text""] {
            width: 100%;
            padding: 12px;
            border: 1px solid " + BorderDefault + @";
            border-radius: 5px;
            background: " + BackgroundDark + @";
            color: " + TextPrimary + @";
            margin-bottom: 10px;
        }
        button, .action-btn {
            width: 100%;
            padding: 12px;
            background: " + AccentSecondary + @";
            border: none;
            border-radius: 5px;
            color: #fff;
            cursor: pointer;
            font-size: 16px;
            font-weight: bold;
            transition: background 0.2s ease;
        }
        button:hover, .action-btn:hover { background: #ff5252; }
        .error { color: " + AccentSecondary + @"; margin-top: 10px; display: none; }
        table { width: 100%; border-collapse: collapse; margin-top: 10px; table-layout: fixed; }
        th, td { padding: 12px; text-align: left; border-bottom: 1px solid " + BorderDefault + @"; }
        th { background: " + BackgroundDark + @"; color: " + AccentPrimary + @"; }
        tr:hover { background: #1f3050; }
        tr.clickable { cursor: pointer; }
        .stats {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
            gap: 15px;
            margin-bottom: 20px;
        }
        .wipe-id-col { width: 280px; min-width: 280px; }
        .stat-box {
            background: " + BackgroundDark + @";
            padding: 15px;
            border-radius: 8px;
            text-align: center;
        }
        .stat-value { font-size: 24px; font-weight: bold; color: " + AccentPrimary + @"; }
        .stat-label { font-size: 12px; color: " + TextSecondary + @"; margin-top: 5px; }
        .refresh-btn { background: " + AccentPrimary + @"; color: #000; }
        .refresh-btn:hover { background: #3dbdb5; }
        .logout-btn { background: " + TextMuted + @"; margin-top: 20px; }
        .logout-btn:hover { background: #555; }
        .last-update { color: " + TextMuted + @"; font-size: 12px; margin-top: 10px; }
        .badge {
            display: inline-block;
            padding: 4px 8px;
            border-radius: 4px;
            font-size: 11px;
            margin-right: 5px;
        }
        .badge-success { background: rgba(100, 200, 100, 0.2); color: " + Success + @"; }
        .badge-warning { background: rgba(255, 193, 7, 0.2); color: " + Warning + @"; }
        .badge-info { background: rgba(0, 217, 255, 0.2); color: " + AccentInfo + @"; }
        .action-btn.secondary { background: " + TextMuted + @"; color: #fff; }
        .action-btn.secondary:hover { background: #555; }
        .action-buttons { display: flex; gap: 10px; flex-wrap: wrap; margin-top: 15px; }
        .loading { text-align: center; padding: 40px; color: " + TextSecondary + @"; }
        .error-box {
            background: rgba(255, 107, 107, 0.1);
            border: 1px solid " + AccentSecondary + @";
            border-radius: 8px;
            padding: 15px;
            color: " + AccentSecondary + @";
        }
        .info-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
            gap: 10px;
        }
        .info-item { background: " + BackgroundDark + @"; padding: 12px; border-radius: 6px; }
        .info-item .label { font-size: 11px; color: " + TextMuted + @"; text-transform: uppercase; }
        .info-item .value { font-size: 14px; color: " + AccentPrimary + @"; margin-top: 4px; }
        .clickable { cursor: pointer; }
        .clickable:hover { opacity: 0.8; }
        .badge.clickable:hover { background: rgba(0, 217, 255, 0.4); }
        .detail-link { color: " + AccentPrimary + @"; text-decoration: none; font-size: 12px; }
        .detail-link:hover { text-decoration: underline; }
        .pos-cell { font-family: monospace; font-size: 11px; word-break: break-word; }
        .modal {
            display: none;
            position: fixed;
            top: 0;
            left: 0;
            width: 100%;
            height: 100%;
            background: rgba(0,0,0,0.7);
            z-index: 1000;
        }
        .modal.show { display: flex; align-items: center; justify-content: center; }
        .modal-content {
            background: " + BackgroundSecondary + @";
            border-radius: 10px;
            padding: 20px;
            max-width: 1400px;
            width: 95%;
            max-height: 90vh;
            overflow-y: auto;
            overflow-x: hidden;
        }
        .modal-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 15px; }
        .modal-header h2 { margin: 0; }
        .close-btn { background: none; border: none; color: " + AccentSecondary + @"; font-size: 24px; cursor: pointer; width: auto; padding: 0; }
        .close-btn:hover { color: #ff5252; }
        .pagination { display: flex; justify-content: center; align-items: center; gap: 10px; margin-top: 15px; }
        .pagination button {
            padding: 8px 16px;
            background: " + AccentPrimary + @";
            border: none;
            border-radius: 5px;
            color: #000;
            cursor: pointer;
            width: auto;
        }
        .pagination button:disabled { background: " + TextMuted + @"; cursor: not-allowed; }
        .pagination button:hover:not(:disabled) { background: #3dbdb5; }
        .pagination span { color: " + TextSecondary + @"; }
        .filter-bar { display: flex; gap: 10px; margin-bottom: 15px; flex-wrap: wrap; }
        .filter-bar .badge { cursor: pointer; }
        .filter-bar .badge.active { background: " + AccentPrimary + @"; color: #000; }
        .download-link { color: " + AccentPrimary + @"; cursor: pointer; text-decoration: underline; }
        .download-link:hover { color: #3dbdb5; }
        .map-preview {
            display: inline-block;
            margin-bottom: 8px;
            cursor: zoom-in;
        }
        .map-preview img {
            max-width: 120px;
            border-radius: 6px;
            border: 2px solid " + AccentPrimary + @";
            background: " + BackgroundDark + @";
            transition: transform 0.15s ease;
        }
        .map-preview:hover img { transform: scale(1.03); }
        .map-preview-overlay {
            display: none;
            position: fixed;
            inset: 0;
            background: rgba(0,0,0,0.85);
            z-index: 2000;
            align-items: center;
            justify-content: center;
        }
        .map-preview-overlay img {
            max-width: 95%;
            max-height: 95%;
            border-radius: 10px;
            box-shadow: 0 0 40px rgba(0,0,0,0.6);
        }
        p { color: " + TextSecondary + @"; margin-bottom: 15px; }";

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
            #layer-toggle { flex-wrap: wrap; justify-content: center; max-width: 90%; }
            #layer-toggle label { font-size: 11px; }
            #info { font-size: 11px; padding: 10px 15px; }
            .viewer-btn { display: none; }
        }";
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
            if (cached != null) return cached;

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
<button id=""back-btn"" class=""viewer-btn"" onclick=""window.history.back()"">Back</button>
<button id=""noclip-btn"" class=""viewer-btn"">No Clip</button>
<div id=""layer-toggle"">
<label><input type=""checkbox"" id=""showDebug""> Debug</label>
<label><input type=""checkbox"" id=""showRoads"" checked> Roads</label>
<label><input type=""checkbox"" id=""showRails"" checked> Rails</label>
<label><input type=""checkbox"" id=""showPrefabs"" checked> Prefabs</label>
<label><input type=""checkbox"" id=""showBB"" checked> PreventBuilding</label>
<label><input type=""checkbox"" id=""showWater"" checked> Water</label>
<label><input type=""checkbox"" id=""showLabels"" checked> Labels</label>
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
let BBMarkers = [];
let isPointerLocked = false;
const tempMatrix = new THREE.Matrix4();
const prefabOffsetCache = new Map();
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
let waterMesh = null;
let roadInstancer, railInstancer, riverInstancer;
const debugEl = document.getElementById('debug-info');
const ROAD_HEIGHT_OFFSET = 0.5;
const RAIL_HEIGHT_OFFSET = 0.8;
const RIVER_HEIGHT_OFFSET = 0.3;
const SEGMENT_FORWARD = new THREE.Vector3(0, 0, 1);
const CHECKBOX_STATE_KEY = 'mapViewer.layerStates';
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
function isBBMarkerType(prefabName) { return BBMarkerConfig.hasOwnProperty(prefabName); }
function getBBConfig(label) { return BBMarkerConfig[label] || null; }
function createBBSphereMarker(x, y, z, radius, label, opacity) {
    opacity = opacity || 0.4;
    const geometry = new THREE.SphereGeometry(radius, 32, 32);
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
    const pos = prefab.position || prefab.Position || prefab.pos || prefab.Postion;
    if (!pos) { return; }
    const rot = prefab.rotation || prefab.Rotation || prefab.rot || { x: 0, y: 0, z: 0 };
    const prefabName = prefab.Name || 'Unknown';
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
if (showDebug) { debugEl.classList.remove('hidden');} else { debugEl.classList.add('hidden');}
function applyInitialVisibility() {
    if (roadInstancer) roadInstancer.visible = showRoads;
    if (railInstancer) railInstancer.visible = showRails;
    if (waterMesh) waterMesh.visible = showWater;
    if (riverInstancer) riverInstancer.visible = showWater;
    prefabMarkers.forEach(function(m) { m.visible = showPrefabs; });
    BBMarkers.forEach(function(m) { m.visible = showBB; });
    prefabLabels.forEach(function(m) { m.visible = showLabels; });
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
function initInstancers(terrainData) {
const maxRoadSegs = terrainData.roads ? terrainData.roads.reduce(function(s, r) { return s + r.length; }, 0) : 0;
const maxRailSegs = terrainData.rail ? terrainData.rail.reduce(function(s, r) { return s + r.length; }, 0) : 0;
const maxRiverSegs = terrainData.river ? terrainData.river.reduce(function(s, r) { return s + r.length; }, 0) : 0;
roadInstancer = createSegmentInstancer(maxRoadSegs, 8, 0.5, 0x111111);
railInstancer = createSegmentInstancer(maxRailSegs, 3, 0.5, 0x3d2b1f);
riverInstancer = createSegmentInstancer(maxRiverSegs, 8, 0.2, 0x87CEEB);
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
setupNoclipButton();
setupLayerToggleHandlers();
setTimeout(function() { document.getElementById('loading').classList.add('hidden'); }, 1000);
animate();
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
window.splatResolution = terrainData.splatMapResolution || terrainData.heightMapResolution;
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
} else {
r = colorMap.grass[0]; g = colorMap.grass[1]; b = colorMap.grass[2];
}
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
log('Terrain mesh created', 'success');
}
function createWaterMesh() {
const waterHeight = 0.01;
const waterGeo = new THREE.PlaneGeometry(TERRAIN_SIZE, TERRAIN_SIZE, 1, 1);
const waterMat = new THREE.MeshPhongMaterial({ color: 0x1a5f7a, transparent: true, opacity: 0.75, shininess: 100, depthWrite: true });
waterMesh = new THREE.Mesh(waterGeo, waterMat);
waterMesh.rotation.x = -Math.PI / 2;
waterMesh.position.y = waterHeight;
waterMesh.renderOrder = 1;
scene.add(waterMesh);
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
function initGLTFLoader() { if (typeof THREE.GLTFLoader !== 'undefined') { gltfLoader = new THREE.GLTFLoader(); } else { log('GLTFLoader not available, will use cube markers', 'error'); } }
function loadGLBModel(url, onLoad, onError) {
if (!gltfLoader) { if (onError) onError('GLTFLoader not initialized'); return; }
gltfLoader.load(url, function(gltf) { if (onLoad) onLoad(gltf); }, function(progress) { }, function(error) { if (onError) onError(error); });
}
async function loadPrefabOffset(prefabName) {
if (prefabOffsetCache.has(prefabName)) { return await prefabOffsetCache.get(prefabName); }
const fetchPromise = (async function() {
const offsetUrl = window.location.origin + '/models/' + prefabName + '.json';
try {
const response = await fetch(offsetUrl);
if (!response.ok) throw new Error('Status: ' + response.status);
const data = await response.json();
return normalizeOffsets(data);
} catch (e) { console.warn('Load failed for ' + prefabName + ', using defaults.', e.message); return normalizeOffsets(null); }
})();
prefabOffsetCache.set(prefabName, fetchPromise);
return await fetchPromise;
}
function normalizeOffsets(data) {
const d = data || {};
return {
positionOffset: { x: d.positionOffset && d.positionOffset.x !== undefined ? d.positionOffset.x : (d.localPositionOffset && d.localPositionOffset.x !== undefined ? d.localPositionOffset.x : 0), y: d.positionOffset && d.positionOffset.y !== undefined ? d.positionOffset.y : (d.localPositionOffset && d.localPositionOffset.y !== undefined ? d.localPositionOffset.y : 0), z: d.positionOffset && d.positionOffset.z !== undefined ? d.positionOffset.z : (d.localPositionOffset && d.localPositionOffset.z !== undefined ? d.localPositionOffset.z : 0) },
rotationOffset: { x: d.rotationOffset && d.rotationOffset.x !== undefined ? d.rotationOffset.x : (d.localRotationOffset && d.localRotationOffset.x !== undefined ? d.localRotationOffset.x : 0), y: d.rotationOffset && d.rotationOffset.y !== undefined ? d.rotationOffset.y : (d.localRotationOffset && d.localRotationOffset.y !== undefined ? d.localRotationOffset.y : 0), z: d.rotationOffset && d.rotationOffset.z !== undefined ? d.rotationOffset.z : (d.localRotationOffset && d.localRotationOffset.z !== undefined ? d.localRotationOffset.z : 0) },
scaleMultiplier: { x: d.scaleMultiplier && d.scaleMultiplier.x !== undefined ? d.scaleMultiplier.x : (d.localScaleOffset && d.localScaleOffset.x !== undefined ? d.localScaleOffset.x : 1), y: d.scaleMultiplier && d.scaleMultiplier.y !== undefined ? d.scaleMultiplier.y : (d.localScaleOffset && d.localScaleOffset.y !== undefined ? d.localScaleOffset.y : 1), z: d.scaleMultiplier && d.scaleMultiplier.z !== undefined ? d.scaleMultiplier.z : (d.localScaleOffset && d.localScaleOffset.z !== undefined ? d.localScaleOffset.z : 1) }
};
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
const pos = prefab.position || prefab.Position || prefab.pos || prefab.Postion;
if (!pos) { return; }
const unityX = pos[0] !== undefined ? pos[0] : (pos.x || 0);
const unityY = pos[1] !== undefined ? pos[1] : (pos.y || 0);
const unityZ = pos[2] !== undefined ? pos[2] : (pos.z || 0);
const rot = prefab.rotation || prefab.Rotation || prefab.rot || { x: 0, y: 0, z: 0 };
const unityRot = { x: rot[0] !== undefined ? rot[0] : (rot.x || 0), y: rot[1] !== undefined ? rot[1] : (rot.y || 0), z: rot[2] !== undefined ? rot[2] : (rot.z || 0) };
const scaleData = prefab.scale || prefab.Scale || { x: 1, y: 1, z: 1 };
const baseScale = { x: scaleData[0] !== undefined ? scaleData[0] : (scaleData.x || 1), y: scaleData[1] !== undefined ? scaleData[1] : (scaleData.y || 1), z: scaleData[2] !== undefined ? scaleData[2] : (scaleData.z || 1) };
const prefabName = prefab.Name || 'Unknown';
if (isBBMarkerType(prefabName)) { createBBMarkerFromPrefab(prefab); }
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
addPrefabLabel(prefabName, model.position.x, model.position.y, model.position.z);
}, function(error) {
log('Failed to load model: ' + prefabName, 'error');
createCubeMarker(prefabName, unityX, unityY, -unityZ);
});
}
function drawPrefabs(prefabsData) {
const maxPrefabs = Math.min(prefabsData.length, 500);
let bbCount = 0;
for (let i = 0; i < maxPrefabs; i++) {
const prefab = prefabsData[i];
const prefabName = prefab.Name || 'Unknown';
if (isBBMarkerType(prefabName)) { bbCount++; }
processPrefab(prefabsData[i], i);
}
if (bbCount > 0) { log('Created ' + bbCount + ' PreventBuilding markers', 'success'); }
log('Started processing ' + maxPrefabs + ' prefabs', 'success');
}
function addPrefabLabel(name, x, y, z) {
const canvas = document.createElement('canvas');
const ctx = canvas.getContext('2d');
canvas.width = 512; canvas.height = 128;
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
sprite.scale.set(80, 20, 1);
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
function setupNoclipButton() {
document.getElementById('noclip-btn').addEventListener('click', function() {
noClipMode = !noClipMode;
const btn = document.getElementById('noclip-btn');
btn.classList.toggle('active', noClipMode);
if (noClipMode) {
renderer.domElement.requestPointerLock();
playerVelocity.set(0, 0, 0);
isGrounded = true;
log('Noclip ON - Fly mode active', 'info');
} else {
const groundHeight = getTerrainHeightAt(camera.position.x, camera.position.z);
camera.position.y = groundHeight + baseCameraY;
playerVelocity.set(0, 0, 0);
isDucking = false;
log('Noclip OFF - Physics mode active', 'info');
}
});
}
function updateMovement(delta) {
if (!isPointerLocked) return;
let moveX = 0;
let moveZ = 0;
if (noClipMode) {
const isRunning = keys.shift;
moveZ = Number(keys.w) - Number(keys.s);
moveX = Number(keys.d) - Number(keys.a);
const currentSpeed = isRunning ? noClipSettings.runSpeed : noClipSettings.walkSpeed;
baseCameraY = isDucking ? noClipSettings.duckHeight : noClipSettings.eyeHeight;
const targetGroundHeight = getTerrainHeightAt(camera.position.x, camera.position.z) + baseCameraY;
if (!isGrounded) {
playerVelocity.y -= noClipSettings.gravity * delta;
}
camera.position.y += playerVelocity.y * delta;
if (camera.position.y - baseCameraY <= targetGroundHeight) {
camera.position.y = targetGroundHeight + baseCameraY;
playerVelocity.y = 0;
isGrounded = true;
} else if (camera.position.y - baseCameraY > targetGroundHeight) {
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
moveZ = Number(keys.w) - Number(keys.s);
moveX = Number(keys.d) - Number(keys.a);
const currentSpeed = keys.shift ? moveSpeed * 6 : moveSpeed;
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
const terrainHeight = getTerrainHeightAt(camera.position.x, camera.position.z);
camera.position.y = Math.max(terrainHeight + baseCameraY, camera.position.y);
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
updateMovement(delta);
updateRotationDisplay();
renderer.render(scene, camera);
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
    const mapInfo = data.mapInfo || {};
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
    } catch (err) {
        content.innerHTML = '<p class=""error-text"">Error: ' + err.message + '</p>';
    }
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
    } catch (err) {
        content.innerHTML = '<p class=""error-text"">Error: ' + err.message + '</p>';
    }
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

function downloadFile(filename, fileType) {
    window.location.href = '/api/download/' + WIPE_ID + '/' + fileType + '/' + encodeURIComponent(filename);
}

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
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Rust Relay Admin</title>
    <style>
        " + HtmlStyles.GetBaseStyles() + @"
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""card login-form"">
            <h1 class=""title"">Rust Relay Admin</h1>
            <form id=""loginForm"">
                <input type=""password"" id=""password"" placeholder=""Enter Password"" required>
                <button type=""submit"">Login</button>
                <p class=""error"" id=""errorMsg"">Invalid password</p>
            </form>
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
    <title>Rust Relay Admin</title>
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
            <table class=""data-table"" id=""serversTable"">
              <thead><tr><th class=""wipe-id-col"">Wipe ID</th><th>Connected</th><th>Last Activity</th><th>Packets</th><th>Bytes</th><th>Status</th></tr></thead>
                <tbody id=""serversBody""></tbody>
            </table>
            <p class=""last-update"" id=""lastUpdate""></p>
        </div>
        <button class=""logout-btn"" onclick=""logout()"">Logout</button>
        <script>
            async function loadServers() {
                try {
                    const res = await fetch('/api/servers');
                    if (res.status === 401) { window.location.reload(); return; }
                    const data = await res.json();
                    const servers = data.servers || [];

                    const totalPackets = servers.reduce(function(a, s) { return a + (s.packetsReceived || 0); }, 0);
                    const totalBytes = servers.reduce(function(a, s) { return a + (s.bytesReceived || 0); }, 0);
                    document.getElementById('stats').innerHTML = '<div class=""stat-box""><div class=""stat-value"">' + servers.length + '</div><div class=""stat-label"">Servers</div></div><div class=""stat-box""><div class=""stat-value"">' + totalPackets.toLocaleString() + '</div><div class=""stat-label"">Total Packets</div></div><div class=""stat-box""><div class=""stat-value"">' + formatBytes(totalBytes) + '</div><div class=""stat-label"">Total Data</div></div><div class=""stat-box""><div class=""stat-value"">' + servers.filter(function(s) { return s.hasMapData; }).length + '</div><div class=""stat-label"">With Map Data</div></div>';
                    const tbody = document.getElementById('serversBody');
                    if (servers.length === 0) {
                        tbody.innerHTML = '<tr><td colspan=""6"" class=""centered"">No servers connected</td></tr>';
                    } else {
                        tbody.innerHTML = servers.map(function(s) {
                            const hasActivity = s.lastActivity && Date.parse(s.lastActivity) > (Date.now() - 5000);
                            const statusBadges = [];
                            if (s.hasMapData) statusBadges.push('<span class=""badge badge-success"">Map</span>');
                            if (s.hasSnapshot) statusBadges.push('<span class=""badge badge-success"">Snapshot</span>');
                            if (!hasActivity && s.packetsReceived > 0) statusBadges.push('<span class=""badge badge-warning"">offline</span>');
                            return '<tr class=""clickable"" onclick=""window.location=\'/server/' + s.wipeId + '\'""><td class=""wipe-id-col""><code>' + s.wipeId + '</code></td><td>' + formatDate(s.connectedAt) + '</td><td>' + formatDate(s.lastActivity) + '</td><td>' + (s.packetsReceived || 0).toLocaleString() + '</td><td>' + formatBytes(s.bytesReceived || 0) + '</td><td>' + statusBadges.join(' ') + '</td></tr>';
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
    </div>
</body>
</html>";
            }
            return html;
        }
    }
    #endregion
}