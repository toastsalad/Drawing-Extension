using System.Net.Http.Headers;
using System.Text;
using PlotterHelper.Cli;

namespace PlotterHelper.Cli.Output;

/// <summary>
/// Uploads generated G-code to a Klipper printer via its Moonraker HTTP API,
/// optionally starting the print. Used when the Output Target is "Upload to
/// Klipper" / "Upload and start print" instead of saving to disk.
///
/// Moonraker endpoints:
///   POST {url}/server/files/upload   multipart: file=&lt;gcode&gt;, root="gcodes"
///   POST {url}/printer/print/start?filename=&lt;name&gt;
/// Auth: optional X-Api-Key header (only needed if the client isn't on Moonraker's
/// trusted network).
/// </summary>
internal static class Klipper
{
    internal static void Upload(string gcode, string filename, string url, string apiKey, bool start, Options opts)
    {
        url = url.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(url))
            throw new Exception("Moonraker URL is empty (set it on the Klipper tab).");
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            url = "http://" + url;

        // Klipper only accepts gcode files with a .gcode / .g extension.
        if (!filename.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase) &&
            !filename.EndsWith(".g", StringComparison.OrdinalIgnoreCase))
            filename += ".gcode";

        Progress.Report(opts, 92, $"Uploading {filename} to Klipper…");

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        if (!string.IsNullOrEmpty(apiKey))
            http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        using var form = new MultipartFormDataContent();
        var fileContent = new StringContent(gcode, Encoding.UTF8);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", filename);
        form.Add(new StringContent("gcodes"), "root");

        using var uploadResp = http.PostAsync($"{url}/server/files/upload", form).GetAwaiter().GetResult();
        if (!uploadResp.IsSuccessStatusCode)
        {
            var body = uploadResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new Exception($"Moonraker upload failed: HTTP {(int)uploadResp.StatusCode} {uploadResp.ReasonPhrase}. {body}");
        }

        if (start)
        {
            Progress.Report(opts, 96, "Starting print…");
            using var startResp = http.PostAsync(
                $"{url}/printer/print/start?filename={Uri.EscapeDataString(filename)}",
                new StringContent("")).GetAwaiter().GetResult();
            if (!startResp.IsSuccessStatusCode)
            {
                var body = startResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                throw new Exception($"Moonraker start-print failed: HTTP {(int)startResp.StatusCode} {startResp.ReasonPhrase}. {body}");
            }
        }

        Progress.Report(opts, 99, start ? $"Started: {filename}" : $"Uploaded: {filename}");
    }
}
