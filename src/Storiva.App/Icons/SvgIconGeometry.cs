using System.Drawing.Drawing2D;
using System.Globalization;
using System.Xml.Linq;

namespace Storiva.App.Icons;

/// <summary>
/// Converts the small SVG subset used by Lucide (path, line, circle, ellipse, rect, polyline,
/// polygon; stroke-only, 24×24 view box) into a GDI+ <see cref="GraphicsPath"/>.
/// Not a general SVG engine: unsupported elements raise <see cref="NotSupportedException"/>.
/// </summary>
internal static class SvgIconGeometry
{
    public const float ViewBoxSize = 24f;

    public static GraphicsPath Parse(string svgText)
    {
        var root = XDocument.Parse(svgText).Root ?? throw new FormatException("Empty SVG document.");
        if (root.Name.LocalName != "svg")
        {
            throw new FormatException("The root element must be <svg>.");
        }

        var viewBox = (string?)root.Attribute("viewBox");
        if (viewBox is not null && viewBox.Trim() != "0 0 24 24")
        {
            throw new NotSupportedException($"Only a 0 0 24 24 view box is supported (got '{viewBox}').");
        }

        var path = new GraphicsPath(FillMode.Winding);
        foreach (var element in root.Elements())
        {
            AddElement(path, element);
        }

        return path;
    }

    private static void AddElement(GraphicsPath path, XElement e)
    {
        switch (e.Name.LocalName)
        {
            case "path":
                AddPathData(path, (string?)e.Attribute("d") ?? string.Empty);
                break;
            case "line":
                path.StartFigure();
                path.AddLine(Num(e, "x1"), Num(e, "y1"), Num(e, "x2"), Num(e, "y2"));
                break;
            case "circle":
            {
                var r = Num(e, "r");
                path.StartFigure();
                path.AddEllipse(Num(e, "cx") - r, Num(e, "cy") - r, 2 * r, 2 * r);
                break;
            }
            case "ellipse":
            {
                var rx = Num(e, "rx");
                var ry = Num(e, "ry");
                path.StartFigure();
                path.AddEllipse(Num(e, "cx") - rx, Num(e, "cy") - ry, 2 * rx, 2 * ry);
                break;
            }
            case "rect":
                AddRect(path, e);
                break;
            case "polyline":
            case "polygon":
            {
                var points = ParsePoints((string?)e.Attribute("points") ?? string.Empty);
                if (points.Length < 2)
                {
                    break;
                }

                path.StartFigure();
                path.AddLines(points);
                if (e.Name.LocalName == "polygon")
                {
                    path.CloseFigure();
                }

                break;
            }
            default:
                throw new NotSupportedException($"SVG element <{e.Name.LocalName}> is not supported by the icon renderer.");
        }
    }

    private static void AddRect(GraphicsPath path, XElement e)
    {
        float x = Num(e, "x", 0), y = Num(e, "y", 0), w = Num(e, "width"), h = Num(e, "height");
        var rxAttr = e.Attribute("rx");
        var ryAttr = e.Attribute("ry");
        var rx = rxAttr is null ? (ryAttr is null ? 0 : Num(e, "ry")) : Num(e, "rx");
        var ry = ryAttr is null ? rx : Num(e, "ry");
        rx = Math.Min(rx, w / 2);
        ry = Math.Min(ry, h / 2);

        path.StartFigure();
        if (rx <= 0 || ry <= 0)
        {
            path.AddRectangle(new RectangleF(x, y, w, h));
            return;
        }

        path.AddArc(x, y, 2 * rx, 2 * ry, 180, 90);
        path.AddArc(x + w - 2 * rx, y, 2 * rx, 2 * ry, 270, 90);
        path.AddArc(x + w - 2 * rx, y + h - 2 * ry, 2 * rx, 2 * ry, 0, 90);
        path.AddArc(x, y + h - 2 * ry, 2 * rx, 2 * ry, 90, 90);
        path.CloseFigure();
    }

