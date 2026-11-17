using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Backthread;

internal sealed record Update(Version Version, Uri Download, Uri Notes);

internal static class Updates
{
    internal const string Repository = "https://github.com/lsqkk/Backthread";
    internal static Version Current { get; } = Version.Parse(typeof(Updates).Assembly.GetName().Version!.ToString(3));
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(12) };

    internal static async Task<Update?> CheckAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/lsqkk/Backthread/releases/latest");
        request.Headers.UserAgent.ParseAdd($"Backthread/{Current}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var data = release.RootElement;
        var tag = data.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) ||
            version.Build < 0 || version.Revision >= 0 || version <= Current)
            return null;

        string installer = $"Backthread-{version}-win-x64-Setup.exe";
        string url = $"{Repository}/releases/download/{tag}/{installer}";
        foreach (var asset in data.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() == installer &&
                asset.GetProperty("browser_download_url").GetString() == url)
                return new(version, new Uri(url), new Uri($"{Repository}/releases/tag/{tag}"));
        }
        return null;
    }
}
