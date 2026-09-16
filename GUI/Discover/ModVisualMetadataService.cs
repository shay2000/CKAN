using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

using log4net;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CKAN.GUI
{
    /// <summary>
    /// Artwork and short description for a mod, gathered from a public metadata source.
    /// Any of the values may be null if the source did not provide it.
    /// </summary>
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    public sealed class ModVisualInfo
    {
        public string? BannerUrl        { get; }
        public string? ShortDescription { get; }
        public string? Author           { get; }
        public string? SourceLabel      { get; }

        public ModVisualInfo(string? bannerUrl, string? shortDescription,
                             string? author, string? sourceLabel)
        {
            BannerUrl        = bannerUrl;
            ShortDescription = shortDescription;
            Author           = author;
            SourceLabel      = sourceLabel;
        }
    }

    /// <summary>
    /// Fetches mod artwork and short descriptions on demand from public metadata
    /// sources (SpaceDock, then GitHub), caching the results on disk so a tile is
    /// only ever fetched once. Every failure degrades to generated fallback art.
    /// </summary>
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    public sealed class ModVisualMetadataService
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(ModVisualMetadataService));

        private static readonly string CacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CKAN", "artcache");

        // No BOM, so cached JSON stays trivially parseable.
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private const int    TimeoutMilliseconds = 10000;
        private const int    MaxDownloadBytes    = 16 * 1024 * 1024;
        private const int    MaxIdentifierLength = 96;
        private const string SourceSpaceDock     = "SpaceDock";
        private const string SourceGitHub        = "GitHub";
        private const string SourceGitHubReadme  = "GitHub README";

        /// <summary>
        /// Reading READMEs is cheap and hits a CDN, but there is no point in
        /// hammering GitHub for a whole session, so stop after this many misses.
        /// </summary>
        private const int    MaxReadmeLookups    = 120;

        private int readmeLookups;

        // ![alt](url) and <img src="url">
        private static readonly Regex MarkdownImagePattern = new Regex(
            @"!\[[^\]]*\]\(\s*(?<url>[^)\s]+)",
            RegexOptions.Compiled);

        private static readonly Regex HtmlImagePattern = new Regex(
            "<img[^>]*\\bsrc\\s*=\\s*[\"'](?<url>[^\"']+)[\"']",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Hosts that serve real screenshots, sometimes without a file extension
        private static readonly string[] ImageHosts =
        {
            "user-images.githubusercontent.com",
            "private-user-images.githubusercontent.com",
            "github.com/user-attachments",
            "i.imgur.com",
            "imgur.com",
            "media.forgecdn.net",
            "spacedock.info",
            "ksp.sarbian.com",
            "i.postimg.cc",
        };

        /// <summary>
        /// Optional diagnostics for the caller; never throws when nobody is listening.
        /// </summary>
        public event Action<string>? LogMessage;

        public ModVisualMetadataService()
        {
        }

        /// <summary>
        /// Fetch (or read from cache) the visual metadata for a mod.
        /// Returns null when no source applies or every source failed.
        /// </summary>
        public async Task<ModVisualInfo?> GetInfoAsync(CkanModule mod, CancellationToken ct)
        {
            if (mod == null)
            {
                return null;
            }

            string safeKey  = SanitizeIdentifier(mod.identifier);
            string infoPath = CachePath(safeKey + ".info.json");
            string nonePath = CachePath(safeKey + ".info.none");

            ModVisualInfo? cachedInfo = ParseInfoJson(ReadTextOrNull(infoPath));
            if (cachedInfo != null)
            {
                return cachedInfo;
            }
            if (FileExistsSafe(nonePath))
            {
                // We already looked and found nothing; don't hit the network again.
                return null;
            }

            ModVisualInfo? info;
            try
            {
                // WebRequest is synchronous, so keep it off the UI thread.
                info = await Task.Run(() => FetchInfo(mod), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exc)
            {
                Warn($"Failed to fetch visual metadata for {mod.identifier}: {exc.Message}");
                info = null;
            }

            if (info != null)
            {
                WriteTextAtomic(infoPath, InfoToJson(info).ToString(Formatting.None));
                TryDelete(nonePath);
            }
            else
            {
                // Negative cache marker, so a mod without metadata stays cheap.
                WriteTextAtomic(nonePath, "none");
            }

            return info;
        }

        /// <summary>
        /// The cover image for a mod: the cached/downloaded banner scaled and center
        /// cropped to exactly <paramref name="width"/> x <paramref name="height"/>,
        /// or generated fallback art. Never returns null and never throws, except
        /// when the caller's token was cancelled.
        /// </summary>
        public async Task<Image> GetCoverAsync(CkanModule mod, int width, int height, CancellationToken ct)
        {
            int w = width  < 1 ? 1 : width;
            int h = height < 1 ? 1 : height;

            if (mod == null)
            {
                return ModArtGenerator.CreateFallbackCover("", "", w, h);
            }

            try
            {
                string safeKey   = SanitizeIdentifier(mod.identifier);
                string coverPath = CachePath(safeKey + ".cover.bin");

                byte[]? bytes = ReadBytesOrNull(coverPath);
                if (bytes == null)
                {
                    ModVisualInfo? info = await GetInfoAsync(mod, ct).ConfigureAwait(false);
                    string? banner = info?.BannerUrl;
                    if (banner != null
                        && Uri.TryCreate(banner, UriKind.Absolute, out Uri? bannerUri)
                        && bannerUri != null)
                    {
                        // Copy to a non-nullable local so the off-thread lambda is unambiguous.
                        Uri downloadUrl = bannerUri;
                        bytes = await Task.Run(() => HttpGetBytes(downloadUrl), ct).ConfigureAwait(false);
                        if (bytes != null && bytes.Length > 0)
                        {
                            WriteBytesAtomic(coverPath, bytes);
                        }
                    }
                }

                if (bytes != null && bytes.Length > 0)
                {
                    Image? cover = DecodeAndFill(bytes, w, h);
                    if (cover != null)
                    {
                        return cover;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exc)
            {
                Warn($"Failed to load cover for {(mod == null ? "unknown" : mod.identifier)}: {exc.Message}");
            }

            return ModArtGenerator.CreateFallbackCover(mod?.identifier ?? "", mod?.name ?? "", w, h);
        }

        /// <summary>
        /// Forget everything cached on disk. Failures are logged and ignored.
        /// </summary>
        public void ClearCache()
        {
            try
            {
                if (Directory.Exists(CacheRoot))
                {
                    Directory.Delete(CacheRoot, true);
                }
                Notify("Artwork cache cleared");
            }
            catch (Exception exc)
            {
                Warn($"Failed to clear artwork cache: {exc.Message}");
            }
        }

        /// <summary>
        /// SpaceDock first (it usually has real banner art), then GitHub's OpenGraph
        /// image. Null means "nothing usable", which the caller negative caches.
        /// </summary>
        private ModVisualInfo? FetchInfo(CkanModule mod)
        {
            ResourcesDescriptor? resources = mod.resources;

            ModVisualInfo? info = TryFetchSpaceDock(resources?.spacedock, mod);
            if (info != null)
            {
                return info;
            }
            return TryFetchGitHub(resources?.repository, mod);
        }

        private ModVisualInfo? TryFetchSpaceDock(Uri? url, CkanModule mod)
        {
            if (url == null || !IsHttp(url) || !HostContains(url, "spacedock.info"))
            {
                return null;
            }

            try
            {
                string? rawId = SpaceDockId(url);
                if (rawId == null
                    || !int.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                    || id <= 0)
                {
                    return null;
                }

                string apiUrl = "https://spacedock.info/api/mod/"
                                + id.ToString(CultureInfo.InvariantCulture);
                string? json = HttpGetString(new Uri(apiUrl));
                if (json == null)
                {
                    return null;
                }

                JObject? payload = TryParseObject(json);
                if (payload == null)
                {
                    return null;
                }

                // SpaceDock's "background" is a site relative path.
                string? banner = SpaceDockBannerUrl(JsonString(payload, "background"));
                if (banner == null)
                {
                    // No artwork here; let the GitHub fallback have a chance.
                    return null;
                }

                // Fall back to the mod's display name when there's no short description.
                string? summary = Trimmed(JsonString(payload, "short_description"))
                                  ?? Trimmed(JsonString(payload, "name"));
                string? author  = Trimmed(JsonString(payload, "author"));

                return new ModVisualInfo(banner, summary, author, SourceSpaceDock);
            }
            catch (Exception exc)
            {
                Warn($"SpaceDock lookup failed for {mod.identifier}: {exc.Message}");
                return null;
            }
        }

        private ModVisualInfo? TryFetchGitHub(Uri? url, CkanModule mod)
        {
            if (url == null || !IsHttp(url) || !HostContains(url, "github.com"))
            {
                return null;
            }

            try
            {
                string[] segments = url.AbsolutePath.Split(new[] { '/' },
                                                           StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length < 2)
                {
                    return null;
                }

                string owner = segments[0];
                string repo  = segments[1];
                if (repo.Length > 4 && repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                {
                    repo = repo.Substring(0, repo.Length - 4);
                }
                if (owner.Length == 0 || repo.Length == 0)
                {
                    return null;
                }

                // Prefer a real screenshot from the repository's README, since a
                // rendered repository card is only a fallback.
                string? readmeImage = TryFetchGithubReadmeImage(owner, repo, mod);

                // GitHub renders a repository card we can use as cover art for free.
                string banner = readmeImage ?? $"https://opengraph.githubassets.com/1/{owner}/{repo}";
                return new ModVisualInfo(banner, null, null,
                                         readmeImage != null ? SourceGitHubReadme : SourceGitHub);
            }
            catch (Exception exc)
            {
                Warn($"GitHub lookup failed for {mod.identifier}: {exc.Message}");
                return null;
            }
        }

        /// <summary>
        /// Many mod repos put screenshots in their README. Pull the first
        /// non-badge image out of it and use that as the tile artwork.
        /// Returns null when the README is missing or has no usable image.
        /// </summary>
        private string? TryFetchGithubReadmeImage(string owner, string repo, CkanModule mod)
        {
            if (Interlocked.Increment(ref readmeLookups) > MaxReadmeLookups)
            {
                return null;
            }

            try
            {
                // Raw content is CDN backed, so this does not burn the API rate limit.
                foreach (string branch in new[] { "master", "main" })
                {
                    string baseUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/";
                    foreach (string name in new[] { "README.md", "readme.md" })
                    {
                        string? markdown = TryHttpGetString(new Uri(baseUrl + name));
                        if (markdown == null)
                        {
                            continue;
                        }
                        string? image = FirstImageUrl(markdown, baseUrl);
                        if (image != null)
                        {
                            log.DebugFormat("Using README image for {0}: {1}", mod.identifier, image);
                        }
                        return image;
                    }
                }
            }
            catch (Exception exc)
            {
                Warn($"README lookup failed for {mod.identifier}: {exc.Message}");
            }
            return null;
        }

        private static string? FirstImageUrl(string markdown, string baseUrl)
        {
            foreach (Match match in MarkdownImagePattern.Matches(markdown))
            {
                string? url = NormalizeImageUrl(match.Groups["url"].Value, baseUrl);
                if (url != null)
                {
                    return url;
                }
            }
            foreach (Match match in HtmlImagePattern.Matches(markdown))
            {
                string? url = NormalizeImageUrl(match.Groups["url"].Value, baseUrl);
                if (url != null)
                {
                    return url;
                }
            }
            return null;
        }

        private static string? NormalizeImageUrl(string raw, string baseUrl)
        {
            string candidate = raw.Trim().Trim('<', '>').Trim();
            if (candidate.Length == 0)
            {
                return null;
            }

            string lower = candidate.ToLowerInvariant();
            // Badges, CI status and donation buttons are never screenshots
            if (lower.Contains("shields.io")
                || lower.Contains("badge")
                || lower.Contains("travis-ci")
                || lower.Contains("codecov")
                || lower.Contains("coveralls")
                || lower.Contains("appveyor")
                || lower.Contains("circleci")
                || lower.Contains("github.com/sponsors")
                || lower.Contains("paypal")
                || lower.Contains("patreon")
                || lower.Contains("ko-fi")
                || lower.Contains("discord")
                || lower.Contains("buildstatus")
                || lower.Contains("license"))
            {
                return null;
            }

            string pathPart = candidate.Split('?')[0].Split('#')[0].ToLowerInvariant();
            bool rasterExtension = pathPart.EndsWith(".png")
                                   || pathPart.EndsWith(".jpg")
                                   || pathPart.EndsWith(".jpeg")
                                   || pathPart.EndsWith(".gif")
                                   || pathPart.EndsWith(".webp")
                                   || pathPart.EndsWith(".bmp");
            if (!rasterExtension && !ImageHosts.Any(h => lower.Contains(h)))
            {
                // SVG icons, wiki links and arbitrary links are not artwork
                return null;
            }
            if (pathPart.EndsWith(".svg"))
            {
                return null;
            }

            if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
            if (candidate.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + candidate;
            }
            if (baseUrl.Length > 0
                && Uri.TryCreate(new Uri(baseUrl), candidate, out Uri? resolved)
                && resolved != null)
            {
                return resolved.AbsoluteUri;
            }
            return null;
        }

        private static bool IsHttp(Uri? url)
            => url != null && url.IsAbsoluteUri
               && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);

        private static bool HostContains(Uri? url, string fragment)
            => url != null && url.IsAbsoluteUri
               && url.Host.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// The numeric id from a SpaceDock mod URL, e.g.
        /// https://spacedock.info/mod/1234/Some-Name -> "1234".
        /// </summary>
        private static string? SpaceDockId(Uri? url)
        {
            if (url == null || !url.IsAbsoluteUri)
            {
                return null;
            }

            string[] segments = url.AbsolutePath.Split(new[] { '/' },
                                                       StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < segments.Length; ++i)
            {
                if (string.Equals(segments[i], "mod", StringComparison.OrdinalIgnoreCase))
                {
                    return segments[i + 1];
                }
            }
            return null;
        }

        private static string? SpaceDockBannerUrl(string? background)
        {
            if (background == null)
            {
                return null;
            }

            // The API returns site relative paths, sometimes with a leading slash.
            string path = background.Trim().TrimStart('/');
            return path.Length == 0 ? null : "https://spacedock.info/" + path;
        }

        /// <summary>
        /// Build an HTTP request with our user agent and a sane timeout.
        /// Returns null if the URL isn't something a WebRequest can handle.
        /// </summary>
        private static HttpWebRequest? CreateRequest(Uri url)
        {
            #pragma warning disable SYSLIB0014
            WebRequest created = WebRequest.Create(url);
            #pragma warning restore SYSLIB0014
            HttpWebRequest? request = created as HttpWebRequest;
            if (request != null)
            {
                request.UserAgent = CKAN.Net.UserAgentString;
                request.Timeout   = TimeoutMilliseconds;
            }
            return request;
        }

        private static string? HttpGetString(Uri url)
        {
            HttpWebRequest? request = CreateRequest(url);
            if (request == null)
            {
                return null;
            }

            using (WebResponse response = request.GetResponse())
            {
                Stream? stream = response.GetResponseStream();
                if (stream == null)
                {
                    return null;
                }
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        /// <summary>
        /// HTTP GET that treats errors, including the 404 we expect for a missing
        /// README, as "no content" instead of throwing.
        /// </summary>
        private static string? TryHttpGetString(Uri url)
        {
            try
            {
                return HttpGetString(url);
            }
            catch (WebException)
            {
                return null;
            }
            catch (Exception exc)
            {
                log.Debug($"GET {url} failed", exc);
                return null;
            }
        }

        private static byte[]? HttpGetBytes(Uri url)
        {
            HttpWebRequest? request = CreateRequest(url);
            if (request == null)
            {
                return null;
            }

            using (WebResponse response = request.GetResponse())
            {
                Stream? stream = response.GetResponseStream();
                if (stream == null)
                {
                    return null;
                }
                using (MemoryStream buffer = new MemoryStream())
                {
                    byte[] chunk = new byte[81920];
                    int read;
                    while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (buffer.Length + read > MaxDownloadBytes)
                        {
                            // Too big to be cover art; treat as a failure.
                            return null;
                        }
                        buffer.Write(chunk, 0, read);
                    }
                    return buffer.ToArray();
                }
            }
        }

        /// <summary>
        /// Decode image bytes and aspect-fill center crop them to exactly width x height.
        /// Returns null when the bytes aren't a usable image.
        /// </summary>
        private static Image? DecodeAndFill(byte[] bytes, int width, int height)
        {
            try
            {
                // Copy semantics: the stream can go away once we're done drawing.
                using (MemoryStream stream = new MemoryStream(bytes, false))
                using (Image source = Image.FromStream(stream))
                {
                    if (source.Width < 1 || source.Height < 1)
                    {
                        return null;
                    }

                    Bitmap target = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    try
                    {
                        using (Graphics g = Graphics.FromImage(target))
                        {
                            g.SmoothingMode     = SmoothingMode.HighQuality;
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode   = PixelOffsetMode.HighQuality;

                            float scale = Math.Max((float)width / source.Width,
                                                   (float)height / source.Height);
                            int drawWidth  = Math.Max(1, (int)Math.Ceiling(source.Width * scale));
                            int drawHeight = Math.Max(1, (int)Math.Ceiling(source.Height * scale));

                            // Overflow is clipped by the bitmap bounds, giving a center crop.
                            g.DrawImage(source, new Rectangle((width - drawWidth) / 2,
                                                              (height - drawHeight) / 2,
                                                              drawWidth, drawHeight));
                        }
                    }
                    catch (Exception)
                    {
                        target.Dispose();
                        return null;
                    }
                    return target;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string CachePath(string fileName)
            => Path.Combine(CacheRoot, fileName);

        private static void EnsureCacheRoot()
        {
            if (!Directory.Exists(CacheRoot))
            {
                Directory.CreateDirectory(CacheRoot);
            }
        }

        private static string SanitizeIdentifier(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                return "unknown";
            }

            StringBuilder builder = new StringBuilder(identifier.Length);
            foreach (char c in identifier)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
                {
                    builder.Append(c);
                }
                if (builder.Length >= MaxIdentifierLength)
                {
                    break;
                }
            }
            return builder.Length == 0 ? "unknown" : builder.ToString();
        }

        private static string? ReadTextOrNull(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path, Utf8NoBom) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static byte[]? ReadBytesOrNull(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool FileExistsSafe(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // A stale marker file is harmless.
            }
        }

        private static void WriteTextAtomic(string path, string content)
        {
            try
            {
                WriteBytesAtomicCore(path, Utf8NoBom.GetBytes(content));
            }
            catch (Exception exc)
            {
                log.Debug($"Failed to write cache file {path}", exc);
            }
        }

        private static void WriteBytesAtomic(string path, byte[] data)
        {
            try
            {
                WriteBytesAtomicCore(path, data);
            }
            catch (Exception exc)
            {
                log.Debug($"Failed to write cache file {path}", exc);
            }
        }

        private static void WriteBytesAtomicCore(string path, byte[] data)
        {
            EnsureCacheRoot();
            // Write to a temp file and rename, so readers never see a partial file.
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, data);
            ReplaceFile(temp, path);
        }

        private static void ReplaceFile(string temp, string destination)
        {
            #if NET5_0_OR_GREATER
            File.Move(temp, destination, true);
            #else
            if (File.Exists(destination))
            {
                try
                {
                    File.Replace(temp, destination, null);
                    return;
                }
                catch (Exception)
                {
                    // Some filesystems don't support Replace; fall back to a copy.
                }
            }
            File.Copy(temp, destination, true);
            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
                // Leaving a stray temp file behind is harmless.
            }
            #endif
        }

        private static JObject? TryParseObject(string json)
        {
            try
            {
                return JObject.Parse(json);
            }
            catch (Exception)
            {
                // Malformed or unexpected payloads are just a cache miss.
                return null;
            }
        }

        private static string? JsonString(JObject payload, string key)
        {
            JToken? token = payload[key];
            return token != null && token.Type == JTokenType.String ? token.ToString() : null;
        }

        private static string? Trimmed(string? value)
        {
            if (value == null)
            {
                return null;
            }

            string trimmed = value.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static JObject InfoToJson(ModVisualInfo info)
        {
            JObject payload = new JObject();
            if (info.BannerUrl != null)
            {
                payload["bannerUrl"] = info.BannerUrl;
            }
            if (info.ShortDescription != null)
            {
                payload["shortDescription"] = info.ShortDescription;
            }
            if (info.Author != null)
            {
                payload["author"] = info.Author;
            }
            if (info.SourceLabel != null)
            {
                payload["sourceLabel"] = info.SourceLabel;
            }
            return payload;
        }

        private static ModVisualInfo? ParseInfoJson(string? json)
        {
            if (json == null || string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            JObject? payload = TryParseObject(json);
            if (payload == null)
            {
                return null;
            }

            return new ModVisualInfo(Trimmed(JsonString(payload, "bannerUrl")),
                                     Trimmed(JsonString(payload, "shortDescription")),
                                     Trimmed(JsonString(payload, "author")),
                                     Trimmed(JsonString(payload, "sourceLabel")));
        }

        private void Notify(string message)
        {
            LogMessage?.Invoke(message);
        }

        private void Warn(string message)
        {
            log.Warn(message);
            Notify(message);
        }
    }
}
