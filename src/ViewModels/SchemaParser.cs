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

        var snapshot = new SchemaSnapshot
        {
            Path = f.FullName,
            FileName = f.Name,
            FormatVersion = Attr(root, "version"),
            Creator = Text(root, "creator"),
            VolumeUuid = Text(root, "volumeuuid"),
            GenerationNumber = Text(root, "generationnumber"),
            UpdatedText = FormatSchemaTime(Text(root, "updatetime")),
            LocationText = ReadLocation(ElementAny(root, "location")),
            PreviousLocationText = ReadLocation(ElementAny(root, "previousgenerationlocation")),
            HighestFileUid = Text(root, "highestfileuid"),
            PolicyOverrideText = IsTrue(Text(root, "allowpolicyupdate")) ? "Permitted" : "Not permitted",
            VolumeLockState = Text(root, "volumelockstate"),
        };

        var rootDir = root.ElementsAny("directory").FirstOrDefault();
        if (rootDir != null)
        {
            snapshot.Root = ReadNode(rootDir, "", 0, snapshot);
            snapshot.VolumeName = string.IsNullOrWhiteSpace(snapshot.Root.Name)
                ? "(unlabelled volume)"
                : snapshot.Root.Name;
            snapshot.Root.Expanded = true;
        }
        else
        {
            snapshot.Root = new SchemaNode
            {
                Name = "(empty index)",
                Path = "/",
                Type = "Directory",
                IsDirectory = true,
                Expanded = true,
            };
        }

        return snapshot;
    }

    private static SchemaNode ReadNode(XElement element, string parentPath, int depth, SchemaSnapshot snapshot)
    {
        bool isDirectory = element.Name.LocalName == "directory";
        var node = new SchemaNode
        {
            Name = Text(element, "name"),
            Type = isDirectory ? "Directory" : "File",
            IsDirectory = isDirectory,
            ReadOnly = IsTrue(Text(element, "readonly")),
            OpenForWrite = IsTrue(Text(element, "openforwrite")),
            FileUid = Text(element, "fileuid"),
            CreationTime = FormatSchemaTime(Text(element, "creationtime")),
            ChangeTime = FormatSchemaTime(Text(element, "changetime")),
            ModifyTime = FormatSchemaTime(Text(element, "modifytime")),
            AccessTime = FormatSchemaTime(Text(element, "accesstime")),
            BackupTime = FormatSchemaTime(Text(element, "backuptime")),
            Depth = depth,
        };

        if (long.TryParse(Text(element, "length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
        {
            node.Length = length;
            snapshot.TotalBytes += length;
        }

        if (string.IsNullOrWhiteSpace(node.Name))
            node.Name = isDirectory && depth == 0 ? "/" : "(unnamed)";
        node.Path = CombineSchemaPath(parentPath, node.Name, depth);

        foreach (var extentInfo in element.ElementsAny("extentinfo"))
        {
            foreach (var extent in extentInfo.ElementsAny("extent"))
                node.Extents.Add(ReadExtent(extent));
        }

        var contents = element.ElementsAny("contents").FirstOrDefault();
        if (contents != null)
        {
            foreach (var child in contents.Elements().Where(e => e.Name.LocalName is "directory" or "file"))
                node.Children.Add(ReadNode(child, node.Path, depth + 1, snapshot));
        }

        if (isDirectory) snapshot.DirectoryCount++;
        else snapshot.FileCount++;

        return node;
    }

    private static SchemaExtent ReadExtent(XElement element) => new()
    {
        FileOffset = Text(element, "fileoffset"),
        Partition = Text(element, "partition"),
        StartBlock = Text(element, "startblock"),
        ByteOffset = Text(element, "byteoffset"),
        ByteCount = Text(element, "bytecount"),
    };

    private static string ReadLocation(XElement? element)
    {
        if (element == null) return "Not specified";
        string partition = Text(element, "partition");
        string block = Text(element, "startblock");
        return $"Partition {Blank(partition)} starting from block {Blank(block)}";
    }

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

    private static XElement? ElementAny(XContainer element, string localName) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "?" : value;
}
