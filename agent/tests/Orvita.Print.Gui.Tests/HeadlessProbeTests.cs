using Avalonia.Headless.XUnit;
using Xunit;

namespace Orvita.Print.Gui.Tests;

/// <summary>Prueba mínima del framework headless: si esta falla, es el cableado de la sesión.</summary>
public sealed class HeadlessProbeTests
{
    [AvaloniaFact]
    public void El_framework_headless_arranca()
    {
        Assert.NotNull(Avalonia.Application.Current);
    }
}
