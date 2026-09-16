using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoulScreen.App.Logic;
using SoulScreen.Core.Logging;

namespace SoulScreen.App;

/// <summary>One release, as reported by GitHub.</summary>
public sealed record UpdateRelease(string TagName, string Name, string Body, string HtmlUrl, IReadOnlyList<UpdateAsset> Assets)
{
    public UpdateVersion Version => UpdateVersion.TryParse(TagName, out var version) ? version : default;
}

/// <summary>One downloadable file attached to a release.</summary>
public sealed record UpdateAsset(string Name, string DownloadUrl, long Size, string? Digest);

/// <summary>Why an update could not be installed automatically.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Reaches GitHub for the latest SoulScreen release, and - if the user asks for it - downloads,
/// verifies and stages it, then hands off to a small PowerShell script that waits for this
/// process to exit, mirrors the staged build over the install directory with the same
/// <c>robocopy /MIR</c> RUN.bat already uses for its own build-and-run cycle, and starts the
/// new copy. Nothing here runs without "Install and restart" in Settings; the background check
/// only ever reads GitHub's public release feed.
/// </summary>
public sealed class UpdateService
{
    private static readonly ILogger Log_ = Log.For("update");
    private const string ReleasesUrl = "https://api.github.com/repos/mrsoulcommunity/SoulScreen/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // The GitHub API refuses requests with no User-Agent at all.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SoulScreen", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    // ---------------------------------------------------------------------- checking

