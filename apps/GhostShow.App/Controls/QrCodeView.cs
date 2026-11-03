using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using QRCoder;

namespace GhostShow.App.Controls;

/// <summary>A QR code for a text (the web remote's address, for a phone's or iPad's camera), drawn square, black on white.</summary>
public sealed class QrCodeView : Control
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<QrCodeView, string?>(nameof(Text));

    List<BitArray>? _modules;

    static QrCodeView() => AffectsRender<QrCodeView>(TextProperty);

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TextProperty) return;
        if (string.IsNullOrEmpty(Text))
        {
            _modules = null;
            return;
        }
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(Text, QRCodeGenerator.ECCLevel.M);
        _modules = data.ModuleMatrix;   // with its quiet zone (the white border scanners need)
    }

    public override void Render(DrawingContext context)
    {
        if (_modules is not { Count: > 0 } modules) return;
        double size = Math.Min(Bounds.Width, Bounds.Height);
        int count = modules.Count;
        double module = Math.Floor(size / count);   // whole pixels: crisp edges scan better
        if (module < 1) return;
        double offset = Math.Floor((size - module * count) / 2);
        context.FillRectangle(Brushes.White, new Rect(0, 0, size, size));
        for (int y = 0; y < count; y++)
            for (int x = 0; x < count; x++)
                if (modules[y][x]) context.FillRectangle(Brushes.Black, new Rect(offset + x * module, offset + y * module, module, module));
    }
}
