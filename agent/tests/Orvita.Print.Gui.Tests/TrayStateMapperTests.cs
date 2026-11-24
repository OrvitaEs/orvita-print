using Orvita.Print.Gui.Models;
using Xunit;

namespace Orvita.Print.Gui.Tests;

/// <summary>
/// Mapeo del icono de bandeja (GUI-03, Doc/08 §4.1): los cinco estados y sus tooltips
/// EXACTOS, derivados SOLO del payload de status (sin tocar la UI).
/// </summary>
public sealed class TrayStateMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private static TrayInputs Inputs(Func<TrayInputs, TrayInputs>? configure = null)
    {
        var baseInputs = new TrayInputs
        {
            ServiceReachable = true,
            Paired = true,
            PollerRunning = true,
            LastSeenByServer = Now.AddSeconds(-10),
            Now = Now,
        };
        return configure?.Invoke(baseInputs) ?? baseInputs;
    }

    [Fact]
    public void Servicio_sin_respuesta_da_estado_apagado_con_tooltip_de_doc()
    {
        var (state, tooltip) = TrayStateMapper.Map(Inputs(i => i with { ServiceReachable = false }));

        Assert.Equal(TrayState.ServiceDown, state);
        Assert.Equal("El servicio Orvita Print no está en ejecución", tooltip);
    }

    [Fact]
    public void Sin_emparejar_da_gris_y_pide_emparejamiento()
    {
        var (state, tooltip) = TrayStateMapper.Map(Inputs(i => i with { Paired = false }));

        Assert.Equal(TrayState.Unpaired, state);
        Assert.Equal("Sin emparejar: ejecuta el emparejamiento", tooltip);
    }

    [Fact]
    public void Incidencia_reciente_da_rojo_sobre_todo_lo_demas()
    {
        var (state, tooltip) = TrayStateMapper.Map(Inputs(i => i with { RecentIncident = true }));

        Assert.Equal(TrayState.Incident, state);
        Assert.Equal("Incidencia de impresión; abre para ver detalles", tooltip);
    }

    [Fact]
    public void Poller_parado_da_ambar_aun_con_latido_fresco()
    {
        var (state, _) = TrayStateMapper.Map(Inputs(i => i with { PollerRunning = false }));

        Assert.Equal(TrayState.NoConnection, state);
    }

    [Fact]
    public void Latido_caducado_da_ambar()
    {
        var (state, tooltip) = TrayStateMapper.Map(Inputs(i => i with
            { LastSeenByServer = Now.AddMinutes(-5) })); // > 3 min (Doc/03 §11)

        Assert.Equal(TrayState.NoConnection, state);
        Assert.Equal("Sin conexión con la tienda; reintentando", tooltip);
    }

    [Fact]
    public void Error_de_ultimo_latido_sin_marca_de_tiempo_da_ambar()
    {
        var (state, _) = TrayStateMapper.Map(Inputs(i => i with
        {
            LastSeenByServer = null,
            LastHeartbeatError = "no se pudo conectar",
        }));

        Assert.Equal(TrayState.NoConnection, state);
    }

    [Fact]
    public void Emparejado_poller_y_latido_frecos_da_verde()
    {
        var (state, tooltip) = TrayStateMapper.Map(Inputs(null));

        Assert.Equal(TrayState.Running, state);
        Assert.Equal("Orvita Print · imprimiendo normalmente", tooltip);
    }

    // ----------------------------------------------------- derivación de la incidencia

    [Fact]
    public void Trabajo_en_error_reciente_es_incidencia()
    {
        var snapshot = new StatusSnapshot
        {
            LastJob = new StatusLastJob { Id = 7, State = "error", UpdatedAt = Now.AddMinutes(-1) },
        };

        Assert.True(TrayInputsBuilder.HasRecentIncident(snapshot, Now, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void Trabajo_reimpreso_tras_corte_es_incidencia()
    {
        var snapshot = new StatusSnapshot
        {
            LastJob = new StatusLastJob { Id = 8, State = "received", DuplicateSuspected = true, UpdatedAt = Now.AddHours(-2) },
        };

        Assert.True(TrayInputsBuilder.HasRecentIncident(snapshot, Now, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void Un_error_de_hace_una_semana_ya_no_manda_rojo()
    {
        var snapshot = new StatusSnapshot
        {
            LastJob = new StatusLastJob { Id = 9, State = "error", UpdatedAt = Now.AddDays(-7) },
        };

        Assert.False(TrayInputsBuilder.HasRecentIncident(snapshot, Now, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void Trabajo_impreso_correctamente_no_es_incidencia()
    {
        var snapshot = new StatusSnapshot
        {
            LastJob = new StatusLastJob { Id = 10, State = "done", UpdatedAt = Now },
        };

        Assert.False(TrayInputsBuilder.HasRecentIncident(snapshot, Now, TimeSpan.FromHours(24)));
    }
}
