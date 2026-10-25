// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace MpvLauncher.Gui.Services
{
    /// <summary>One file announced by the helper's metadata event.</summary>
    public sealed record TorrentFileEntry(int Index, string Name, string DisplayName, long Length)
    {
        public string DisplaySize => FormatSize(Length);

        private static string FormatSize(long bytes)
        {
            string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0
                ? string.Format(CultureInfo.InvariantCulture, "{0:0} {1}", value, units[unit])
                : string.Format(CultureInfo.InvariantCulture, "{0:0.0} {1}", value, units[unit]);
        }
    }

    /// <summary>
    /// One line of the helper's stdout protocol, parsed without trusting it.
    ///
    /// The helper is a separate executable that the user can replace, so every
    /// field is optional and every number is range-checked by the consumer.
    /// Unknown event types are surfaced as <see cref="Type"/> = "unknown" rather
    /// than throwing: a newer helper must not crash an older launcher.
    /// </summary>
    public sealed class TorrentHelperEvent
    {
        public string Type { get; init; } = "unknown";
        public int Contract { get; init; }
        public string Version { get; init; } = "";
        public string? InfoHash { get; init; }
        public string? Name { get; init; }
        public string? Phase { get; init; }
        public int Peers { get; init; }
        public long DownloadSpeed { get; init; }
        public long UploadSpeed { get; init; }
        public double Progress { get; init; }
        public long Received { get; init; }
        public long Length { get; init; }
        public int FileIndex { get; init; } = -1;
        public string? FileName { get; init; }
        public string? DisplayName { get; init; }
        public long Size { get; init; }
        public string? Url { get; init; }
        public string? Code { get; init; }
        public string? Message { get; init; }
        public bool Fatal { get; init; }
        public bool Ready { get; init; }
        public IReadOnlyList<TorrentFileEntry> Files { get; init; } = Array.Empty<TorrentFileEntry>();

        public static TorrentHelperEvent? Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                string type = Text(root, "type") ?? "unknown";
                var files = new List<TorrentFileEntry>();
                if (root.TryGetProperty("files", out var array) && array.ValueKind == JsonValueKind.Array)
                {
                    int index = 0;
                    foreach (var item in array.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        string name = Text(item, "name") ?? "";
                        files.Add(new TorrentFileEntry(
                            Integer(item, "index") ?? index,
                            name,
                            Text(item, "displayName") ?? name,
                            Long(item, "length") ?? 0));
                        index++;
                        if (files.Count >= 2000) break;
                    }
                }

                return new TorrentHelperEvent
                {
                    Type = type,
                    Contract = Integer(root, "contract") ?? 0,
                    Version = Text(root, "version") ?? "",
                    InfoHash = Text(root, "infoHash"),
                    Name = Text(root, "name"),
                    Phase = Text(root, "phase"),
                    Peers = Integer(root, "peers") ?? 0,
                    DownloadSpeed = Long(root, "downloadSpeed") ?? 0,
                    UploadSpeed = Long(root, "uploadSpeed") ?? 0,
                    Progress = Double(root, "progress") ?? 0,
                    Received = Long(root, "received") ?? 0,
                    Length = Long(root, "length") ?? 0,
                    FileIndex = Integer(root, "fileIndex") ?? -1,
                    FileName = Text(root, "fileName"),
                    DisplayName = Text(root, "displayName"),
                    Size = Long(root, "size") ?? 0,
                    Url = Text(root, "url"),
                    Code = Text(root, "code"),
                    Message = Text(root, "message"),
                    Fatal = Boolean(root, "fatal"),
                    Ready = Boolean(root, "ready"),
                    Files = files
                };
            }
            catch (JsonException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static int? Integer(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int number)
                ? number
                : null;

        private static long? Long(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out long number)
                ? number
                : null;

        private static double? Double(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out double number) &&
            double.IsFinite(number)
                ? number
                : null;

        private static bool Boolean(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }
}
