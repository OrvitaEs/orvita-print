using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Orvita.Print.Gui.Models;

namespace Orvita.Print.Gui.Tray;

/// <summary>
/// Iconos de bandeja (GUI-03, Doc/08 §4.1): CINCO estados generados en código — sin
/// recursos externos (regla de Doc/08 §1.4). Círculo de color con un glifo de impresora
/// estilizado; el estado «apagado» es un círculo hueco gris tenue.
/// </summary>
public static class TrayIcons
{
    private const int Size = 32;

    /// <summary>Icono por estado (instancias nuevas: el TrayIcon puede querer una propia).</summary>
    public static WindowIcon For(TrayState state)
    {
        var target = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            switch (state)
            {
                case TrayState.Running:
                    DrawCircle(context, Color.Parse(TrayColors.Running), filled: true);
                    break;
                case TrayState.NoConnection:
                    DrawCircle(context, Color.Parse(TrayColors.NoConnection), filled: true);
                    break;
                case TrayState.Incident:
                    DrawCircle(context, Color.Parse(TrayColors.Incident), filled: true);
                    break;
                case TrayState.Unpaired:
                    DrawCircle(context, Color.Parse(TrayColors.Unpaired), filled: true);
                    break;
                case TrayState.ServiceDown:
                    DrawCircle(context, Color.Parse(TrayColors.ServiceDown), filled: false);
                    break;
            }
        }

        return new WindowIcon(target);
    }

    private static void DrawCircle(DrawingContext context, Color color, bool filled)
    {
        var brush = new SolidColorBrush(color);
        var background = new SolidColorBrush(Colors.Transparent);
        if (filled)
        {
            context.DrawEllipse(brush, null, new Rect(1, 1, Size - 2, Size - 2));
            // Glifo de impresora (blanco): cuerpo + bandeja de papel.
            context.FillRectangle(Brushes.White, new Rect(8, 12, 16, 9));
            context.FillRectangle(Brushes.White, new Rect(11, 7, 10, 5));
            context.FillRectangle(Brushes.White, new Rect(11, 20, 10, 5));
        }
        else
        {
            var pen = new Pen(brush, 3);
            context.DrawEllipse(background, pen, new Rect(2.5, 2.5, Size - 5, Size - 5));
        }
    }
}
