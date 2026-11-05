using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LessonCue.Server;

public sealed class GoogleDriveAuthorizationException(string message) : IOException(message);

/// <summary>
/// Small Drive v3 client used only by scheduled backup destinations. OAuth
/// credentials stay server-side; uploaded files use Drive's resumable protocol.
/// </summary>
public sealed class GoogleDriveBackupClient(IHttpClientFactory clients)
{
    private static readonly Uri TokenEndpoint = new("https://oauth2.googleapis.com/token");
    private static readonly Uri AuthorizationEndpoint = new("https://accounts.google.com/o/oauth2/v2/auth");
    private static readonly Uri DriveFilesEndpoint = new("https://www.googleapis.com/drive/v3/files");
    private static readonly Uri DriveUploadEndpoint = new("https://www.googleapis.com/upload/drive/v3/files");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string CreateAuthorizationUrl(string clientId, string callbackUri, string state, bool requestConsent)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = callbackUri,
            ["response_type"] = "code",
            ["scope"] = "https://www.googleapis.com/auth/drive.file",
            ["access_type"] = "offline",
            ["include_granted_scopes"] = "true",
            ["state"] = state
        };
        // Reuse an existing durable refresh grant when reconnecting. Repeated
        // consent grants can consume Google's per-client refresh-token quota
        // and invalidate the oldest token. Force consent only for the initial
        // connection or an explicitly expired/revoked connection.
        if (requestConsent) parameters["prompt"] = "consent";
        var query = string.Join("&", parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new UriBuilder(AuthorizationEndpoint) { Query = query }.Uri.AbsoluteUri;
    }

    public async Task<string?> ExchangeAuthorizationCodeAsync(
        string clientId,
        string clientSecret,
        string code,
        string callbackUri,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["redirect_uri"] = callbackUri,
                ["grant_type"] = "authorization_code"
            })
        };
        using var response = await clients.CreateClient("backup-google-oauth").SendAsync(request, ct);
        using var document = await ReadJsonResponseAsync(response, "Google OAuth code exchange", ct);
        return document.RootElement.TryGetProperty("refresh_token", out var refreshToken) &&
               refreshToken.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(refreshToken.GetString())
            ? refreshToken.GetString()!
            : null;
    }

    public async Task<GoogleDriveSession> CreateSessionAsync(
        string clientId,
        string clientSecret,
        string refreshToken,
        CancellationToken ct)
    {
        var session = new GoogleDriveSession(
            clients.CreateClient("backup-offsite"),
            clients.CreateClient("backup-google-oauth"),
            clientId,
            clientSecret,
            refreshToken);
        await session.RefreshAccessTokenAsync(ct);
        return session;
    }

    private static async Task<JsonDocument> ReadJsonResponseAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadErrorDetailAsync(response.Content, ct);
            if (operation.Equals("refresh Google Drive authorization", StringComparison.Ordinal) &&
                detail.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
            {
                throw new GoogleDriveAuthorizationException(
                    "Google Drive authorization expired or was revoked. Reconnect Google Drive in Backup & Recovery.");
            }
            throw new IOException(
                $"{operation} failed with HTTP {(int)response.StatusCode}" +
                (detail.Length == 0 ? "." : $": {detail}"));
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static async Task<string> ReadErrorDetailAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[1024];
        var length = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        return new string(buffer, 0, length).Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    public sealed class GoogleDriveSession
    {
        private const string FolderMimeType = "application/vnd.google-apps.folder";
        private const int UploadChunkBytes = 8 * 1024 * 1024;
        private const int MaxUploadRetries = 4;
        private readonly HttpClient http;
        private readonly HttpClient oauthHttp;
        private readonly string clientId;
        private readonly string clientSecret;
        private readonly string refreshToken;
        private readonly SemaphoreSlim tokenGate = new(1, 1);
        private string? accessToken;
        private DateTimeOffset tokenExpiresAt;

        internal GoogleDriveSession(
            HttpClient http,
            HttpClient oauthHttp,
            string clientId,
            string clientSecret,
            string refreshToken)
        {
            this.http = http;
            this.oauthHttp = oauthHttp;
            this.clientId = clientId;
            this.clientSecret = clientSecret;
            this.refreshToken = refreshToken;
        }

        public async Task<string> GetOrCreateFolderAsync(
            string parentId,
            string name,
            string managedType,
            CancellationToken ct)
        {
            var children = await ListChildrenAsync(parentId, ct);
            var existing = children.FirstOrDefault(item =>
                item.MimeType == FolderMimeType && item.Name == name &&
                item.ManagedType == managedType);
            if (existing is not null) return existing.Id;

            using var document = await SendJsonAsync(
                HttpMethod.Post,
                DriveFilesEndpoint,
                new
                {
                    name,
                    mimeType = FolderMimeType,
                    parents = new[] { parentId },
                    appProperties = ManagedProperties(managedType)
                },
                "create Google Drive folder",
                ct);
            return GetRequiredString(document.RootElement, "id", "Google Drive did not return a folder ID.");
        }

        public async Task<IReadOnlyList<GoogleDriveItem>> ListChildrenAsync(
            string parentId,
            CancellationToken ct)
        {
            var result = new List<GoogleDriveItem>();
            string? pageToken = null;
            do
            {
                var query = $"'{parentId.Replace("'", "\\'", StringComparison.Ordinal)}' in parents and trashed = false";
                var parameters = new Dictionary<string, string>
                {
                    ["q"] = query,
                    ["pageSize"] = "1000",
                    ["fields"] = "nextPageToken,files(id,name,mimeType,size,modifiedTime,md5Checksum,appProperties)"
                };
                if (!string.IsNullOrEmpty(pageToken)) parameters["pageToken"] = pageToken;
                var uri = AddQuery(DriveFilesEndpoint, parameters);
                using var document = await SendJsonAsync(
                    HttpMethod.Get, uri, null, "list Google Drive folder", ct);
                if (document.RootElement.TryGetProperty("files", out var files) &&
                    files.ValueKind == JsonValueKind.Array)
                {
                    result.AddRange(files.EnumerateArray().Select(ParseItem));
                }
                pageToken = document.RootElement.TryGetProperty("nextPageToken", out var next) &&
                            next.ValueKind == JsonValueKind.String
                    ? next.GetString()
                    : null;
            } while (!string.IsNullOrEmpty(pageToken));
            return result;
        }

        public async Task<GoogleDriveItem> UploadFileAsync(
            string parentId,
            string name,
            string mimeType,
            string managedType,
            Stream content,
            long contentLength,
            string? existingFileId,
            CancellationToken ct)
        {
            if (!content.CanSeek)
                throw new IOException("Google Drive resumable uploads require a seekable source stream.");
            var metadata = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["mimeType"] = mimeType,
                ["appProperties"] = ManagedProperties(managedType)
            };
            if (existingFileId is null) metadata["parents"] = new[] { parentId };

            using var initiate = await SendJsonHttpAsync(
                existingFileId is null ? HttpMethod.Post : HttpMethod.Patch,
                existingFileId is null
                    ? AddQuery(DriveUploadEndpoint, new Dictionary<string, string>
                    {
                        ["uploadType"] = "resumable",
                        ["fields"] = "id,name,mimeType,size,modifiedTime,md5Checksum,appProperties"
                    })
                    : AddQuery(FileUri(DriveUploadEndpoint, existingFileId),
                        new Dictionary<string, string>
                        {
                            ["uploadType"] = "resumable",
                            ["fields"] = "id,name,mimeType,size,modifiedTime,md5Checksum,appProperties"
                        }),
                metadata,
                request =>
                {
                    request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", mimeType);
                    request.Headers.TryAddWithoutValidation(
                        "X-Upload-Content-Length", contentLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
                },
                "start Google Drive resumable upload",
                ct);
            if (!initiate.IsSuccessStatusCode)
                throw await ApiErrorAsync(initiate, "start Google Drive resumable upload", ct);
            if (initiate.Headers.Location is not { IsAbsoluteUri: true } uploadUri ||
                uploadUri.Scheme != Uri.UriSchemeHttps ||
                (!uploadUri.Host.Equals("googleapis.com", StringComparison.OrdinalIgnoreCase) &&
                 !uploadUri.Host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Google Drive did not return a secure resumable-upload URL.");

            content.Position = 0;
            var offset = 0L;
            var retriesWithoutProgress = 0;
            while (offset < contentLength || (contentLength == 0 && offset == 0))
            {
                var chunkLength = (int)Math.Min(UploadChunkBytes, contentLength - offset);
                var chunk = new byte[chunkLength];
                if (chunkLength > 0)
                    await content.ReadExactlyAsync(chunk, ct);
                using var uploadRequest = new HttpRequestMessage(HttpMethod.Put, uploadUri)
                {
                    Content = new ByteArrayContent(chunk)
                };
                uploadRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
                uploadRequest.Content.Headers.ContentLength = chunkLength;
                if (contentLength > 0)
                    uploadRequest.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                        offset, offset + chunkLength - 1, contentLength);

                HttpResponseMessage uploadResponse;
                try
                {
                    uploadResponse = await http.SendAsync(
                        uploadRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                }
                catch (HttpRequestException) when (retriesWithoutProgress < MaxUploadRetries)
                {
                    retriesWithoutProgress++;
                    await DelayBeforeUploadRetryAsync(null, retriesWithoutProgress, ct);
                    var state = await QueryUploadStatusAsync(uploadUri, contentLength, ct);
                    if (state.Completed is not null) return state.Completed;
                    offset = state.Offset;
                    content.Position = offset;
                    continue;
                }
                catch (OperationCanceledException) when (
                    !ct.IsCancellationRequested && retriesWithoutProgress < MaxUploadRetries)
                {
                    retriesWithoutProgress++;
                    await DelayBeforeUploadRetryAsync(null, retriesWithoutProgress, ct);
                    var state = await QueryUploadStatusAsync(uploadUri, contentLength, ct);
                    if (state.Completed is not null) return state.Completed;
                    offset = state.Offset;
                    content.Position = offset;
                    continue;
                }
                using (uploadResponse)
                {
                    if (uploadResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                    {
                        using var result = await ReadJsonResponseAsync(
                            uploadResponse, "Google Drive file upload", ct);
                        return ParseItem(result.RootElement);
                    }
                    if ((int)uploadResponse.StatusCode == 308)
                    {
                        var nextOffset = ReadAcknowledgedOffset(uploadResponse);
                        if (nextOffset > offset) retriesWithoutProgress = 0;
                        else if (++retriesWithoutProgress > MaxUploadRetries)
                            throw new IOException("Google Drive made no progress while receiving an upload chunk.");
                        offset = nextOffset;
                        content.Position = offset;
                        continue;
                    }
                    if (IsTransientUploadStatus(uploadResponse.StatusCode) &&
                        retriesWithoutProgress < MaxUploadRetries)
                    {
                        retriesWithoutProgress++;
                        await DelayBeforeUploadRetryAsync(uploadResponse, retriesWithoutProgress, ct);
                        var state = await QueryUploadStatusAsync(uploadUri, contentLength, ct);
                        if (state.Completed is not null) return state.Completed;
                        offset = state.Offset;
                        content.Position = offset;
                        continue;
                    }
                    throw await ApiErrorAsync(uploadResponse, "Google Drive file upload", ct);
                }
            }
            throw new IOException("Google Drive ended an upload session without confirming the uploaded file.");
        }

        private static bool IsTransientUploadStatus(HttpStatusCode statusCode) =>
            statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
                HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

        private static Task DelayBeforeUploadRetryAsync(
            HttpResponseMessage? response,
            int attempt,
            CancellationToken ct)
        {
            var retryAfter = response?.Headers.RetryAfter;
            var delay = retryAfter?.Delta ?? (retryAfter?.Date is { } date
                ? date - DateTimeOffset.UtcNow
                : TimeSpan.FromMilliseconds(Math.Min(8000, 250 * (1 << (attempt - 1)))));
            return Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 0, 30_000)), ct);
        }

        private async Task<(long Offset, GoogleDriveItem? Completed)> QueryUploadStatusAsync(
            Uri uploadUri,
            long contentLength,
            CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUri)
            {
                Content = new ByteArrayContent([])
            };
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(contentLength);
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode == 308)
                return (ReadAcknowledgedOffset(response), null);
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                using var result = await ReadJsonResponseAsync(
                    response, "check Google Drive upload progress", ct);
                return (contentLength, ParseItem(result.RootElement));
            }
            throw await ApiErrorAsync(response, "check Google Drive upload progress", ct);
        }

        private static long ReadAcknowledgedOffset(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Range", out var values)) return 0;
            var range = values.FirstOrDefault();
            if (range is null) return 0;
            var separator = range.LastIndexOf('-');
            return separator >= 0 && long.TryParse(
                    range.AsSpan(separator + 1),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var lastByte)
                ? lastByte + 1
                : throw new IOException("Google Drive returned an invalid resumable-upload range.");
        }

        public async Task<byte[]> DownloadFileAsync(string fileId, CancellationToken ct)
        {
            using var response = await SendAuthorizedAsync(
                () => new HttpRequestMessage(
                    HttpMethod.Get,
                    AddQuery(FileUri(DriveFilesEndpoint, fileId),
                        new Dictionary<string, string> { ["alt"] = "media" })),
                "download Google Drive file",
                ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return [];
            if (!response.IsSuccessStatusCode)
                throw await ApiErrorAsync(response, "download Google Drive file", ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, ct);
            return output.ToArray();
        }

        public async Task DeleteFileAsync(string fileId, CancellationToken ct)
        {
            using var response = await SendAuthorizedAsync(
                () => new HttpRequestMessage(
                    HttpMethod.Delete,
                    FileUri(DriveFilesEndpoint, fileId)),
                "delete Google Drive file",
                ct);
            if (response.StatusCode != HttpStatusCode.NotFound && !response.IsSuccessStatusCode)
                throw await ApiErrorAsync(response, "delete Google Drive file", ct);
        }

        private async Task<JsonDocument> SendJsonAsync(
            HttpMethod method,
            Uri uri,
            object? body,
            string operation,
            CancellationToken ct)
        {
            using var response = await SendAuthorizedAsync(
                () => CreateJsonRequest(method, uri, body), operation, ct);
            return await ReadJsonResponseAsync(response, operation, ct);
        }

        private async Task<HttpResponseMessage> SendJsonHttpAsync(
            HttpMethod method,
            Uri uri,
            object body,
            Action<HttpRequestMessage> configure,
            string operation,
            CancellationToken ct) =>
            await SendAuthorizedAsync(() =>
            {
                var request = CreateJsonRequest(method, uri, body);
                configure(request);
                return request;
            }, operation, ct);

        private static HttpRequestMessage CreateJsonRequest(HttpMethod method, Uri uri, object? body)
        {
            var request = new HttpRequestMessage(method, uri);
            if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
            return request;
        }

        private async Task<HttpResponseMessage> SendAuthorizedAsync(
            Func<HttpRequestMessage> createRequest,
            string operation,
            CancellationToken ct)
        {
            await EnsureAccessTokenAsync(ct);
            var response = await SendWithTokenAsync(createRequest, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
            response.Dispose();
            await RefreshAccessTokenAsync(ct);
            response = await SendWithTokenAsync(createRequest, ct);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            {
                var error = await ApiErrorAsync(response, operation, ct);
                response.Dispose();
                throw error;
            }
            return response;
        }

        private async Task<HttpResponseMessage> SendWithTokenAsync(
            Func<HttpRequestMessage> createRequest,
            CancellationToken ct)
        {
            using var request = createRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        private async Task EnsureAccessTokenAsync(CancellationToken ct)
        {
            if (accessToken is not null && tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return;
            await RefreshAccessTokenAsync(ct);
        }

        internal async Task RefreshAccessTokenAsync(CancellationToken ct)
        {
            await tokenGate.WaitAsync(ct);
            try
            {
                if (accessToken is not null && tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return;
                using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["client_id"] = clientId,
                        ["client_secret"] = clientSecret,
                        ["refresh_token"] = refreshToken,
                        ["grant_type"] = "refresh_token"
                    })
                };
                using var response = await oauthHttp.SendAsync(request, ct);
                using var document = await ReadJsonResponseAsync(response, "refresh Google Drive authorization", ct);
                accessToken = GetRequiredString(
                    document.RootElement, "access_token", "Google did not return an access token.");
                var expiresIn = document.RootElement.TryGetProperty("expires_in", out var expires) &&
                                expires.TryGetInt32(out var seconds)
                    ? Math.Clamp(seconds, 60, 3600)
                    : 3600;
                tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            }
            finally { tokenGate.Release(); }
        }

        private static async Task<IOException> ApiErrorAsync(
            HttpResponseMessage response,
            string operation,
            CancellationToken ct)
        {
            var detail = await ReadErrorDetailAsync(response.Content, ct);
            return new IOException(
                $"{operation} failed with HTTP {(int)response.StatusCode}" +
                (detail.Length == 0 ? "." : $": {detail}"));
        }

        private static GoogleDriveItem ParseItem(JsonElement item)
        {
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            if (item.TryGetProperty("appProperties", out var appProperties) &&
                appProperties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in appProperties.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String)
                        properties[property.Name] = property.Value.GetString() ?? "";
            }
            long? bytes = item.TryGetProperty("size", out var size) &&
                          long.TryParse(size.GetString(), out var parsedSize)
                ? parsedSize
                : null;
            DateTimeOffset? modifiedAt = item.TryGetProperty("modifiedTime", out var modified) &&
                                         DateTimeOffset.TryParse(modified.GetString(), out var parsedDate)
                ? parsedDate
                : null;
            return new GoogleDriveItem(
                GetRequiredString(item, "id", "Google Drive returned a file without an ID."),
                item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                item.TryGetProperty("mimeType", out var mimeType) ? mimeType.GetString() ?? "" : "",
                bytes,
                modifiedAt,
                item.TryGetProperty("md5Checksum", out var checksum) ? checksum.GetString() : null,
                properties.GetValueOrDefault("lessonCueType"),
                null);
        }

        private static Dictionary<string, string> ManagedProperties(string type) => new()
        {
            ["lessonCueManaged"] = "true",
            ["lessonCueType"] = type
        };

        private static string GetRequiredString(JsonElement value, string name, string error) =>
            value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString())
                ? property.GetString()!
                : throw new IOException(error);

        private static Uri AddQuery(Uri uri, IReadOnlyDictionary<string, string> values)
        {
            var query = string.Join("&", values.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
            var builder = new UriBuilder(uri) { Query = query };
            return builder.Uri;
        }

        private static Uri FileUri(Uri endpoint, string fileId) =>
            new(endpoint.AbsoluteUri.TrimEnd('/') + "/" + Uri.EscapeDataString(fileId));
    }
}

public sealed record GoogleDriveItem(
    string Id,
    string Name,
    string MimeType,
    long? Bytes,
    DateTimeOffset? ModifiedAt,
    string? Md5Checksum,
    string? ManagedType,
    string? ManagedPath);
