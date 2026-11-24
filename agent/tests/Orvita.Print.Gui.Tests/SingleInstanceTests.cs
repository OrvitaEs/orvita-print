using Orvita.Print.Gui;
using Orvita.Print.Gui.Management;
using Orvita.Print.Gui.Models;
using Xunit;

namespace Orvita.Print.Gui.Tests;

/// <summary>Instancia única (GUI-01, Doc/08 §2): mutex global — la segunda no arranca.</summary>
public sealed class SingleInstanceTests
{
    [Fact]
    public void El_segundo_intento_de_instancia_no_toma_el_testigo()
    {
        // OJO: si otros tests sueltan el mutex al liberarse, esto es independiente por nombre.
        Assert.True(SingleInstance.TryAcquire(out var first));
        try
        {
            Assert.False(SingleInstance.TryAcquire(out var second));
        }
        finally
        {
            first.Dispose();
        }

        // Liberado el testigo, una nueva instancia SÍ puede arrancar.
        Assert.True(SingleInstance.TryAcquire(out var third));
        third.Dispose();
    }
}
