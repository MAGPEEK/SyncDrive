using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.Ui;

namespace SyncDrive.Client.Tray.Controls;

/// <summary>Zustandsabzeichen: farbiger Kreis mit weißem Zeichen (Haken, kreisende Pfeile, Ausrufezeichen, Pause, Punkte, Strich) oder nur ein Punkt.</summary>
public enum BadgeKind
{
    None,
    Done,
    Sync,
    Error,
    Pause,
    Connecting,
    Disabled,
    Notification,
}

/// <summary>Eigene Farben und Zeichen (Prinzip „Grundzeichen + Abzeichen“).</summary>
public static class Palette
{
    public static readonly Color Accent = Color.Parse("#0F8B8D");
    public static readonly Color AccentLight = Color.Parse("#E4F2F2");
    public static readonly Color Green = Color.Parse("#2E9B57");
    public static readonly Color Blue = Color.Parse("#2F78D0");
    public static readonly Color Grey = Color.Parse("#8C9199");
    public static readonly Color Red = Color.Parse("#D64541");
    public static readonly Color Pale = Color.Parse("#B8BDC3");
    public static readonly Color Text = Color.Parse("#2B2F33");

    public static Color Of(StatusColor color) => color switch
    {
        StatusColor.Green => Green,
        StatusColor.Blue => Blue,
        StatusColor.Grey => Grey,
        StatusColor.Red => Red,
        StatusColor.Pale => Pale,
        _ => Accent,
    };
}

public static class Badges
{
    public static BadgeKind For(TrayState state) => state switch
    {
        TrayState.UpToDate => BadgeKind.Done,
        TrayState.Syncing => BadgeKind.Sync,
        TrayState.Paused => BadgeKind.Pause,
        TrayState.Error => BadgeKind.Error,
        TrayState.Disconnected => BadgeKind.Connecting,
        _ => BadgeKind.None,
    };

    public static BadgeKind For(TaskState state) => state switch
    {
        TaskState.Done => BadgeKind.Done,
        TaskState.Syncing => BadgeKind.Sync,
        TaskState.Paused => BadgeKind.Pause,
        TaskState.Error => BadgeKind.Error,
        TaskState.Disabled => BadgeKind.Disabled,
        _ => BadgeKind.Connecting,
    };

    public static Color ColorOf(BadgeKind kind) => kind switch
    {
        BadgeKind.Done => Palette.Green,
        BadgeKind.Sync => Palette.Blue,
        BadgeKind.Pause or BadgeKind.Disabled => Palette.Grey,
        BadgeKind.Error or BadgeKind.Notification => Palette.Red,
        BadgeKind.Connecting => Palette.Pale,
        _ => Palette.Accent,
    };

