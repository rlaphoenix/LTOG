using System.Globalization;
using System.Xml.Linq;

namespace LTOG;

internal static class SchemaParser
{
    /// <summary>
    /// Extract the interesting bits of an LTFS index snapshot: volume name,
    /// generation, on-tape update time, file count, volume UUID.
    /// </summary>
    public static SchemaItem Summarize(FileInfo f)
    {
        string? name = null, uuid = null, gen = null, updated = null;
        int fileCount = 0;
        try
        {
            using var reader = System.Xml.XmlReader.Create(f.FullName,
                new System.Xml.XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true });
            while (reader.Read())
            {
                if (reader.NodeType != System.Xml.XmlNodeType.Element) continue;
                switch (reader.LocalName)
                {
                    case "name" when name == null:           // first <name> = volume name
                        name = reader.ReadElementContentAsString();
                        break;
                    case "volumeuuid" when uuid == null:
                        uuid = reader.ReadElementContentAsString();
                        break;
                    case "generationnumber" when gen == null:
                        gen = reader.ReadElementContentAsString();
                        break;
                    case "updatetime" when updated == null:
                        updated = reader.ReadElementContentAsString();
                        break;
                    case "file":
                        fileCount++;
                        break;
                }
            }
        }
        catch
        {
            return new SchemaItem
            {
                Title = f.Name,
                GenLine = "Unreadable index snapshot",
                CaptureLine = $"Captured {f.LastWriteTime:yyyy-MM-dd HH:mm}, {f.Length / 1024.0:0.#} KB",
                Path = f.FullName,
                Captured = f.LastWriteTime,
            };
        }

        string updatedText = "";
        if (updated != null && DateTime.TryParse(updated, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
            updatedText = $", tape index written {dt.ToLocalTime():yyyy-MM-dd HH:mm}";

        return new SchemaItem
        {
            Title = string.IsNullOrEmpty(name) ? "(unlabelled volume)" : name,
            GenLine = $"Index generation {gen ?? "?"}{updatedText}",
            CaptureLine = $"Captured {f.LastWriteTime:yyyy-MM-dd HH:mm}, " +
                          $"{fileCount:N0} file{(fileCount == 1 ? "" : "s")}, {f.Length / 1024.0:0.#} KB",
            Uuid = uuid ?? "",
            Path = f.FullName,
            Captured = f.LastWriteTime,
        };
    }

    public static SchemaSnapshot Parse(FileInfo f)
    {
        var doc = XDocument.Load(f.FullName, LoadOptions.None);
        var root = doc.Root ?? throw new InvalidDataException("Missing LTFS index root element.");

        var rootDir = root.ElementsAny("directory").FirstOrDefault();
        return new SchemaSnapshot
        {
            Path = f.FullName,
            FormatVersion = Attr(root, "version"),
            Creator = Text(root, "creator"),
            VolumeUuid = Text(root, "volumeuuid"),
            GenerationNumber = Text(root, "generationnumber"),
            UpdatedText = FormatSchemaTime(Text(root, "updatetime")),
            HighestFileUid = Text(root, "highestfileuid"),
            PolicyOverrideText = IsTrue(Text(root, "allowpolicyupdate")) ? "Permitted" : "Not permitted",
            VolumeLockState = Text(root, "volumelockstate"),
            Root = rootDir != null
                ? ReadNode(rootDir, "", 0)
                : new SchemaNode { Name = "(empty index)", Path = "/", IsDirectory = true },
        };
    }

    private static SchemaNode ReadNode(XElement element, string parentPath, int depth)
    {
        bool isDirectory = element.Name.LocalName == "directory";
        var node = new SchemaNode
        {
            Name = Text(element, "name"),
            IsDirectory = isDirectory,
            ReadOnly = IsTrue(Text(element, "readonly")),
            FileUid = Text(element, "fileuid"),
            CreationTime = FormatSchemaTime(Text(element, "creationtime")),
            ModifyTime = FormatSchemaTime(Text(element, "modifytime")),
            AccessTime = FormatSchemaTime(Text(element, "accesstime")),
            BackupTime = FormatSchemaTime(Text(element, "backuptime")),
        };

        if (long.TryParse(Text(element, "length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
            node.Length = length;

        if (string.IsNullOrWhiteSpace(node.Name))
            node.Name = isDirectory && depth == 0 ? "/" : "(unnamed)";
        node.Path = CombineSchemaPath(parentPath, node.Name, depth);

        var firstExtent = element.ElementsAny("extentinfo").SelectMany(e => e.ElementsAny("extent")).FirstOrDefault();
        if (firstExtent != null)
            node.FirstLocationText = ReadLocation(firstExtent);

        var contents = element.ElementsAny("contents").FirstOrDefault();
        if (contents != null)
        {
            foreach (var child in contents.Elements().Where(e => e.Name.LocalName is "directory" or "file"))
                node.Children.Add(ReadNode(child, node.Path, depth + 1));
        }

        return node;
    }

    private static string ReadLocation(XElement element) =>
        $"Partition {Blank(Text(element, "partition"))} starting from block {Blank(Text(element, "startblock"))}";

    private static string CombineSchemaPath(string parentPath, string name, int depth)
    {
        if (depth == 0) return "";
        string clean = string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;
        if (string.IsNullOrWhiteSpace(parentPath))
            return "\\" + clean;
        return parentPath.TrimEnd('\\') + "\\" + clean;
    }

    private static string FormatSchemaTime(string value)
    {
        if (DateTimeOffset.TryParse(value, null, DateTimeStyles.AssumeUniversal, out var dt))
            return dt.UtcDateTime.ToString("yyyy-MM-dd 'at' HH:mm:ss '(UTC)'");
        return value;
    }

    private static bool IsTrue(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        value == "1";

    private static string Text(XElement element, string localName) =>
        element.ElementsAny(localName).FirstOrDefault()?.Value.Trim() ?? "";

    private static string Attr(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value.Trim() ?? "";

    private static IEnumerable<XElement> ElementsAny(this XContainer element, string localName) =>
        element.Elements().Where(e => e.Name.LocalName == localName);

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "?" : value;
}