    private static float Num(XElement e, string name, float? fallback = null)
    {
        var text = (string?)e.Attribute(name);
        if (text is null)
        {
            return fallback ?? throw new FormatException($"<{e.Name.LocalName}> is missing '{name}'.");
        }

        return float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static PointF[] ParsePoints(string text)
    {
        var reader = new PathReader(text);
        var points = new List<PointF>();
        while (reader.NextIsNumber())
        {
            points.Add(new PointF(reader.ReadNumber(), reader.ReadNumber()));
        }

        return points.ToArray();
    }

    /// <summary>Path data per SVG 1.1 §8.3 (all commands, absolute and relative).</summary>
    internal static void AddPathData(GraphicsPath path, string d)
    {
        var reader = new PathReader(d);
        PointF current = default, start = default;
        PointF? lastCubicControl = null, lastQuadControl = null;
        char command = '\0';

        while (true)
        {
            if (reader.TryReadCommand(out var next))
            {
                command = next;
            }
            else if (reader.AtEnd)
            {
                break;
            }
            else if (command is '\0' or 'Z' or 'z')
            {
                throw new FormatException($"Unexpected data at position {reader.Position} in path '{d}'.");
            }

            // After the first coordinate pair, extra moveto pairs are implicit lineto commands.
            var relative = char.IsLower(command);
            PointF Point(float x, float y) => relative ? new PointF(current.X + x, current.Y + y) : new PointF(x, y);

            PointF? cubic = null, quad = null;
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    current = Point(reader.ReadNumber(), reader.ReadNumber());
                    start = current;
                    path.StartFigure();
                    command = relative ? 'l' : 'L';
                    break;
                case 'L':
                    current = LineTo(path, current, Point(reader.ReadNumber(), reader.ReadNumber()));
                    break;
                case 'H':
                {
                    var x = reader.ReadNumber();
                    current = LineTo(path, current, new PointF(relative ? current.X + x : x, current.Y));
                    break;
                }
                case 'V':
                {
                    var y = reader.ReadNumber();
                    current = LineTo(path, current, new PointF(current.X, relative ? current.Y + y : y));
                    break;
                }
                case 'C':
                {
                    var c1 = Point(reader.ReadNumber(), reader.ReadNumber());
                    var c2 = Point(reader.ReadNumber(), reader.ReadNumber());
                    var end = Point(reader.ReadNumber(), reader.ReadNumber());
                    path.AddBezier(current, c1, c2, end);
                    cubic = c2;
                    current = end;
                    break;
                }
                case 'S':
                {
                    var c1 = lastCubicControl is { } prev ? Reflect(prev, current) : current;
                    var c2 = Point(reader.ReadNumber(), reader.ReadNumber());
                    var end = Point(reader.ReadNumber(), reader.ReadNumber());
                    path.AddBezier(current, c1, c2, end);
                    cubic = c2;
                    current = end;
                    break;
                }
                case 'Q':
                {
                    var control = Point(reader.ReadNumber(), reader.ReadNumber());
                    var end = Point(reader.ReadNumber(), reader.ReadNumber());
                    AddQuadratic(path, current, control, end);
                    quad = control;
                    current = end;
                    break;
                }
                case 'T':
                {
                    var control = lastQuadControl is { } prev ? Reflect(prev, current) : current;
                    var end = Point(reader.ReadNumber(), reader.ReadNumber());
                    AddQuadratic(path, current, control, end);
                    quad = control;
                    current = end;
                    break;
                }
                case 'A':
                {
                    var rx = reader.ReadNumber();
                    var ry = reader.ReadNumber();
                    var rotation = reader.ReadNumber();
                    var largeArc = reader.ReadFlag();
                    var sweep = reader.ReadFlag();
                    var end = Point(reader.ReadNumber(), reader.ReadNumber());
                    AddArc(path, current, rx, ry, rotation, largeArc, sweep, end);
                    current = end;
                    break;
                }
                case 'Z':
                    path.CloseFigure();
                    current = start;
                    break;
                default:
                    throw new FormatException($"Unknown path command '{command}'.");
            }

            lastCubicControl = cubic;
            lastQuadControl = quad;
        }
    }

    private static PointF LineTo(GraphicsPath path, PointF from, PointF to)
    {
        path.AddLine(from, to);
        return to;
    }

    private static PointF Reflect(PointF control, PointF about) => new(2 * about.X - control.X, 2 * about.Y - control.Y);

    private static void AddQuadratic(GraphicsPath path, PointF p0, PointF control, PointF p2)
    {
        var c1 = new PointF(p0.X + 2f / 3f * (control.X - p0.X), p0.Y + 2f / 3f * (control.Y - p0.Y));
        var c2 = new PointF(p2.X + 2f / 3f * (control.X - p2.X), p2.Y + 2f / 3f * (control.Y - p2.Y));
        path.AddBezier(p0, c1, c2, p2);
    }