    /// <summary>Zeichnet das Abzeichen in <paramref name="r"/> (quadratisch gedacht).</summary>
    public static void Draw(DrawingContext ctx, Rect r, BadgeKind kind)
    {
        if (kind == BadgeKind.None)
        {
            return;
        }

        var center = r.Center;
        var radius = Math.Min(r.Width, r.Height) / 2;
        ctx.DrawEllipse(new SolidColorBrush(ColorOf(kind)), new Pen(Brushes.White, Math.Max(1, radius * 0.12)), center, radius, radius);
        var u = radius / 10; // Raster für die Zeichen
        var pen = new Pen(Brushes.White, 2.2 * u, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        switch (kind)
        {
            case BadgeKind.Done:
                ctx.DrawGeometry(null, pen, Polyline(center, u, (-4.5, 0.2), (-1.4, 3.4), (4.6, -3.2)));
                break;
            case BadgeKind.Sync:
                ctx.DrawGeometry(null, pen, Arc(center, 5 * u, 200, 340));
                ctx.DrawGeometry(null, pen, Arc(center, 5 * u, 20, 160));
                ctx.DrawGeometry(null, pen, Polyline(center, u, (2.2, -6.4), (4.7, -1.8), (0.2, -1.2)));
                ctx.DrawGeometry(null, pen, Polyline(center, u, (-2.2, 6.4), (-4.7, 1.8), (-0.2, 1.2)));
                break;
            case BadgeKind.Error:
                ctx.DrawLine(pen, center + new Vector(0, -5 * u), center + new Vector(0, 1.2 * u));
                ctx.DrawEllipse(Brushes.White, null, center + new Vector(0, 4.6 * u), 1.4 * u, 1.4 * u);
                break;
            case BadgeKind.Pause:
                ctx.DrawLine(pen, center + new Vector(-2.4 * u, -4.2 * u), center + new Vector(-2.4 * u, 4.2 * u));
                ctx.DrawLine(pen, center + new Vector(2.4 * u, -4.2 * u), center + new Vector(2.4 * u, 4.2 * u));
                break;
            case BadgeKind.Connecting:
                foreach (var x in new[] { -4.0, 0, 4.0 })
                {
                    ctx.DrawEllipse(Brushes.White, null, center + new Vector(x * u, 0), 1.4 * u, 1.4 * u);
                }

                break;
            case BadgeKind.Disabled:
                ctx.DrawLine(pen, center + new Vector(-4.5 * u, 0), center + new Vector(4.5 * u, 0));
                break;
        }
    }

    /// <summary>Grundzeichen der App: Ordner auf petrolfarbener Kachel mit einem Bogen (Abgleich).</summary>
    public static void DrawGlyph(DrawingContext ctx, Rect r, double opacity = 1)
    {
        using var _ = ctx.PushOpacity(opacity);
        var s = r.Width / 64;
        ctx.DrawRectangle(new SolidColorBrush(Palette.Accent), null, new Rect(r.X + 2 * s, r.Y + 2 * s, 60 * s, 60 * s), 14 * s, 14 * s);
        var folder = new StreamGeometry();
        using (var g = folder.Open())
        {
            g.BeginFigure(new Point(r.X + 13 * s, r.Y + 20 * s), true);
            g.LineTo(new Point(r.X + 26 * s, r.Y + 20 * s));
            g.LineTo(new Point(r.X + 30 * s, r.Y + 25 * s));
            g.LineTo(new Point(r.X + 51 * s, r.Y + 25 * s));
            g.LineTo(new Point(r.X + 51 * s, r.Y + 47 * s));
            g.LineTo(new Point(r.X + 13 * s, r.Y + 47 * s));
            g.EndFigure(true);
        }

        ctx.DrawGeometry(Brushes.White, null, folder);
        var arc = new Pen(new SolidColorBrush(Palette.Accent), 3.4 * s, lineCap: PenLineCap.Round);
        ctx.DrawGeometry(null, arc, Arc(new Point(r.X + 32 * s, r.Y + 37 * s), 7 * s, 210, 510));
        ctx.DrawGeometry(null, arc, Polyline(new Point(r.X + 32 * s, r.Y + 37 * s), s, (3.5, -9.5), (7, -6), (2.5, -3.5)));
    }

    /// <summary>Tray-Symbol: Grundzeichen, Abzeichen unten rechts; getrennt = blass ohne Abzeichen; Benachrichtigung = roter Punkt oben rechts.</summary>
    public static Bitmap TrayBitmap(TrayState state, bool hasNotifications, bool minimal)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(64, 64));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            var disconnected = state == TrayState.Disconnected;
            DrawGlyph(ctx, new Rect(0, 0, 64, 64), disconnected ? 0.4 : 1);
            var badge = For(state);
            if (!disconnected && !(minimal && badge == BadgeKind.Done) && badge != BadgeKind.None)
            {
                Draw(ctx, new Rect(34, 34, 30, 30), badge);
            }

            if (hasNotifications)
            {
                Draw(ctx, new Rect(46, 0, 18, 18), BadgeKind.Notification);
            }
        }

        return bitmap;
    }

    /// <summary>Fenstersymbol aller Fenster: das Grundzeichen.</summary>
    public static WindowIcon AppIcon()
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(64, 64));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            DrawGlyph(ctx, new Rect(0, 0, 64, 64));
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;
        return new WindowIcon(stream);
    }

    private static StreamGeometry Polyline(Point center, double u, params (double X, double Y)[] points)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(center + new Vector(points[0].X * u, points[0].Y * u), false);
        foreach (var (x, y) in points.Skip(1))
        {
            g.LineTo(center + new Vector(x * u, y * u));
        }

        g.EndFigure(false);
        return geometry;
    }

    private static StreamGeometry Arc(Point center, double radius, double fromDegrees, double toDegrees)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        const int steps = 24;
        for (var i = 0; i <= steps; i++)
        {
            var angle = (fromDegrees + ((toDegrees - fromDegrees) * i / steps)) * Math.PI / 180;
            var point = center + new Vector(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
            if (i == 0)
            {
                g.BeginFigure(point, false);
            }
            else
            {
                g.LineTo(point);
            }
        }

        g.EndFigure(false);
        return geometry;
    }
}

