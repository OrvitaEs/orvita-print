using Orvita.Print.Core;
using Xunit;

namespace Orvita.Print.Core.Tests;

/// <summary>
/// Almacén local de configuración (F2-10): emparejamiento (URL + token protegido) y salidas
/// con role único, sobre la MISMA BD que la cola (tablas distintas). Comprueba también la
/// persistencia entre instancias y que NO colisiona con OutboxRepository (misma BD, dos repos).
/// </summary>
public sealed class LocalConfigStoreTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"orp-config-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        LocalConfigStore? store = null;
        OutboxRepository? outbox = null;
        try
        {
            store = new LocalConfigStore(_databasePath);
            store.Dispose();
            outbox = new OutboxRepository(_databasePath);
            outbox.Dispose();
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // Buenas intenciones de limpieza en %TEMP%; no es parte de la aserción.
        }
    }

    private LocalConfigStore Create() => new(_databasePath);

    // ---------------------------------------------------------------- emparejamiento

    [Fact]
    public void Sin_emparejar_devuelve_pairing_vacio()
    {
        using var store = Create();
        var pairing = store.GetPairing();
        Assert.False(pairing.IsPaired);
        Assert.Null(pairing.ServerUrl);
        Assert.Null(pairing.TokenProtected);
    }

    [Fact]
    public void SetPairing_guarda_url_token_y_local_y_sobrevive_a_nueva_instancia()
    {
        using (var store = Create())
        {
            store.SetPairing(new PairingInfo
            {
                ServerUrl = "https://tienda.example/wp-json/orp-print/v1/",
                TokenProtected = "BASE64CIFRADO", // opaco para el almacén (llega cifrado, S-01)
                PairedAtUtc = new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero),
                LocationId = 2,
                LocationCode = "pueblo-b",
                LocationName = "Pizzería Pueblo B",
                AgentName = "TPV Mostrador",
            });
        }

        using var reabierto = Create();
        var pairing = reabierto.GetPairing();
        Assert.True(pairing.IsPaired);
        Assert.Equal("https://tienda.example/wp-json/orp-print/v1/", pairing.ServerUrl);
        Assert.Equal("BASE64CIFRADO", pairing.TokenProtected);
        Assert.Equal(2, pairing.LocationId);
        Assert.Equal("pueblo-b", pairing.LocationCode);
        Assert.Equal("Pizzería Pueblo B", pairing.LocationName);
        Assert.Equal("TPV Mostrador", pairing.AgentName);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero), pairing.PairedAtUtc);
    }

    [Fact]
    public void SetPairing_reemplaza_todo_junto_repair()
    {
        using var store = Create();
        store.SetPairing(new PairingInfo { ServerUrl = "https://a.example/v1/", TokenProtected = "viejo" });
        store.SetPairing(new PairingInfo { ServerUrl = "https://b.example/v1/", TokenProtected = "nuevo" });

        var pairing = store.GetPairing();
        Assert.Equal("https://b.example/v1/", pairing.ServerUrl);
        Assert.Equal("nuevo", pairing.TokenProtected);
    }

    [Fact]
    public void SetPairing_exige_url_y_token()
    {
        using var store = Create();
        Assert.Throws<ArgumentNullException>(() => store.SetPairing(null!));
        Assert.Throws<ArgumentException>(() => store.SetPairing(new PairingInfo { ServerUrl = "", TokenProtected = "x" }));
        Assert.Throws<ArgumentException>(() => store.SetPairing(new PairingInfo { ServerUrl = "https://a.example/", TokenProtected = "" }));
    }

    [Fact]
    public void ClearPairing_borra_url_y_token_pero_conserva_salidas()
    {
        using var store = Create();
        store.SetPairing(new PairingInfo { ServerUrl = "https://a.example/v1/", TokenProtected = "tok" });
        store.UpsertDestination(new DestinationOptions { Role = "default", PrinterName = "EPSON" });

        store.ClearPairing();

        Assert.False(store.GetPairing().IsPaired);
        var salida = Assert.Single(store.ListDestinations());
        Assert.Equal("default", salida.Role);
    }

    // ---------------------------------------------------------------------- destinos

    [Fact]
    public void UpsertDestination_crea_y_luego_actualiza_por_role_unico()
    {
        using var store = Create();
        var creada = store.UpsertDestination(new DestinationOptions { Role = "default", PrinterName = "EPSON TM-T20III", Copies = 1 });
        Assert.True(creada);

        var actualizada = store.UpsertDestination(new DestinationOptions { Role = "default", PrinterName = "EPSON TM-T20III", Copies = 2, Label = "Mostrador" });
        Assert.False(actualizada); // no duplica: upsert por role (Doc/04 §2.5)

        var destino = Assert.Single(store.ListDestinations());
        Assert.Equal(2, destino.Copies);
        Assert.Equal("Mostrador", destino.Label);
    }

    [Fact]
    public void ListDestinations_conserva_todos_los_campos_y_orden_por_role()
    {
        using var store = Create();
        store.UpsertDestination(new DestinationOptions { Role = "cocina", Label = "Cocina", PrinterName = "XPRINTER", Copies = 1, Enabled = false, Kind = PrintTransportKind.Tcp9100, Address = "192.168.1.50:9100" });
        store.UpsertDestination(new DestinationOptions { Role = "default", PrinterName = "EPSON TM-T20III", Copies = 3, Kind = PrintTransportKind.RawSpooler });

        var destinations = store.ListDestinations();
        Assert.Equal(["cocina", "default"], destinations.Select(d => d.Role).ToArray());

        var cocina = destinations[0];
        Assert.False(cocina.Enabled);
        Assert.Equal(PrintTransportKind.Tcp9100, cocina.Kind);
        Assert.Equal("192.168.1.50:9100", cocina.Address);
        var mostrador = destinations[1];
        Assert.Equal(3, mostrador.Copies);
        Assert.Equal(PrintTransportKind.RawSpooler, mostrador.Kind);
        Assert.True(mostrador.Enabled);
    }

    [Fact]
    public void UpsertDestination_acota_copies_y_exige_role()
    {
        using var store = Create();
        Assert.Throws<ArgumentException>(() => store.UpsertDestination(new DestinationOptions { Role = " ", PrinterName = "X" }));

        store.UpsertDestination(new DestinationOptions { Role = "default", PrinterName = "X", Copies = 500 });
        Assert.Equal(99, store.ListDestinations()[0].Copies);
    }

    [Fact]
    public void RemoveDestination_elimina_por_role_exacto()
    {
        using var store = Create();
        store.UpsertDestination(new DestinationOptions { Role = "default", PrinterName = "A" });
        store.UpsertDestination(new DestinationOptions { Role = "cocina", PrinterName = "B" });

        Assert.True(store.RemoveDestination("cocina"));
        Assert.False(store.RemoveDestination("cocina")); // idempotente: false si no existe
        Assert.Equal(["default"], store.ListDestinations().Select(d => d.Role).ToArray());
    }

    // ----------------------------------------------------------------- misma BD que la cola

    [Fact]
    public void Convive_con_OutboxRepository_en_la_misma_BD()
    {
        using var store = Create();
        using var outbox = new OutboxRepository(_databasePath);

        store.SetPairing(new PairingInfo { ServerUrl = "https://a.example/v1/", TokenProtected = "tok" });
        store.UpsertDestination(new DestinationOptions { Role = "default", PrinterName = "EPSON" });
        outbox.Enqueue(1844, "default", 1, "{}");

        var job = outbox.Get(1844);
        Assert.NotNull(job);
        Assert.Equal("default", job.PrinterRole);
        Assert.True(store.GetPairing().IsPaired);
        Assert.Single(store.ListDestinations());

        // La recuperación de la cola no toca la configuración.
        outbox.TryClaim(1844);
        outbox.RecoverInterrupted();
        Assert.True(store.GetPairing().IsPaired);
    }

    // ---------------------------------------------------------- nombres de transporte (CLI)

    [Fact]
    public void Nombres_de_transporte_redondan_con_el_enum()
    {
        Assert.Equal("auto", LocalConfigStore.ToKindName(PrintTransportKind.Auto));
        Assert.Equal("raw", LocalConfigStore.ToKindName(PrintTransportKind.RawSpooler));
        Assert.Equal("gdi", LocalConfigStore.ToKindName(PrintTransportKind.Gdi));
        Assert.Equal("tcp", LocalConfigStore.ToKindName(PrintTransportKind.Tcp9100));
        Assert.Equal("pdf", LocalConfigStore.ToKindName(PrintTransportKind.Pdf));

        Assert.Equal(PrintTransportKind.Auto, LocalConfigStore.ParseKindName("auto"));
        Assert.Equal(PrintTransportKind.RawSpooler, LocalConfigStore.ParseKindName("RAW"));
        Assert.Equal(PrintTransportKind.Gdi, LocalConfigStore.ParseKindName("Gdi"));
        Assert.Equal(PrintTransportKind.Tcp9100, LocalConfigStore.ParseKindName("tcp"));
        Assert.Equal(PrintTransportKind.Pdf, LocalConfigStore.ParseKindName("PDF"));
        Assert.Equal(PrintTransportKind.Auto, LocalConfigStore.ParseKindName(null));
        Assert.Equal(PrintTransportKind.Auto, LocalConfigStore.ParseKindName("loquesea")); // desconocido → auto
    }
}
