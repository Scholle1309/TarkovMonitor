using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace TarkovMonitor
{
    /// <summary>A marker read from a share code, with the category it had there.</summary>
    public sealed record SharedMarker(string Map, double Lat, double Lng, string Label, string Description, string Icon, string Modes, string CategoryName, string CategoryColor);

    /// <summary>
    /// Share codes for markers: plain text ("TMM1:" + compressed JSON in URL-safe base64), so
    /// they can be pasted anywhere without a server. A code carries the markers and the name
    /// and colour of their categories; on import, categories are matched by name.
    /// </summary>
    public static class MarkerShare
    {
        private const string Prefix = "TMM1:";

        public static string Encode(IReadOnlyList<MapMarker> markers, IReadOnlyList<MarkerCategory> categories)
        {
            var used = new List<MarkerCategory>();
            var index = new Dictionary<string, int>();
            foreach (var marker in markers)
            {
                if (index.ContainsKey(marker.Category))
                {
                    continue;
                }
                var category = categories.FirstOrDefault(entry => entry.Id == marker.Category)
                    ?? categories.FirstOrDefault(entry => entry.Id == MarkerCategory.OtherId)
                    ?? new MarkerCategory(MarkerCategory.OtherId, "Other", "#9e9e9e", 0);
                index[marker.Category] = used.Count;
                used.Add(category);
            }
            var payload = new
            {
                c = used.Select(category => new[] { category.Name, category.Color }).ToList(),
                m = markers.Select(marker => new object[]
                {
                    marker.Map, Math.Round(marker.Lat, 3), Math.Round(marker.Lng, 3), marker.Label, marker.Description, marker.Icon,
                    index[marker.Category], marker.Modes,
                }).ToList(),
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(payload);
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                deflate.Write(json);
            }
            return Prefix + Convert.ToBase64String(output.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>Reads a share code; null when the text is not a valid code.</summary>
        public static List<SharedMarker>? Decode(string? code)
        {
            var text = (code ?? "").Trim();
            var start = text.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return null;
            }
            var data = new string(text[(start + Prefix.Length)..].TakeWhile(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
            try
            {
                var base64 = data.Replace('-', '+').Replace('_', '/');
                base64 += new string('=', (4 - base64.Length % 4) % 4);
                using var input = new MemoryStream(Convert.FromBase64String(base64));
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                using var reader = new StreamReader(deflate, Encoding.UTF8);
                using var document = JsonDocument.Parse(reader.ReadToEnd());
                var root = document.RootElement;
                var categories = root.GetProperty("c").EnumerateArray()
                    .Select(entry => (Name: entry[0].GetString() ?? "", Color: entry[1].GetString() ?? "#9e9e9e"))
                    .ToList();
                var result = new List<SharedMarker>();
                foreach (var entry in root.GetProperty("m").EnumerateArray())
                {
                    var categoryIndex = entry[6].GetInt32();
                    var category = categoryIndex >= 0 && categoryIndex < categories.Count ? categories[categoryIndex] : ("", "#9e9e9e");
                    result.Add(new SharedMarker(
                        entry[0].GetString() ?? "",
                        entry[1].GetDouble(),
                        entry[2].GetDouble(),
                        entry[3].GetString() ?? "",
                        entry[4].GetString() ?? "",
                        entry[5].GetString() ?? "",
                        entry[7].GetString() ?? "",
                        category.Item1,
                        IsColor(category.Item2) ? category.Item2 : "#9e9e9e"));
                }
                return result.Where(marker => !string.IsNullOrEmpty(marker.Map)).ToList();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>A marker already exists when one on the same map sits at almost the same spot.</summary>
        public static bool Exists(SharedMarker shared, IEnumerable<MapMarker> existing) =>
            existing.Any(marker => marker.Map == shared.Map
                && Math.Abs(marker.Lat - shared.Lat) < 0.5
                && Math.Abs(marker.Lng - shared.Lng) < 0.5);

        /// <summary>"#rrggbb" only, so a code cannot inject anything into styles.</summary>
        public static bool IsColor(string? value) =>
            value != null && value.Length == 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
    }
}