/// <summary>Abzeichen als Steuerelement (Panel-Kopf, Aufgabenzeilen, Statuszeile).</summary>
public sealed class StatusBadge : Control
{
    public static readonly StyledProperty<BadgeKind> KindProperty = AvaloniaProperty.Register<StatusBadge, BadgeKind>(nameof(Kind));

    static StatusBadge() => AffectsRender<StatusBadge>(KindProperty);

    public BadgeKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public override void Render(DrawingContext context) => Badges.Draw(context, new Rect(Bounds.Size), Kind);
}

/// <summary>Grundzeichen der App als Steuerelement (Fenstertitel, Info).</summary>
public sealed class AppGlyph : Control
{
    public override void Render(DrawingContext context) => Badges.DrawGlyph(context, new Rect(Bounds.Size));
}

/// <summary>Ordnersymbol mit Zustandsabzeichen (Aufgabenzeile) oder Server-/Computer-Symbol (Assistent, Servergruppe).</summary>
public sealed class FolderBadge : Control
{
    public static readonly StyledProperty<BadgeKind> KindProperty = AvaloniaProperty.Register<FolderBadge, BadgeKind>(nameof(Kind));

    static FolderBadge() => AffectsRender<FolderBadge>(KindProperty);

    public BadgeKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var s = Bounds.Width / 32;
        var folder = new StreamGeometry();
        using (var g = folder.Open())
        {
            g.BeginFigure(new Point(2 * s, 6 * s), true);
            g.LineTo(new Point(11 * s, 6 * s));
            g.LineTo(new Point(14 * s, 9 * s));
            g.LineTo(new Point(28 * s, 9 * s));
            g.LineTo(new Point(28 * s, 26 * s));
            g.LineTo(new Point(2 * s, 26 * s));
            g.EndFigure(true);
        }

        context.DrawGeometry(new SolidColorBrush(Color.Parse("#E8B64C")), null, folder);
        Badges.Draw(context, new Rect(17 * s, 15 * s, 15 * s, 15 * s), Kind);
    }
}

/// <summary>Dateityp-Symbol: farbiges Blatt mit Kürzel (DOC, XLS, PDF, IMG …).</summary>
public sealed class FileKindIcon : Control
{
    public static readonly StyledProperty<FileKind> KindProperty = AvaloniaProperty.Register<FileKindIcon, FileKind>(nameof(Kind));

    static FileKindIcon() => AffectsRender<FileKindIcon>(KindProperty);

    public FileKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var (color, label) = Kind switch
        {
            FileKind.Document => ("#2F6FC4", "DOC"),
            FileKind.Spreadsheet => ("#2E8B57", "XLS"),
            FileKind.Presentation => ("#D2662E", "PPT"),
            FileKind.Pdf => ("#C9372C", "PDF"),
            FileKind.Image => ("#8E5BC2", "IMG"),
            FileKind.Audio => ("#C2558E", "AUD"),
            FileKind.Video => ("#5B6BC2", "VID"),
            FileKind.Archive => ("#8A6D3B", "ZIP"),
            FileKind.Folder => ("#E8B64C", string.Empty),
            _ => ("#8C9199", "FILE"),
        };
        var w = Bounds.Width;
        var h = Bounds.Height;
        context.DrawRectangle(new SolidColorBrush(Color.Parse(color)), null, new Rect(w * 0.12, 0, w * 0.76, h), 3, 3);
        if (label.Length > 0)
        {
            var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, h * 0.26, Brushes.White);
            context.DrawText(text, new Point((w - text.Width) / 2, h * 0.58 - (text.Height / 2)));
        }
    }
}
