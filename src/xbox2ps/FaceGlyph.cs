using System.Windows;
using System.Windows.Media;
using Xbox2Ps.Core;

namespace Xbox2Ps;

/// <summary>
/// A PlayStation face-button symbol (cross, circle, square, triangle) drawn as vector lines in its
/// classic colour. Collapses itself for any other output, so it can sit in every dropdown row.
/// </summary>
public sealed class FaceGlyph : FrameworkElement
{
    public static readonly DependencyProperty OutputProperty = DependencyProperty.Register(
        nameof(Output), typeof(DsOutput), typeof(FaceGlyph),
        new FrameworkPropertyMetadata(DsOutput.None, FrameworkPropertyMetadataOptions.AffectsRender, OnOutputChanged));

    public DsOutput Output
    {
        get => (DsOutput)GetValue(OutputProperty);
        set => SetValue(OutputProperty, value);
    }

    private static readonly Pen CrossPen = Frozen(0x5B, 0x8D, 0xEF);    // blue
    private static readonly Pen CirclePen = Frozen(0xE8, 0x47, 0x5F);   // red
    private static readonly Pen SquarePen = Frozen(0xD8, 0x6B, 0xC4);   // pink
    private static readonly Pen TrianglePen = Frozen(0x1F, 0xB8, 0x9A); // green

    private static Pen Frozen(byte r, byte g, byte b)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(r, g, b)), 1.8)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }

    public static bool IsFaceButton(DsOutput o) => o is DsOutput.Cross or DsOutput.Circle or DsOutput.Square or DsOutput.Triangle;

    public FaceGlyph()
    {
        Width = Height = 13;
        Visibility = Visibility.Collapsed;
        SnapsToDevicePixels = true;
    }

    private static void OnOutputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((FaceGlyph)d).Visibility = IsFaceButton((DsOutput)e.NewValue) ? Visibility.Visible : Visibility.Collapsed;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        const double inset = 1.5;
        var box = new Rect(inset, inset, w - 2 * inset, h - 2 * inset);

        switch (Output)
        {
            case DsOutput.Cross:
                dc.DrawLine(CrossPen, box.TopLeft, box.BottomRight);
                dc.DrawLine(CrossPen, box.TopRight, box.BottomLeft);
                break;
            case DsOutput.Circle:
                dc.DrawEllipse(null, CirclePen, new Point(w / 2, h / 2), box.Width / 2, box.Height / 2);
                break;
            case DsOutput.Square:
                dc.DrawRectangle(null, SquarePen, box);
                break;
            case DsOutput.Triangle:
                var tri = new StreamGeometry();
                using (var g = tri.Open())
                {
                    g.BeginFigure(new Point(w / 2, box.Top), isFilled: false, isClosed: true);
                    g.LineTo(new Point(box.Right, box.Bottom - 0.5), isStroked: true, isSmoothJoin: true);
                    g.LineTo(new Point(box.Left, box.Bottom - 0.5), isStroked: true, isSmoothJoin: true);
                }
                tri.Freeze();
                dc.DrawGeometry(null, TrianglePen, tri);
                break;
        }
    }
}