    public async Task<UpdateRelease?> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(ReleasesUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log_.Warn($"the release check got {(int)response.StatusCode} from GitHub");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var dto = await JsonSerializer.DeserializeAsync<ReleaseDto>(stream, JsonOptions, ct);
            if (dto?.TagName is null) return null;

            var assets = (dto.Assets ?? [])
                .Where(a => a.Name is not null && a.BrowserDownloadUrl is not null)
                .Select(a => new UpdateAsset(a.Name!, a.BrowserDownloadUrl!, a.Size, a.Digest))
                .ToList();

            return new UpdateRelease(dto.TagName, dto.Name ?? dto.TagName, dto.Body ?? "", dto.HtmlUrl ?? "", assets);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log_.Warn("could not check for an update", ex);
            return null;
        }
    }

    // ------------------------------------------------------------------ installing

    /// <summary>
    /// True when this running copy is one <c>LaunchInstallerAndExit</c> can actually replace:
    /// a published exe under its own folder, not <c>dotnet.exe</c> hosting the app from a
    /// build tree (running under <c>dotnet run</c>, or a debugger's own host process), and a
    /// folder this process can prove it can write to.
    /// </summary>
    public static bool CanSelfUpdate(out string reason)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath) || !File.Exists(processPath))
        {
            reason = "SoulScreen could not find its own executable.";
            return false;
        }

        var exeName = Path.GetFileName(processPath);
        if (!exeName.Equals("SoulScreen.App.exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = "This looks like a build run from source (dotnet run), not a downloaded copy. " +
                     "Pull the latest changes and build again instead.";
            return false;
        }

        var installDir = Path.GetDirectoryName(processPath)!;
        try
        {
            var probe = Path.Combine(installDir, $".soulscreen-update-probe-{Environment.ProcessId}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            reason = $"SoulScreen cannot write to {installDir}. Move it out of a protected folder " +
                     "such as Program Files, or run the installer as an administrator.";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>Downloads <paramref name="asset"/>, checks its digest when GitHub supplied
    /// one, and unpacks it into a fresh staging folder. Throws <see cref="UpdateException"/>
    /// with a message fit to show the user on any failure; nothing outside the staging and
    /// download temp folders is touched.</summary>
    public async Task<string> DownloadAndStageAsync(UpdateAsset asset, string version, IProgress<double>? progress, CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "SoulScreen-update");
        Directory.CreateDirectory(workDir);
        var zipPath = Path.Combine(workDir, asset.Name);
        var stagingDir = Path.Combine(workDir, $"staging-{version}");

        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);

        try
        {
            using (var response = await Http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? asset.Size;

                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var destination = File.Create(zipPath);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    if (total > 0) progress?.Report(Math.Clamp((double)received / total, 0, 1));
                }
            }

            VerifyDigest(zipPath, asset.Digest);

            Directory.CreateDirectory(stagingDir);
            ZipFile.ExtractToDirectory(zipPath, stagingDir);

            var exeName = Path.GetFileName(Environment.ProcessPath) ?? "SoulScreen.App.exe";
            if (FindStagedExecutable(stagingDir, exeName) is null)
                throw new UpdateException($"The downloaded build does not contain {exeName}.");

            return stagingDir;
        }
        catch (Exception ex) when (ex is not UpdateException)
        {
            throw new UpdateException($"Could not prepare the update: {ex.Message}");
        }
    }

    /// <summary>Some releases zip the app straight into the archive root, others wrap it in
    /// one top-level folder (the shape a GitHub "Source code" habit, or an unzip-and-rename,
    /// tends to leave behind); either is found by searching one level down.</summary>
    private static string? FindStagedExecutable(string stagingDir, string exeName)
    {
        var direct = Path.Combine(stagingDir, exeName);
        if (File.Exists(direct)) return Path.GetDirectoryName(direct);

        return Directory.GetDirectories(stagingDir)
            .Select(dir => Path.Combine(dir, exeName))
            .Where(File.Exists)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault();
    }

    private static void VerifyDigest(string zipPath, string? digest)
    {
        var parsed = UpdatePolicy.ParseDigest(digest);
        if (parsed is not { Algorithm: "sha256" } sha) return; // nothing to check against

        using var stream = File.OpenRead(zipPath);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(hash, sha.Hex, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("The downloaded file does not match GitHub's checksum; nothing was installed.");
    }

    /// <summary>
    /// Hands off to a detached PowerShell script that waits for this process to exit, mirrors
    /// the staged build over the install directory, cleans up, and starts the new copy - then
    /// returns immediately so the caller can shut the app down. The script runs unelevated:
    /// <see cref="CanSelfUpdate"/> already proved this process can write to its own folder.
    /// </summary>
    public static void LaunchInstallerAndExit(string stagingDir, string zipPath)
    {
        var installDir = Path.GetDirectoryName(Environment.ProcessPath)!;
        var exePath = Environment.ProcessPath!;
        var pid = Environment.ProcessId;

        string Escape(string value) => value.Replace("'", "''");

        var script = new StringBuilder()
            .AppendLine("$ErrorActionPreference = 'SilentlyContinue'")
            .AppendLine($"$targetPid = {pid}")
            .AppendLine($"$staging = '{Escape(stagingDir)}'")
            .AppendLine($"$install = '{Escape(installDir)}'")
            .AppendLine($"$exe = '{Escape(exePath)}'")
            .AppendLine($"$zip = '{Escape(zipPath)}'")
            .AppendLine("$deadline = (Get-Date).AddSeconds(30)")
            .AppendLine("while ((Get-Date) -lt $deadline) {")
            .AppendLine("    if (-not (Get-Process -Id $targetPid -ErrorAction SilentlyContinue)) { break }")
            .AppendLine("    Start-Sleep -Milliseconds 300")
            .AppendLine("}")
            // Same tool and the same flags RUN.bat uses to move a build into place: /MIR also
            // removes files the new release no longer ships, so an update can never leave a
            // stray DLL from three versions ago behind.
            .AppendLine("robocopy $staging $install /MIR /R:5 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null")
            .AppendLine("Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue")
            .AppendLine("Remove-Item -Force $zip -ErrorAction SilentlyContinue")
            .AppendLine("Start-Process -FilePath $exe -WorkingDirectory $install")
            .ToString();

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            WorkingDirectory = installDir,
        };
        System.Diagnostics.Process.Start(start);
        Log_.Info($"handed off to the update script; {installDir} will be replaced from {stagingDir} once this process exits");
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<AssetDto>? Assets { get; set; }
    }

    private sealed class AssetDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}