    /// <summary>Elliptical arc via endpoint-to-center conversion (SVG 1.1 implementation notes, F.6.5).</summary>
    internal static void AddArc(GraphicsPath path, PointF from, float rxIn, float ryIn, float rotationDeg, bool largeArc, bool sweep, PointF to)
    {
        if (from == to)
        {
            return;
        }

        double rx = Math.Abs(rxIn), ry = Math.Abs(ryIn);
        if (rx == 0 || ry == 0)
        {
            path.AddLine(from, to);
            return;
        }

        var phi = rotationDeg * Math.PI / 180;
        double cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx2 = (from.X - to.X) / 2.0, dy2 = (from.Y - to.Y) / 2.0;
        var x1p = cos * dx2 + sin * dy2;
        var y1p = -sin * dx2 + cos * dy2;

        var lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
        if (lambda > 1)
        {
            var scale = Math.Sqrt(lambda);
            rx *= scale;
            ry *= scale;
        }

        var numerator = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
        var denominator = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
        var coefficient = Math.Sqrt(Math.Max(0, numerator / denominator)) * (largeArc == sweep ? -1 : 1);
        var cxp = coefficient * rx * y1p / ry;
        var cyp = coefficient * -ry * x1p / rx;
        var cx = cos * cxp - sin * cyp + (from.X + to.X) / 2.0;
        var cy = sin * cxp + cos * cyp + (from.Y + to.Y) / 2.0;

        double ux = (x1p - cxp) / rx, uy = (y1p - cyp) / ry;
        double vx = (-x1p - cxp) / rx, vy = (-y1p - cyp) / ry;
        var theta1 = Math.Atan2(uy, ux);
        var delta = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
        if (!sweep && delta > 0)
        {
            delta -= 2 * Math.PI;
        }
        else if (sweep && delta < 0)
        {
            delta += 2 * Math.PI;
        }

        var segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-9));
        var step = delta / segments;
        var t = 4.0 / 3.0 * Math.Tan(step / 4);

        PointF At(double a) => new(
            (float)(cx + rx * Math.Cos(a) * cos - ry * Math.Sin(a) * sin),
            (float)(cy + rx * Math.Cos(a) * sin + ry * Math.Sin(a) * cos));

        (double X, double Y) Derivative(double a) => (
            -rx * Math.Sin(a) * cos - ry * Math.Cos(a) * sin,
            -rx * Math.Sin(a) * sin + ry * Math.Cos(a) * cos);

        var a1 = theta1;
        var p1 = from;
        for (var i = 0; i < segments; i++)
        {
            var a2 = a1 + step;
            var p2 = i == segments - 1 ? to : At(a2);
            var d1 = Derivative(a1);
            var d2 = Derivative(a2);
            var c1 = new PointF((float)(p1.X + t * d1.X), (float)(p1.Y + t * d1.Y));
            var c2 = new PointF((float)(p2.X - t * d2.X), (float)(p2.Y - t * d2.Y));
            path.AddBezier(p1, c1, c2, p2);
            a1 = a2;
            p1 = p2;
        }
    }

    /// <summary>Tokenizer for path data and point lists ("1.69-.9", ".5.5", "1e-3", compact arc flags).</summary>
    internal sealed class PathReader(string text)
    {
        private int _index;

        public int Position => _index;

        public bool AtEnd
        {
            get
            {
                SkipSeparators();
                return _index >= text.Length;
            }
        }

        public bool TryReadCommand(out char command)
        {
            SkipSeparators();
            if (_index < text.Length && char.IsAsciiLetter(text[_index]) && text[_index] is not ('e' or 'E'))
            {
                command = text[_index++];
                return true;
            }

            command = '\0';
            return false;
        }

        public bool NextIsNumber()
        {
            SkipSeparators();
            return _index < text.Length && (char.IsAsciiDigit(text[_index]) || text[_index] is '.' or '-' or '+');
        }

        public float ReadNumber()
        {
            if (!NextIsNumber())
            {
                throw new FormatException($"Number expected at position {_index}.");
            }

            var start = _index;
            if (text[_index] is '-' or '+')
            {
                _index++;
            }

            var seenDot = false;
            while (_index < text.Length && (char.IsAsciiDigit(text[_index]) || (text[_index] == '.' && !seenDot)))
            {
                seenDot |= text[_index] == '.';
                _index++;
            }

            if (_index < text.Length && text[_index] is 'e' or 'E')
            {
                _index++;
                if (_index < text.Length && text[_index] is '-' or '+')
                {
                    _index++;
                }

                while (_index < text.Length && char.IsAsciiDigit(text[_index]))
                {
                    _index++;
                }
            }

            return float.Parse(text.AsSpan(start, _index - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        public bool ReadFlag()
        {
            SkipSeparators();
            if (_index < text.Length && text[_index] is '0' or '1')
            {
                return text[_index++] == '1';
            }

            throw new FormatException($"Arc flag expected at position {_index}.");
        }

        private void SkipSeparators()
        {
            while (_index < text.Length && (char.IsWhiteSpace(text[_index]) || text[_index] == ','))
            {
                _index++;
            }
        }
    }
}
