using Newtonsoft.Json;
using SiraUtil.Logging;
using System;
using System.Globalization;
using System.Threading.Tasks;
using Version = Hive.Versioning.Version;

namespace SiraUtil.Web.SiraSync.Implementations
{
    internal class GitHubSiraSyncService : ISiraSyncService
    {
        private Release? _cachedRelease;
        private string _githubURL = null!;
        private readonly SiraLog _siraLog;
        private readonly IHttpService _httpService;

        public GitHubSiraSyncService(SiraLog siraLog, IHttpService httpService)
        {
            _siraLog = siraLog;
            _httpService = httpService;
        }

        internal void Set(string repoOwner, string repoName)
            => _githubURL = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases";

        public async Task<string?> LatestChangelog()
        {
            Release? release = await GetRelease();
            return release?.Body;
        }

        public async Task<Version?> LatestVersion()
        {
            bool cached = _cachedRelease is not null;
            Release? release = await GetRelease();
            if (release is null)
            {
                return null;
            }

            try
            {
                string? tagName = release.TagName;
                CultureInfo culture = CultureInfo.CurrentCulture;
                if (!cached && culture.GetType() == typeof(CultureInfo))
                {
                    CultureInfo preparedCulture = CultureInfo.ReadOnly((CultureInfo)culture.Clone());
                    Version? prepared = await Task.Run(() => PrepareVersion(tagName, preparedCulture));
                    if (prepared is not null)
                    {
                        return prepared;
                    }
                }

                return Version.Parse(NormalizeTag(tagName));
            }
            catch (Exception e)
            {
                _siraLog.Error("Could not convert the tag name into a SemVer version. Make sure your tag name is SemVer!");
                _siraLog.Error(e);
                return null;
            }
        }

        private static Version? PrepareVersion(string? tagName, CultureInfo culture)
        {
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = culture;
                return Version.TryParse(NormalizeTag(tagName), out Version? version) ? version : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }
        }

        private static string NormalizeTag(string? tagName)
        {
            string value = tagName?.Trim() ?? string.Empty;
            return value.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? value[1..] : value;
        }

        private async Task<Release?> GetRelease()
        {
            if (_cachedRelease is not null)
            {
                return _cachedRelease;
            }

            _siraLog.Debug($"Starting changelog request at {_githubURL}");
            IHttpResponse response = await _httpService.GetAsync(_githubURL);
            if (!response.Successful)
            {
                _siraLog.Error($"({response.Code}) An error occurred while trying to get the latest release. {await response.Error()}");
                return null;
            }

            Release[] releases;
            try
            {
                releases = JsonConvert.DeserializeObject<Release[]>(await response.ReadAsStringAsync())!;
            }
            catch (Exception e)
            {
                _siraLog.Error("An error occured while trying to deserialize the release body.");
                _siraLog.Error(e);
                return null;
            }
            if (releases.Length == 0)
            {
                _siraLog.Debug($"There are no releases at {_githubURL}");
                return null;
            }
            return _cachedRelease = releases[0];
        }

        private class Release
        {
            [JsonProperty("tag_name")]
            public string TagName { get; set; } = null!;

            [JsonProperty("body")]
            public string Body { get; set; } = null!;
        }
    }
}
