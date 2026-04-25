using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Wacton.Unicolour;

namespace ClientAvalonia;

public class OklchExtension : MarkupExtension
{
    public double L { get; set; }
    public double C { get; set; }
    public double H { get; set; }
    public double A { get; set; } = 1.0;

    public OklchExtension() { }
    public OklchExtension(double l, double c, double h) { L = l; C = c; H = h; }

    public override object ProvideValue(IServiceProvider sp)
    {
        var colour = new Unicolour(ColourSpace.Oklch, L, C, H);
        var (r, g, b) = colour.Rgb;
        byte rr = (byte)Math.Clamp((int)Math.Round(r * 255), 0, 255);
        byte gg = (byte)Math.Clamp((int)Math.Round(g * 255), 0, 255);
        byte bb = (byte)Math.Clamp((int)Math.Round(b * 255), 0, 255);
        byte aa = (byte)Math.Clamp((int)Math.Round(A * 255), 0, 255);
        return new SolidColorBrush(Color.FromArgb(aa, rr, gg, bb));
    }
}
