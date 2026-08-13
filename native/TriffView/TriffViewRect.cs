using System.Drawing;

namespace TriffView;

internal static class TriffViewPreviewDimensions
{
    public const int Minimum = 16;
    public const int Maximum = 32767;
}

internal sealed class TriffViewRect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public bool IsUsable => Width >= TriffViewPreviewDimensions.Minimum
        && Height >= TriffViewPreviewDimensions.Minimum;

    public Rectangle ToRectangle() => new(X, Y, Width, Height);

    public static TriffViewRect FromRectangle(Rectangle rect)
    {
        return new TriffViewRect
        {
            X = rect.X,
            Y = rect.Y,
            Width = rect.Width,
            Height = rect.Height,
        };
    }
}
