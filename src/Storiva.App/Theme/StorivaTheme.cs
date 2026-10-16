using Storiva.Core.Listing;

namespace Storiva.App.Theme;

/// <summary>
/// Single source of colours. Controls read colours from <see cref="Current"/> (never hard-coded), so the theme can
/// change at run time: <see cref="ThemeService"/> swaps the palette and <see cref="ThemeApplier"/> re-colours the
/// open windows. Light values are Storiva's original palette; Dark is its counterpart
/// (docs/THEMES.md).
/// </summary>
internal sealed class StorivaPalette
{
    public static StorivaPalette Light { get; } = new() { Name = "Light" };

    /// <summary>
    /// Dark neutral surfaces (never pure black), same teal identity, lighter accents for contrast.
    /// Contrast on Surface #20262A: TextPrimary ≈ 13:1, TextSecondary ≈ 7:1, Accent ≈ 6:1, Link ≈ 7.5:1.
    /// </summary>
    public static StorivaPalette Dark { get; } = new()
    {
        Name = "Dark",
        IsDark = true,
        Chrome = ColorTranslator.FromHtml("#181D20"),
        Surface = ColorTranslator.FromHtml("#20262A"),
        Toolbar = ColorTranslator.FromHtml("#1C2225"),
        Border = ColorTranslator.FromHtml("#343D42"),
        BorderStrong = ColorTranslator.FromHtml("#4F5B61"),
        GridLine = ColorTranslator.FromHtml("#2B3337"),
        HeaderBackground = ColorTranslator.FromHtml("#262D31"),
        TextPrimary = ColorTranslator.FromHtml("#E4EAEC"),
        TextSecondary = ColorTranslator.FromHtml("#A3B1B7"),
        TextDisabled = ColorTranslator.FromHtml("#6B787E"),
        TextOnAccent = ColorTranslator.FromHtml("#0B1A19"),
        Accent = ColorTranslator.FromHtml("#3DB5AC"),
        AccentStrong = ColorTranslator.FromHtml("#67CDC5"),
        HoverFill = ColorTranslator.FromHtml("#26393A"),
        PressedFill = ColorTranslator.FromHtml("#2C4A48"),
        CheckedFill = ColorTranslator.FromHtml("#284241"),
        SelectionFocused = ColorTranslator.FromHtml("#1E4F4B"),
        SelectionUnfocused = ColorTranslator.FromHtml("#2E3639"),
        FocusRing = ColorTranslator.FromHtml("#4FC9BF"),
        Link = ColorTranslator.FromHtml("#5FCEC5"),
        Success = ColorTranslator.FromHtml("#4CC38A"),
        Warning = ColorTranslator.FromHtml("#E8A94F"),
        Danger = ColorTranslator.FromHtml("#F2766B"),
        Info = ColorTranslator.FromHtml("#6AAEE6"),
        ProgressTrack = ColorTranslator.FromHtml("#353F43"),
        IconDefault = ColorTranslator.FromHtml("#C1CCD1"),
        IconDisabled = ColorTranslator.FromHtml("#5A666C"),
        IconFolder = ColorTranslator.FromHtml("#E2AD52"),
        IconBucket = ColorTranslator.FromHtml("#47C2B8"),
        IconImage = ColorTranslator.FromHtml("#A992EE"),
        IconArchive = ColorTranslator.FromHtml("#CF9A62"),
        IconData = ColorTranslator.FromHtml("#63C485"),
        IconMedia = ColorTranslator.FromHtml("#E883AB"),
    };

    /// <summary>Palette in use (UI thread only). Changed only by <see cref="ThemeService"/>.</summary>
    public static StorivaPalette Current { get; internal set; } = Light;

    public string Name { get; init; } = "Light";

    public bool IsDark { get; init; }

    // Surfaces
    public Color Chrome { get; init; } = ColorTranslator.FromHtml("#F3F5F6");
    public Color Surface { get; init; } = Color.White;
    public Color Toolbar { get; init; } = ColorTranslator.FromHtml("#F8FAFA");
    public Color Border { get; init; } = ColorTranslator.FromHtml("#D6DDE0");
    public Color BorderStrong { get; init; } = ColorTranslator.FromHtml("#A7B3B8");
    public Color GridLine { get; init; } = ColorTranslator.FromHtml("#EDF1F2");
    public Color HeaderBackground { get; init; } = ColorTranslator.FromHtml("#F6F8F8");

    // Text (contrast against white: primary 15.6:1, secondary 6.3:1, accent 5.1:1)
    public Color TextPrimary { get; init; } = ColorTranslator.FromHtml("#1C2428");
    public Color TextSecondary { get; init; } = ColorTranslator.FromHtml("#55626A");
    public Color TextDisabled { get; init; } = ColorTranslator.FromHtml("#8C979D");
    public Color TextOnAccent { get; init; } = Color.White;

