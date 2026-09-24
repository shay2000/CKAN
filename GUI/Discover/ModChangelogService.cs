using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

using log4net;
using Newtonsoft.Json.Linq;

namespace CKAN.GUI
{
    /// <summary>
    /// Builds a changelog / version history for a mod. The local CKAN registry
    /// provides the base history, optionally enriched on demand with the mod's
    /// public GitHub releases.
    /// </summary>
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    public sealed class ModChangelogService
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(ModChangelogService));

        public event Action<string>? LogMessage;

        public ModChangelogService()
        {
        }

        /// <summary>
        /// Returns the versions known to the local registry, newest first.
        /// Never returns null.
        /// </summary>
        public IReadOnlyList<ModChangelogEntry> GetLocalHistory(IRegistryQuerier registry, GUIMod mod)
        {
            var entries = new List<ModChangelogEntry>();

            List<CkanModule> modules;
            try
            {
                modules = registry.AvailableByIdentifier(mod.Identifier)
                                  .OrderByDescending(module => module.version)
                                  .Take(40)
                                  .ToList();
            }
            catch (Exception exc)
            {
                // An unknown identifier or a broken registry should just yield no history.
                log.DebugFormat("Unable to get local history for {0}: {1}",
                                mod.Identifier, exc.Message);
                return entries;
            }

            var installedVersion = mod.InstalledMod?.Module.version;
            var selectedVersion  = mod.SelectedMod?.version;

            for (var i = 0; i < modules.Count; i++)
            {
                var    module = modules[i];
                var    res    = module.resources;
                string? url   = res?.homepage?.ToString()
                                ?? res?.spacedock?.ToString()
                                ?? res?.repository?.ToString();

                entries.Add(new ModChangelogEntry(
                    // Strip the epoch and any leading "v" for display
                    module.version.ToString(true, true),
                    module.release_date,
                    installedVersion != null && installedVersion.Equals(module.version),
                    i == 0,
                    selectedVersion != null && selectedVersion.Equals(module.version),
                    module.name,
                    module.@abstract,
                    "CKAN",
                    url));
            }

            return entries;
        }

        /// <summary>
        /// Fetches the mod's public GitHub releases, newest first.
        /// Returns an empty list for non-GitHub mods or on any failure.
        /// </summary>
        public Task<IReadOnlyList<ModChangelogEntry>> GetGithubReleasesAsync(CkanModule mod,
                                                                             CancellationToken ct)
            // Keep the blocking network work off the UI thread
            => Task.Run(() => FetchGithubReleases(mod, ct), ct);

        /// <summary>
        /// Returns the local registry history first, followed by any additional
        /// GitHub-only releases. Never returns null.
        /// </summary>
        public async Task<IReadOnlyList<ModChangelogEntry>> GetFullChangelogAsync(IRegistryQuerier registry,
                                                                                   GUIMod mod,
                                                                                   CancellationToken ct)
        {
            var local  = GetLocalHistory(registry, mod);
            var github = await GetGithubReleasesAsync(mod.Module, ct).ConfigureAwait(false);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<ModChangelogEntry>(local);
            foreach (var entry in github)
            {
                string version = NormalizeVersion(entry.Version);
                if (!seen.Add(version))
                {
                    continue;
                }
                int index = result.FindIndex(existing => string.Equals(
                    NormalizeVersion(existing.Version), version, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    result.Add(entry);
                }
                else
                {
                    // A version already in CKAN still needs its release notes.
                    // Preserve registry-derived selection and installation state.
                    var existing = result[index];
                    result[index] = new ModChangelogEntry(
                        existing.Version,
                        existing.ReleaseDate ?? entry.ReleaseDate,
                        existing.IsInstalled,
                        existing.IsLatest,
                        existing.IsSelected,
                        entry.Title ?? existing.Title,
                        string.IsNullOrWhiteSpace(entry.Notes) ? existing.Notes : entry.Notes,
                        entry.Source,
                        entry.Url ?? existing.Url);
                }
            }
            return result;
        }

        private IReadOnlyList<ModChangelogEntry> FetchGithubReleases(CkanModule mod,
                                                                     CancellationToken ct)
        {
            if (!TryGetGithubRepo(mod, out string owner, out string repo))
            {
                return Array.Empty<ModChangelogEntry>();
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                var json = FetchReleasesJson(owner, repo);
                ct.ThrowIfCancellationRequested();
                return ParseReleases(json);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exc)
            {
                log.WarnFormat("Unable to fetch GitHub releases for {0}: {1}",
                               mod.identifier, exc.Message);
                LogMessage?.Invoke($"Unable to fetch GitHub releases for {mod.identifier}: {exc.Message}");
                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                return Array.Empty<ModChangelogEntry>();
            }
        }

        /// <summary>
        /// Extracts {owner} and {repo} from a mod's repository URL, but only for
        /// absolute GitHub URLs.
        /// </summary>
        private static bool TryGetGithubRepo(CkanModule mod, out string owner, out string repo)
        {
            owner = "";
            repo  = "";

            var uri = mod.resources?.repository;
            if (uri == null
                || !uri.IsAbsoluteUri
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                     || uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var segments = uri.AbsolutePath.Split(new[] { '/' },
                                                  StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                return false;
            }

            owner = segments[0];
            repo  = segments[1];
            if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                repo = repo.Substring(0, repo.Length - ".git".Length);
            }
            return repo.Length > 0;
        }

        private static string FetchReleasesJson(string owner, string repo)
        {
            var url = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=30";

            // WebRequest is obsolete but is still the simplest option for net481
            #pragma warning disable SYSLIB0014
            var request = WebRequest.Create(url);
            request.Timeout = 10000;
            if (request is HttpWebRequest httpRequest)
            {
                httpRequest.UserAgent = CKAN.Net.UserAgentString;
                httpRequest.Accept    = "application/vnd.github+json";
            }

            using (var response = request.GetResponse())
            using (var stream = response.GetResponseStream())
            {
                if (stream == null)
                {
                    return "";
                }
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            #pragma warning restore SYSLIB0014
        }

        private IReadOnlyList<ModChangelogEntry> ParseReleases(string json)
        {
            var    entries        = new List<ModChangelogEntry>();
            var    releases       = JArray.Parse(json);
            var    latestAssigned = false;

            foreach (var token in releases)
            {
                if (!(token is JObject release))
                {
                    continue;
                }
                // Skip drafts but keep prereleases
                if (release["draft"]?.Value<bool>() == true)
                {
                    continue;
                }

                var tagName = release["tag_name"]?.Value<string>();
                var name    = release["name"]?.Value<string>();
                var version = tagName ?? name ?? "";
                var body    = release["body"]?.Value<string>();
                if (body != null && body.Length > 4000)
                {
                    body = body.Substring(0, 4000);
                }

                DateTime? published = null;
                var publishedText = release["published_at"]?.Value<string>();
                if (publishedText != null
                    && DateTime.TryParse(publishedText,
                                         CultureInfo.InvariantCulture,
                                         DateTimeStyles.AdjustToUniversal
                                         | DateTimeStyles.AssumeUniversal,
                                         out var parsed))
                {
                    published = parsed;
                }

                entries.Add(new ModChangelogEntry(
                    version,
                    published,
                    false,
                    !latestAssigned,
                    false,
                    name,
                    body,
                    "GitHub",
                    release["html_url"]?.Value<string>()));
                latestAssigned = true;
            }

            return entries;
        }

        /// <summary>
        /// Trims surrounding whitespace and a single leading 'v'/'V' for
        /// comparing local and GitHub version strings.
        /// </summary>
        private static string NormalizeVersion(string version)
        {
            var trimmed = version.Trim();
            if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
            {
                trimmed = trimmed.Substring(1);
            }
            return trimmed;
        }
    }
}
