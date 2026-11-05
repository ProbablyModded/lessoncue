using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LessonCue.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LessonCue.Server.Tests;

public sealed class GoogleDriveBackupTests
{
    [Fact]
    public async Task OAuthScheduledBackupAndMediaSyncUseManagedResumableDriveFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"lessoncue-google-drive-{Guid.NewGuid():N}");
        var mediaRoot = Path.Combine(root, "media", "originals");
        Directory.CreateDirectory(Path.Combine(root, "database"));
        Directory.CreateDirectory(Path.Combine(root, "config", "keys"));
        Directory.CreateDirectory(mediaRoot);
        var mediaPath = Path.Combine(mediaRoot, "lesson.jpg");
        await File.WriteAllTextAsync(mediaPath, "photo-v1", ct);

        try
        {
            var fakeDrive = new FakeGoogleDrive();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "config", "keys")))
                .SetApplicationName("LessonCue.GoogleDrive.Tests");
            services.AddDbContext<LessonCueDb>(options =>
                options.UseSqlite($"Data Source={Path.Combine(root, "database", "lessoncue.db")}"));
            services.AddHttpClient("backup-offsite")
                .ConfigurePrimaryHttpMessageHandler(() => new FakeGoogleDriveHandler(fakeDrive));
            services.AddHttpClient("backup-google-oauth")
                .ConfigurePrimaryHttpMessageHandler(() => new FakeGoogleDriveHandler(fakeDrive));
            await using var provider = services.BuildServiceProvider();

            await using (var setup = provider.CreateAsyncScope())
            {
                var db = setup.ServiceProvider.GetRequiredService<LessonCueDb>();
                await db.Database.EnsureCreatedAsync(ct);
                db.Organizations.Add(new Organization { Name = "Drive Test", TimeZone = "UTC" });
                await db.SaveChangesAsync(ct);
            }

            var backupService = new BackupService(root);
            var policy = new BackupPolicyService(
                root,
                provider.GetRequiredService<IServiceScopeFactory>(),
                backupService,
                provider.GetRequiredService<IDataProtectionProvider>(),
                provider.GetRequiredService<IHttpClientFactory>(),
                provider.GetRequiredService<ILogger<BackupPolicyService>>());
            const string clientSecret = "test-oauth-client-secret";
            const string backupPassword = "google drive test backup password";
            var saved = await policy.UpdateAsync(
                new BackupPolicyInput(
                    true,
                    "daily",
                    2,
                    null,
                    false,
                    10,
                    3650,
                    "exclude",
                    backupPassword,
                    null,
                    "none",
                    null,
                    null,
                    [
                        new BackupDestinationInput(
                            "googledrive",
                            null,
                            "oauth",
                            null,
                            null,
                            10,
                            3650,
                            "LessonCue-Test",
                            "drive-client.apps.googleusercontent.com",
                            clientSecret)
                    ],
                    "sync"),
                "UTC",
                ct);
            Assert.False(Assert.Single(saved.Destinations!).GoogleDriveConnected);

            const string callback = "https://lessoncue.test/api/v1/backups/google-drive/callback";
            var authorizationUri = new Uri(
                await policy.BeginGoogleDriveAuthorizationAsync(callback, ct));
            Assert.Equal("accounts.google.com", authorizationUri.Host);
            Assert.Contains("drive.file", authorizationUri.Query, StringComparison.Ordinal);
            Assert.Contains("drive-client.apps.googleusercontent.com", authorizationUri.Query, StringComparison.Ordinal);
            Assert.Contains("prompt=consent", authorizationUri.Query, StringComparison.Ordinal);
            var state = QueryValue(authorizationUri, "state");
            await policy.CompleteGoogleDriveAuthorizationAsync(state, "one-time-code", ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                policy.CompleteGoogleDriveAuthorizationAsync(state, "replayed-code", ct));

            var connected = Assert.Single(policy.GetStatus("UTC").Destinations!);
            Assert.True(connected.GoogleDriveConnected);
            Assert.True(connected.GoogleDriveClientSecretConfigured);
            var reconnectUri = new Uri(await policy.BeginGoogleDriveAuthorizationAsync(callback, ct));
            Assert.DoesNotContain("prompt=consent", reconnectUri.Query, StringComparison.Ordinal);
            var policyJson = await File.ReadAllTextAsync(
                Path.Combine(root, "config", "backup-policy.json"), ct);
            Assert.DoesNotContain(clientSecret, policyJson, StringComparison.Ordinal);
            Assert.DoesNotContain("fake-refresh-token", policyJson, StringComparison.Ordinal);

            var first = await policy.RunNowAsync("UTC", ct);
            var destination = Assert.Single(first.Destinations!);
            Assert.Null(destination.LastError);
            Assert.Equal(1, destination.LastMediaSyncAdded);
            Assert.Equal(0, destination.LastMediaSyncUpdated);
            Assert.Equal(0, destination.LastMediaSyncDeleted);
            Assert.Equal(1, fakeDrive.Files.Count(file => file.ManagedType == "media-file"));
            var mediaFile = Assert.Single(fakeDrive.Files, file =>
                file.ManagedType == "media-file" && file.Name == "lesson.jpg");
            Assert.Equal("photo-v1", Encoding.UTF8.GetString(mediaFile.Content!));
            Assert.Single(fakeDrive.Files, file => file.ManagedType == "backup");
            var mediaManifest = Assert.Single(fakeDrive.Files, file => file.ManagedType == "media-manifest");
            Assert.Equal(".lessoncue-media-sync.json", mediaManifest.Name);
            Assert.Contains("originals/lesson.jpg", Encoding.UTF8.GetString(mediaManifest.Content!));
            var second = await policy.RunNowAsync("UTC", ct);
            Assert.Equal(0, Assert.Single(second.Destinations!).LastMediaSyncAdded);
            Assert.True(fakeDrive.DownloadCount > 0);
            Assert.Equal(0, Assert.Single(second.Destinations!).LastMediaSyncUpdated);
            Assert.Equal(1, fakeDrive.Files.Count(file => file.ManagedType == "media-file"));

            var largeMediaPath = Path.Combine(mediaRoot, "large.bin");
            var largeMediaBytes = RandomNumberGenerator.GetBytes(8 * 1024 * 1024 + 31);
            fakeDrive.DropFirstChunkResponseAfterAccepting = true;
            await File.WriteAllBytesAsync(largeMediaPath, largeMediaBytes, ct);
            var largeMediaSync = await policy.RunNowAsync("UTC", ct);
            Assert.Equal(1, Assert.Single(largeMediaSync.Destinations!).LastMediaSyncAdded);
            Assert.Equal(largeMediaBytes, Assert.Single(
                fakeDrive.Files, file => file.Name == "large.bin" && file.ManagedType == "media-file").Content);
            Assert.True(fakeDrive.UploadChunkRequests >= 2);
            Assert.False(fakeDrive.DropFirstChunkResponseAfterAccepting);

            var longFolder = new string('d', 70);
            var deeperFolder = new string('n', 70);
            var longFileName = new string('f', 80) + ".jpg";
            var nestedMediaPath = Path.Combine(mediaRoot, longFolder, deeperFolder, longFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(nestedMediaPath)!);
            await File.WriteAllTextAsync(nestedMediaPath, "deeply nested", ct);
            var nestedSync = await policy.RunNowAsync("UTC", ct);
            Assert.Equal(1, Assert.Single(nestedSync.Destinations!).LastMediaSyncAdded);
            Assert.Equal("deeply nested", Encoding.UTF8.GetString(Assert.Single(
                fakeDrive.Files, file => file.Name == longFileName && file.ManagedType == "media-file").Content!));
            var verifyNested = await policy.RunNowAsync("UTC", ct);
            Assert.Equal(0, Assert.Single(verifyNested.Destinations!).LastMediaSyncAdded);
            Assert.Equal(0, Assert.Single(verifyNested.Destinations!).LastMediaSyncUpdated);

            await File.WriteAllTextAsync(mediaPath, "photo-v2-changed", ct);
            var third = await policy.RunNowAsync("UTC", ct);
            Assert.Equal(1, Assert.Single(third.Destinations!).LastMediaSyncUpdated);
            Assert.Equal("photo-v2-changed", Encoding.UTF8.GetString(
                Assert.Single(fakeDrive.Files, file => file.Name == "lesson.jpg" && file.ManagedType == "media-file").Content!));

            File.Delete(mediaPath);
            var fourth = await policy.RunNowAsync("UTC", ct);
            Assert.Equal(1, Assert.Single(fourth.Destinations!).LastMediaSyncDeleted);
            Assert.DoesNotContain(fakeDrive.Files, file => file.Name == "lesson.jpg" && file.ManagedType == "media-file");
            Assert.Contains(fakeDrive.Files, file => file.Name == "large.bin" && file.ManagedType == "media-file");
            Assert.True(fakeDrive.ResumableUploads >= 7);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string QueryValue(Uri uri, string key)
    {
        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .First(parts => Uri.UnescapeDataString(parts[0]) == key);
        return Uri.UnescapeDataString(query[1].Replace('+', ' '));
    }

    private sealed class FakeGoogleDrive
    {
        private readonly Dictionary<string, DriveFile> files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingUpload> uploads = new(StringComparer.Ordinal);
        private int nextId;

        public int ResumableUploads { get; private set; }
        public int DownloadCount { get; private set; }
        public int UploadChunkRequests { get; private set; }
        public bool DropFirstChunkResponseAfterAccepting { get; set; }
        public IReadOnlyCollection<DriveFile> Files => files.Values.ToArray();

        public HttpResponseMessage Handle(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "oauth2.googleapis.com")
                return OAuth(request);
            if (uri.Host != "www.googleapis.com")
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent($"Unexpected host: {uri.Host}")
                };

            if (uri.AbsolutePath == "/drive/v3/files" && request.Method == HttpMethod.Get)
                return ListFiles(request);
            if (uri.AbsolutePath == "/drive/v3/files" && request.Method == HttpMethod.Post)
                return CreateFolder(request, ct);
            if (uri.AbsolutePath.StartsWith("/drive/v3/files/", StringComparison.Ordinal) &&
                request.Method == HttpMethod.Get && uri.Query.Contains("alt=media", StringComparison.Ordinal))
                return Download(uri);
            if (uri.AbsolutePath.StartsWith("/drive/v3/files/", StringComparison.Ordinal) &&
                request.Method == HttpMethod.Delete)
                return Delete(uri);
            if (uri.AbsolutePath.StartsWith("/upload/drive/v3/files", StringComparison.Ordinal) &&
                request.Method is var method && (method == HttpMethod.Post || method == HttpMethod.Patch))
                return BeginUpload(request, ct);
            if (uri.AbsolutePath.StartsWith("/upload/sessions/", StringComparison.Ordinal) &&
                request.Method == HttpMethod.Put)
                return FinishUpload(request, ct);
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
                Content = new StringContent($"Unhandled fake Drive request: {request.Method} {uri.AbsolutePath}")
            };
        }

        private static HttpResponseMessage OAuth(HttpRequestMessage request)
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
            var response = body.Contains("grant_type=authorization_code", StringComparison.Ordinal)
                ? "{\"access_token\":\"test-access-token\",\"expires_in\":3600,\"refresh_token\":\"fake-refresh-token\"}"
                : "{\"access_token\":\"test-access-token\",\"expires_in\":3600}";
            return Json(request, HttpStatusCode.OK, response);
        }

        private HttpResponseMessage ListFiles(HttpRequestMessage request)
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query)
                .Replace('+', ' ');
            var firstQuote = query.IndexOf('\'');
            var secondQuote = query.IndexOf('\'', firstQuote + 1);
            var parentId = firstQuote >= 0 && secondQuote > firstQuote
                ? query[(firstQuote + 1)..secondQuote]
                : "root";
            var children = files.Values
                .Where(file => file.ParentId == parentId)
                .Select(ToJson)
                .ToArray();
            return Json(request, HttpStatusCode.OK, JsonSerializer.Serialize(new { files = children }));
        }

        private HttpResponseMessage CreateFolder(HttpRequestMessage request, CancellationToken ct)
        {
            var metadata = ReadJson(request, ct);
            if (!AppPropertiesFit(metadata))
                return Json(request, HttpStatusCode.BadRequest, "Drive custom property exceeds 124 bytes.");
            var file = NewFile(metadata);
            files[file.Id] = file;
            return Json(request, HttpStatusCode.OK, JsonSerializer.Serialize(ToJson(file)));
        }

        private HttpResponseMessage BeginUpload(HttpRequestMessage request, CancellationToken ct)
        {
            var metadata = ReadJson(request, ct);
            if (!AppPropertiesFit(metadata))
                return Json(request, HttpStatusCode.BadRequest, "Drive custom property exceeds 124 bytes.");
            var existingId = request.Method == HttpMethod.Patch
                ? Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.Split('/').Last())
                : null;
            var sessionId = (++nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            uploads[sessionId] = new PendingUpload(metadata, existingId);
            ResumableUploads++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            response.Headers.Location = new Uri($"https://www.googleapis.com/upload/sessions/{sessionId}");
            return response;
        }

        private HttpResponseMessage FinishUpload(HttpRequestMessage request, CancellationToken ct)
        {
            var sessionId = request.RequestUri!.AbsolutePath.Split('/').Last();
            var pending = uploads[sessionId];
            var requestContent = request.Content ?? throw new InvalidOperationException("Upload request has no body.");
            var contentRange = requestContent.Headers.ContentRange;
            var totalBytes = contentRange?.Length ?? 0;
            if (contentRange?.From is null && contentRange?.To is null && contentRange?.Length is not null)
            {
                var status = new HttpResponseMessage((HttpStatusCode)308) { RequestMessage = request };
                if (pending.Content.Length > 0)
                    status.Headers.TryAddWithoutValidation("Range", $"bytes=0-{pending.Content.Length - 1}");
                return status;
            }
            var content = requestContent.ReadAsByteArrayAsync(ct).GetAwaiter().GetResult();
            UploadChunkRequests++;
            if (contentRange?.From is { } rangeStart && rangeStart != pending.Content.Length)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    RequestMessage = request,
                    Content = new StringContent("Upload chunks must be contiguous.")
                };
            pending.Content.Write(content);
            if (pending.Content.Length < totalBytes)
            {
                if (DropFirstChunkResponseAfterAccepting)
                {
                    DropFirstChunkResponseAfterAccepting = false;
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
                }
                var incomplete = new HttpResponseMessage((HttpStatusCode)308) { RequestMessage = request };
                incomplete.Headers.TryAddWithoutValidation("Range", $"bytes=0-{pending.Content.Length - 1}");
                return incomplete;
            }
            content = pending.Content.ToArray();
            var file = pending.ExistingId is null
                ? NewFile(pending.Metadata)
                : files[pending.ExistingId] with
                {
                    Name = ReadString(pending.Metadata, "name"),
                    MimeType = ReadString(pending.Metadata, "mimeType"),
                    ManagedType = ReadAppProperty(pending.Metadata, "lessonCueType")
                };
            file = file with { Content = content, ModifiedAt = DateTimeOffset.UtcNow };
            files[file.Id] = file;
            uploads.Remove(sessionId);
            return Json(request, HttpStatusCode.OK, JsonSerializer.Serialize(ToJson(file)));
        }

        private HttpResponseMessage Download(Uri uri)
        {
            DownloadCount++;
            var id = Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last());
            if (!files.TryGetValue(id, out var file) || file.Content is null)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(file.Content)
            };
        }

        private HttpResponseMessage Delete(Uri uri)
        {
            files.Remove(Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last()));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private DriveFile NewFile(JsonElement metadata)
        {
            var parents = metadata.TryGetProperty("parents", out var values) &&
                          values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Select(value => value.GetString()).FirstOrDefault()
                : null;
            return new DriveFile(
                $"drive-{++nextId}",
                ReadString(metadata, "name"),
                ReadString(metadata, "mimeType"),
                parents ?? "root",
                ReadAppProperty(metadata, "lessonCueType"),
                DateTimeOffset.UtcNow,
                null);
        }

        private static object ToJson(DriveFile file)
        {
            var json = new Dictionary<string, object?>
            {
                ["id"] = file.Id,
                ["name"] = file.Name,
                ["mimeType"] = file.MimeType,
                ["modifiedTime"] = file.ModifiedAt,
                ["appProperties"] = new Dictionary<string, string>
                {
                    ["lessonCueManaged"] = "true",
                    ["lessonCueType"] = file.ManagedType ?? ""
                }
            };
            if (file.Content is not null)
            {
                json["size"] = file.Content.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
                json["md5Checksum"] = Convert.ToHexString(MD5.HashData(file.Content)).ToLowerInvariant();
            }
            return json;
        }

        private static JsonElement ReadJson(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return JsonDocument.Parse(body).RootElement.Clone();
        }

        private static string ReadString(JsonElement value, string property) =>
            value.TryGetProperty(property, out var result) ? result.GetString() ?? "" : "";

        private static string? ReadAppProperty(JsonElement value, string key) =>
            value.TryGetProperty("appProperties", out var properties) &&
            properties.TryGetProperty(key, out var result)
                ? result.GetString()
                : null;

        private static bool AppPropertiesFit(JsonElement metadata)
        {
            if (!metadata.TryGetProperty("appProperties", out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
                return true;
            return properties.EnumerateObject().All(property =>
                Encoding.UTF8.GetByteCount(property.Name) +
                Encoding.UTF8.GetByteCount(property.Value.GetString() ?? "") <= 124);
        }

        private static HttpResponseMessage Json(
            HttpRequestMessage request,
            HttpStatusCode status,
            string body) => new(status)
        {
            RequestMessage = request,
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        private sealed class PendingUpload(JsonElement metadata, string? existingId)
        {
            public JsonElement Metadata { get; } = metadata;
            public string? ExistingId { get; } = existingId;
            public MemoryStream Content { get; } = new();
        }
    }

    private sealed record DriveFile(
        string Id,
        string Name,
        string MimeType,
        string ParentId,
        string? ManagedType,
        DateTimeOffset ModifiedAt,
        byte[]? Content);

    private sealed class FakeGoogleDriveHandler(FakeGoogleDrive fakeDrive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(fakeDrive.Handle(request, cancellationToken));
    }
}
