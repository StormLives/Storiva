using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using Storiva.App.Icons;

namespace Storiva.Tests;

public class SvgIconGeometryTests
{
    [Fact]
    public void Every_embedded_icon_parses_and_renders_visible_pixels()
    {
        Assert.Equal(49, LucideIcons.AvailableNames.Count);
        foreach (var name in LucideIcons.AvailableNames)
        {
            foreach (var size in new[] { 16, 20, 24, 32 })
            {
                using var bitmap = LucideIcons.RenderUncached(name, size, Color.Black);
                Assert.Equal(new Size(size, size), bitmap.Size);
                var inked = CountInked(bitmap);
                Assert.True(inked > size, $"{name} at {size}px drew only {inked} pixels");
                Assert.True(inked < size * size * 0.8, $"{name} at {size}px is almost solid ({inked} pixels)");
            }
        }
    }

    [Fact]
    public void Missing_icon_reports_its_name()
    {
        var ex = Assert.Throws<KeyNotFoundException>(() => LucideIcons.RenderUncached("does-not-exist", 16, Color.Black));
        Assert.Contains("does-not-exist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Path_data_supports_relative_and_horizontal_vertical_commands()
    {
        using var path = new GraphicsPath();
        SvgIconGeometry.AddPathData(path, "M2 2h20v20H2z");
        AssertBounds(path, 2, 2, 20, 20);
    }

    [Fact]
    public void Path_data_accepts_compact_number_forms()
    {
        using var path = new GraphicsPath();
        SvgIconGeometry.AddPathData(path, "M1.5.5L3-.5l1e1,2");
        var points = path.PathPoints;
        Assert.Equal(new PointF(1.5f, 0.5f), points[0]);
        Assert.Equal(new PointF(3f, -0.5f), points[1]);
        Assert.Equal(new PointF(13f, 1.5f), points[^1]);
    }

    [Fact]
    public void Arcs_follow_the_svg_endpoint_parameterisation()
    {
        // Two half arcs form a full circle of radius 10 centred at (12,12).
        using var path = new GraphicsPath();
        SvgIconGeometry.AddPathData(path, "M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0");
        AssertBounds(path, 2, 2, 20, 20, tolerance: 0.05f);
    }

    [Fact]
    public void Arc_flags_may_be_written_without_separators()
    {
        using var path = new GraphicsPath();
        SvgIconGeometry.AddPathData(path, "M2 12a10 10 0 1020 0");
        var bounds = path.GetBounds();
        Assert.InRange(bounds.Bottom, 21.9f, 22.1f); // sweep=0 from left to right goes through the bottom
    }

    [Fact]
    public void Quadratic_and_smooth_curves_are_supported()
    {
        using var path = new GraphicsPath();
        SvgIconGeometry.AddPathData(path, "M0 0Q5 10 10 0T20 0S25 5 30 0");
        Assert.True(path.PointCount > 4);
    }

    [Fact]
    public void Unsupported_elements_are_rejected()
    {
        const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><text>x</text></svg>";
        Assert.Throws<NotSupportedException>(() => SvgIconGeometry.Parse(svg));
    }

    [Fact]
    public void Brand_icon_is_embedded_and_has_the_expected_sizes()
    {
        using var icon = BrandIcon.Create();
        using var large = BrandIcon.Bitmap(64);
        Assert.Equal(64, large.Width);
        Assert.True(CountInked(large) > 64 * 64 / 2);
    }

    private static int CountInked(Bitmap bitmap)
    {
        var count = 0;
        for (var x = 0; x < bitmap.Width; x++)
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                if (bitmap.GetPixel(x, y).A > 32)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static void AssertBounds(GraphicsPath path, float x, float y, float w, float h, float tolerance = 0.001f)
    {
        var b = path.GetBounds();
        Assert.InRange(b.X, x - tolerance, x + tolerance);
        Assert.InRange(b.Y, y - tolerance, y + tolerance);
        Assert.InRange(b.Width, w - 2 * tolerance, w + 2 * tolerance);
        Assert.InRange(b.Height, h - 2 * tolerance, h + 2 * tolerance);
    }
}

/// <summary>
/// Guards the network boundary: only Storiva.Infrastructure (AWS SDK) may do networking; Core and the UI may not,
/// and no Storiva code may weaken TLS certificate validation.
/// </summary>
public class IsolationTests
{
    private static readonly string[] ForbiddenAssemblies =
    [
        "System.Net.Http", "System.Net.Sockets", "System.Net.Requests", "System.Net.WebClient",
        "System.Net.WebSockets", "System.Net.WebSockets.Client", "System.Net.NetworkInformation",
        "System.Net.Mail", "System.Net.Ping", "AWSSDK.Core", "AWSSDK.S3",
    ];

    public static TheoryData<string> NetworkFreeAssemblies => new() { "Storiva.Core", "Storiva" };

    [Theory]
    [MemberData(nameof(NetworkFreeAssemblies))]
    public void Core_and_ui_do_not_reference_networking_libraries(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);
        var references = assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain(references, r => ForbiddenAssemblies.Contains(r, StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(NetworkFreeAssemblies))]
    public void Core_and_ui_do_not_use_networking_types(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);
        var offending = assembly.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Select(f => f.FieldType))
            .Where(t => t.Namespace is { } ns && (ns.StartsWith("System.Net", StringComparison.Ordinal) || ns.StartsWith("Amazon", StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .Distinct()
            .ToList();
        Assert.Empty(offending);
    }

    [Fact]
    public void Infrastructure_networking_goes_through_the_aws_sdk_only()
    {
        var references = Assembly.Load("Storiva.Infrastructure").GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.Contains("AWSSDK.S3", references);
        Assert.DoesNotContain(references, r => r is "System.Net.Requests" or "System.Net.WebClient" or "System.Net.WebSockets.Client" or "System.Net.Mail");
    }

    /// <summary>Source scan: no certificate-validation bypass, no global ServicePointManager changes, no DisableSSL.</summary>
    [Fact]
    public void No_code_weakens_tls_validation()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        var forbidden = new[]
        {
            "ServerCertificateCustomValidationCallback", "RemoteCertificateValidationCallback", "DangerousAcceptAnyServerCertificateValidator",
            "ServicePointManager", "CheckCertificateRevocationList = false", "DisableSSL", "SslProtocols.None",
        };
        var hits = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => forbidden.Where(token => File.ReadAllText(f).Contains(token, StringComparison.Ordinal)).Select(token => $"{Path.GetFileName(f)}: {token}"))
            .ToList();
        Assert.Empty(hits);
    }

    /// <summary>Source scan: secrets are never formatted into log lines or exception texts by name.</summary>
    [Fact]
    public void Secrets_are_not_interpolated_into_messages()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        var suspicious = new[] { "{credentials.SecretAccessKey", "{SecretAccessKey", "{_secretKey.Text", "{secret}", "{credentials.SessionToken" };
        var hits = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => suspicious.Where(token => File.ReadAllText(f).Contains(token, StringComparison.Ordinal)).Select(token => $"{Path.GetFileName(f)}: {token}"))
            .ToList();
        Assert.Empty(hits);
    }

    internal static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Storiva.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Storiva.sln not found above the test output folder.");
    }
}