    // Brand / interaction
    public Color Accent { get; init; } = ColorTranslator.FromHtml("#0E7C78");
    public Color AccentStrong { get; init; } = ColorTranslator.FromHtml("#0A5E5B");
    public Color HoverFill { get; init; } = ColorTranslator.FromHtml("#E5F2F1");
    public Color PressedFill { get; init; } = ColorTranslator.FromHtml("#CBE7E4");
    public Color CheckedFill { get; init; } = ColorTranslator.FromHtml("#D7EEEC");
    public Color SelectionFocused { get; init; } = ColorTranslator.FromHtml("#C6E7E3");
    public Color SelectionUnfocused { get; init; } = ColorTranslator.FromHtml("#E4EAEB");
    public Color FocusRing { get; init; } = ColorTranslator.FromHtml("#0E7C78");
    public Color Link { get; init; } = ColorTranslator.FromHtml("#0A6966");

    // Status
    public Color Success { get; init; } = ColorTranslator.FromHtml("#16794A");
    public Color Warning { get; init; } = ColorTranslator.FromHtml("#A15C07");
    public Color Danger { get; init; } = ColorTranslator.FromHtml("#B42318");
    public Color Info { get; init; } = ColorTranslator.FromHtml("#1B6CA8");
    public Color ProgressTrack { get; init; } = ColorTranslator.FromHtml("#E3E9EA");

    // Icons
    public Color IconDefault { get; init; } = ColorTranslator.FromHtml("#3D4B52");
    public Color IconDisabled { get; init; } = ColorTranslator.FromHtml("#B3BCC0");
    public Color IconFolder { get; init; } = ColorTranslator.FromHtml("#B7791F");
    public Color IconBucket { get; init; } = ColorTranslator.FromHtml("#0E7C78");
    public Color IconImage { get; init; } = ColorTranslator.FromHtml("#6D4CC2");
    public Color IconArchive { get; init; } = ColorTranslator.FromHtml("#8A5A2B");
    public Color IconData { get; init; } = ColorTranslator.FromHtml("#1F7A3F");
    public Color IconMedia { get; init; } = ColorTranslator.FromHtml("#B83A6B");

    /// <summary>Background roles, most specific first; used to map colours already assigned to controls.</summary>
    internal static readonly Func<StorivaPalette, Color>[] BackgroundRoles =
    [
        p => p.Surface, p => p.Chrome, p => p.Toolbar, p => p.HeaderBackground, p => p.SelectionFocused, p => p.SelectionUnfocused,
        p => p.HoverFill, p => p.PressedFill, p => p.CheckedFill, p => p.Accent, p => p.AccentStrong, p => p.ProgressTrack,
        p => p.Border, p => p.BorderStrong, p => p.GridLine,
    ];

    /// <summary>Foreground roles (text, links, status and icon colours), most specific first.</summary>
    internal static readonly Func<StorivaPalette, Color>[] ForegroundRoles =
    [
        p => p.TextPrimary, p => p.TextSecondary, p => p.TextDisabled, p => p.Link, p => p.Accent, p => p.AccentStrong,
        p => p.Success, p => p.Warning, p => p.Danger, p => p.Info, p => p.TextOnAccent, p => p.IconDefault, p => p.IconDisabled,
        p => p.Border, p => p.BorderStrong, p => p.GridLine, p => p.FocusRing,
    ];

    public Color IconFor(FileCategory category) => category switch
    {
        FileCategory.Bucket => IconBucket,
        FileCategory.Folder => IconFolder,
        FileCategory.Image => IconImage,
        FileCategory.Archive => IconArchive,
        FileCategory.Spreadsheet or FileCategory.Code => IconData,
        FileCategory.Video or FileCategory.Audio => IconMedia,
        _ => IconDefault,
    };
}

/// <summary>Spacing and size constants in logical units (96 DPI); scale with LogicalToDeviceUnits.</summary>
internal static class Metrics
{
    public const int SpaceXs = 4;
    public const int SpaceS = 8;
    public const int SpaceM = 12;
    public const int SpaceL = 16;
    public const int SpaceXl = 24;

    public const int IconSmall = 16;
    public const int IconLarge = 20;
    public const int DialogHeaderIcon = 32;

    // Values marked [F] were measured on the reference layout at 96 DPI;
    // here they are logical units scaled with LogicalToDeviceUnits.
    public const int RowHeight = 20;              // [F] list rows 20 px
    public const int HeaderHeight = 24;           // [F] column header ~24 px
    public const int StripHeight = 25;            // [F] path bar / bucket bar 25 px
    public const int SplitterWidth = 7;           // reference 9–10 px with a dotted grip; slightly thinner here
    public const int OuterMargin = 3;             // [F] 3 px between window edge and panes
    public const int CornerRadius = 3;

    // Client size chosen so the outer window is 1024×769 at 96 DPI, like the reference capture.
    public const int InitialWindowWidth = 1008;
    public const int InitialWindowHeight = 730;
    public const int MinimumWindowWidth = 720;
    public const int MinimumWindowHeight = 480;
    public const int NavigationPaneWidth = 240;   // [F] bucket pane 240 px
    public const int TaskPanelHeight = 300;       // tabs + task list + footer (reference ≈ 330 px of 769)
}

internal static class StorivaFonts
{
    private static Font? _ui;
    private static Font? _uiBold;
    private static Font? _title;

    /// <summary>Segoe UI 9 pt, the Windows UI font (same as ApplicationDefaultFont).</summary>
    public static Font Ui => _ui ??= new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

    public static Font UiBold => _uiBold ??= new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);

    public static Font Title => _title ??= new Font("Segoe UI Semibold", 12f, FontStyle.Regular, GraphicsUnit.Point);
}
