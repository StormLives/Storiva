using System.Drawing.Drawing2D;
using System.Reflection;

namespace Storiva.App.Icons;

/// <summary>Names of the Lucide icons embedded in the app (see docs/ASSET_LICENSES.md).</summary>
internal static class Lucide
{
    public const string ArrowLeft = "arrow-left";
    public const string ArrowRight = "arrow-right";
    public const string ArrowUp = "arrow-up";
    public const string RefreshCw = "refresh-cw";
    public const string FolderPlus = "folder-plus";
    public const string Search = "search";
    public const string ListFilter = "list-filter";
    public const string House = "house";
    public const string Database = "database";
    public const string Folder = "folder";
    public const string FolderOpen = "folder-open";
    public const string File = "file";
    public const string FileText = "file-text";
    public const string FileImage = "file-image";
    public const string FileArchive = "file-archive";
    public const string FileSpreadsheet = "file-spreadsheet";
    public const string FileCode = "file-code";
    public const string FileVideo = "file-video";
    public const string FileMusic = "file-music";
    public const string Play = "play";
    public const string Square = "square";
    public const string CircleX = "circle-x";
    public const string Loader = "loader";
    public const string CircleAlert = "circle-alert";
    public const string List = "list";
    public const string Upload = "upload";
    public const string Download = "download";
    public const string Copy = "copy";
    public const string UserPlus = "user-plus";
    public const string Info = "info";
    public const string Settings = "settings";
    public const string PanelLeft = "panel-left";
    public const string PanelBottom = "panel-bottom";
    public const string LogOut = "log-out";
    public const string RotateCcw = "rotate-ccw";
    public const string ChevronRight = "chevron-right";
    public const string ChevronDown = "chevron-down";
    public const string Plus = "plus";
    public const string Trash2 = "trash-2";
    public const string Pencil = "pencil";
    public const string ExternalLink = "external-link";
    public const string Users = "users";
    public const string Lock = "lock";
    public const string Check = "check";
    public const string Star = "star";
    public const string CloudOff = "cloud-off";
    public const string Cloud = "cloud";
    public const string Server = "server";
    public const string X = "x";
}

/// <summary>Renders embedded Lucide SVGs to bitmaps at an exact pixel size, with caching.</summary>
internal static class LucideIcons
{
    private const string ResourcePrefix = "Storiva.Icons.";
    private static readonly Dictionary<string, GraphicsPath> Geometry = new(StringComparer.Ordinal);
    private static readonly Dictionary<(string Name, int Size, int Argb), Bitmap> Cache = new();
    private static readonly object Gate = new();

    /// <summary>All icon names available as embedded resources.</summary>
    public static IReadOnlyList<string> AvailableNames { get; } = typeof(LucideIcons).Assembly
        .GetManifestResourceNames()
        .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".svg", StringComparison.Ordinal))
        .Select(n => n[ResourcePrefix.Length..^4])
        .Order(StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// Returns a cached bitmap of <paramref name="pixelSize"/> × <paramref name="pixelSize"/> device pixels.
    /// The bitmap is owned by the cache: do not dispose it.
    /// </summary>
    public static Bitmap Get(string name, int pixelSize, Color color)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelSize, 8);
        lock (Gate)
        {
            var key = (name, pixelSize, color.ToArgb());
            if (!Cache.TryGetValue(key, out var bitmap))
            {
                bitmap = Render(GetGeometry(name), pixelSize, color);
                Cache[key] = bitmap;
            }

            return bitmap;
        }
    }

    /// <summary>Renders without caching (caller owns the bitmap). Used by tests and tools.</summary>
    public static Bitmap RenderUncached(string name, int pixelSize, Color color)
    {
        lock (Gate)
        {
            return Render(GetGeometry(name), pixelSize, color);
        }
    }

    private static GraphicsPath GetGeometry(string name)
    {
        if (Geometry.TryGetValue(name, out var path))
        {
            return path;
        }

        var resource = ResourcePrefix + name + ".svg";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new KeyNotFoundException($"Icon '{name}' is not embedded (resource '{resource}').");
        using var reader = new StreamReader(stream);
        path = SvgIconGeometry.Parse(reader.ReadToEnd());
        Geometry[name] = path;
        return path;
    }

    private static Bitmap Render(GraphicsPath geometry, int pixelSize, Color color)
    {
        var bitmap = new Bitmap(pixelSize, pixelSize, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;

        var scale = pixelSize / SvgIconGeometry.ViewBoxSize;
        g.ScaleTransform(scale, scale);

        // Lucide's stroke width is 2 units at 24 px. Below 20 px keep the line at ~1.5 device px
        // so small toolbar icons do not turn into blurry blobs.
        var strokeUnits = pixelSize < 20 ? 1.5f / scale : 2f;
        using var pen = new Pen(color, strokeUnits)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        g.DrawPath(pen, geometry);
        return bitmap;
    }
}
