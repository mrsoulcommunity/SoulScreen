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
/// A downloaded and unpacked release, staged and ready to hand off to the installer script.
/// <see cref="ContentDir"/> is the folder that actually holds <c>SoulScreen.App.exe</c> - it
/// is the one to mirror over the install directory. It is not always <see cref="StagingRoot"/>
/// itself: some releases zip the app straight into the archive root, others wrap it in one
/// top-level folder, and mirroring the wrong one would leave the install directory holding a
/// single subfolder instead of the app.
/// </summary>
public sealed record StagedUpdate(string ContentDir, string StagingRoot, string ZipPath);

/// <summary>Why an update that was handed off to the installer script did not finish, read
/// back from the marker the script leaves behind when it could not complete.</summary>
public sealed record UpdateFailureInfo(string Version, string Reason, DateTime TimestampUtc);

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

            if (dto.Prerelease)
            {
                Log_.Info($"latest release {dto.TagName} is a prerelease; skipping");
                return null;
            }

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
    public async Task<StagedUpdate> DownloadAndStageAsync(UpdateAsset asset, string version, IProgress<double>? progress, CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "SoulScreen-update");
        Directory.CreateDirectory(workDir);
        var zipPath = Path.Combine(workDir, asset.Name);
        var stagingDir = Path.Combine(workDir, $"staging-{version}");

        // Leftovers from an earlier failed or abandoned attempt are cleared before this one
        // starts, so a temp folder that is retried a few times does not quietly grow without
        // bound.
        CleanStaleWork(workDir, keep: [zipPath, stagingDir]);
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
            var contentDir = FindStagedExecutable(stagingDir, exeName)
                ?? throw new UpdateException($"The downloaded build does not contain {exeName}.");

            return new StagedUpdate(contentDir, stagingDir, zipPath);
        }
        catch (Exception ex) when (ex is not UpdateException)
        {
            throw new UpdateException($"Could not prepare the update: {ex.Message}");
        }
    }

    /// <summary>Removes every <c>staging-*</c> folder and <c>.zip</c> under the update work
    /// folder except the ones this attempt is about to (re)use, so an update that is retried
    /// after a failed download or a cancelled install does not leave its predecessors behind
    /// forever.</summary>
    private static void CleanStaleWork(string workDir, IReadOnlyCollection<string> keep)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(workDir))
            {
                if (keep.Contains(entry, StringComparer.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileName(entry);
                if (!name.StartsWith("staging-", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                    else File.Delete(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A previous staging folder still open elsewhere is not worth failing
                    // this update over; it is tried again next time.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log_.Warn($"could not clean up {workDir}", ex);
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
    /// <para>
    /// The script is defensive about everything that is genuinely outside the app's control
    /// once it has exited: it gives up and records a diagnosable failure (read back by
    /// <see cref="ConsumeLastFailure"/> the next time SoulScreen starts) rather than silently
    /// leaving a half-installed copy, if this process does not exit in time, if robocopy
    /// cannot copy everything after a few retries (a file still locked by another handle), or
    /// if the executable is somehow missing once the mirror is done.
    /// </para>
    /// </summary>
    public static void LaunchInstallerAndExit(StagedUpdate staged, string version)
    {
        var installDir = Path.GetDirectoryName(Environment.ProcessPath)!;
        var exePath = Environment.ProcessPath!;
        var pid = Environment.ProcessId;
        var logPath = InstallLogPath;

        // Values land inside single-quoted PowerShell strings, where only the single quote
        // is special (escaped by doubling). Backticks and $ are literal in that context,
        // so pre-pending a backtick before a $ — or doubling a backtick — would corrupt
        // any path that legitimately contains those characters.
        string Escape(string value) => value.Replace("'", "''");

        var script = new StringBuilder()
            .AppendLine("$ErrorActionPreference = 'SilentlyContinue'")
            .AppendLine($"$targetPid = {pid}")
            .AppendLine($"$content = '{Escape(staged.ContentDir)}'")
            .AppendLine($"$stagingRoot = '{Escape(staged.StagingRoot)}'")
            .AppendLine($"$install = '{Escape(installDir)}'")
            .AppendLine($"$exe = '{Escape(exePath)}'")
            .AppendLine($"$zip = '{Escape(staged.ZipPath)}'")
            .AppendLine($"$version = '{Escape(version)}'")
            .AppendLine($"$marker = '{Escape(FailureMarkerPath)}'")
            .AppendLine($"$logFile = '{Escape(logPath)}'")
            .AppendLine()
            .AppendLine("function Write-InstallLog($msg) {")
            .AppendLine("    \"$((Get-Date).ToString('o'))  $msg\" | Out-File -FilePath $logFile -Append -Encoding utf8")
            .AppendLine("}")
            .AppendLine("function Write-InstallFailure($reason) {")
            .AppendLine("    Write-InstallLog \"FAILED: $reason\"")
            .AppendLine("    $obj = [ordered]@{ version = $version; reason = $reason; timestampUtc = (Get-Date).ToUniversalTime().ToString('o') }")
            .AppendLine("    $obj | ConvertTo-Json | Out-File -FilePath $marker -Encoding utf8")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("Write-InstallLog \"waiting for pid $targetPid to exit (installing $version)\"")
            .AppendLine("$deadline = (Get-Date).AddSeconds(30)")
            .AppendLine("while ((Get-Date) -lt $deadline) {")
            .AppendLine("    if (-not (Get-Process -Id $targetPid -ErrorAction SilentlyContinue)) { break }")
            .AppendLine("    Start-Sleep -Milliseconds 300")
            .AppendLine("}")
            .AppendLine("if (Get-Process -Id $targetPid -ErrorAction SilentlyContinue) {")
            .AppendLine("    Write-InstallFailure 'SoulScreen did not exit within 30 seconds; the update was not installed.'")
            .AppendLine("    exit 1")
            .AppendLine("}")
            // A file that closed a moment ago can still be briefly held by an antivirus
            // scanner or the shell's own indexer; a short grace period avoids a robocopy
            // retry (and its 1-second wait) for something that clears on its own.
            .AppendLine("Start-Sleep -Milliseconds 500")
            .AppendLine()
            // Same tool RUN.bat uses to move a build into place: /MIR also removes files
            // the new release no longer ships, so an update can never leave a stray DLL
            // from three versions ago behind. Retried a few times at the script level too,
            // since /R:5 /W:1 only covers one robocopy invocation's own per-file retries.
            .AppendLine("$mirrorSucceeded = $false")
            .AppendLine("$lastCode = -1")
            .AppendLine("for ($attempt = 1; $attempt -le 3 -and -not $mirrorSucceeded; $attempt++) {")
            .AppendLine("    robocopy $content $install /MIR /R:5 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null")
            .AppendLine("    $lastCode = $LASTEXITCODE")
            .AppendLine("    Write-InstallLog \"robocopy attempt $attempt exit code $lastCode\"")
            .AppendLine($"    if ($lastCode -lt {UpdatePolicy.RobocopySuccessThreshold}) {{ $mirrorSucceeded = $true }} else {{ Start-Sleep -Seconds 2 }}")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("if (-not $mirrorSucceeded) {")
            .AppendLine("    Write-InstallFailure \"robocopy could not fully copy the update (exit code $lastCode) after 3 attempts; a file may still be in use. The staged copy was kept at $stagingRoot for a manual retry.\"")
            .AppendLine("    if (Test-Path $exe) { Start-Process -FilePath $exe -WorkingDirectory $install }")
            .AppendLine("    exit 1")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("if (-not (Test-Path $exe)) {")
            .AppendLine("    Write-InstallFailure \"the mirror reported success but $exe is missing afterwards. The staged copy was kept at $stagingRoot.\"")
            .AppendLine("    exit 1")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("Remove-Item -Recurse -Force $stagingRoot -ErrorAction SilentlyContinue")
            .AppendLine("Remove-Item -Force $zip -ErrorAction SilentlyContinue")
            .AppendLine("Remove-Item -Force $marker -ErrorAction SilentlyContinue")
            .AppendLine("Write-InstallLog \"update to $version installed; starting $exe\"")
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
        Log_.Info($"handed off to the update script; {installDir} will be replaced from {staged.ContentDir} once this process exits");
    }

    /// <summary>Where the update work folder's failure marker and install log live. Both are
    /// written only by the detached installer script, which by definition runs after this
    /// process has already exited - so nothing in this class writes them itself.</summary>
    private static string WorkDir => Path.Combine(Path.GetTempPath(), "SoulScreen-update");
    private static string FailureMarkerPath => Path.Combine(WorkDir, "last-update-failure.json");
    private static string InstallLogPath => Path.Combine(WorkDir, "install.log");

    /// <summary>
    /// Reads and deletes the marker the installer script leaves behind when a previous
    /// update could not be completed, so SoulScreen can tell the user about it once - on the
    /// very next startup - rather than either staying silent about a failed install or
    /// nagging about it forever.
    /// </summary>
    public static UpdateFailureInfo? ConsumeLastFailure()
    {
        var path = FailureMarkerPath;
        try
        {
            if (!File.Exists(path)) return null;
            var dto = JsonSerializer.Deserialize<FailureDto>(File.ReadAllText(path), JsonOptions);
            File.Delete(path);
            if (dto?.Reason is null) return null;
            return new UpdateFailureInfo(dto.Version ?? "?", dto.Reason, dto.TimestampUtc ?? DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log_.Warn($"could not read {path}", ex);
            try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
            return null;
        }
    }

    private sealed class FailureDto
    {
        public string? Version { get; set; }
        public string? Reason { get; set; }
        public DateTime? TimestampUtc { get; set; }
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
