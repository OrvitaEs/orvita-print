using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orvita.Print.Core;
using Orvita.Print.Gui.Management;
using Orvita.Print.Gui.Models;

namespace Orvita.Print.Gui.ViewModels;

/// <summary>Opción del combo de filtro: valor del protocolo + etiqueta en español.</summary>
public sealed record StateFilterOption(string Value, string Display);

/// <summary>
/// Pestaña «Cola» (GUI-04, Doc/08 §4.2): trabajos del outbox local con los 5 estados en
/// español, filtro por estado, búsqueda por nº de pedido y auto-refresco cada 3 s SOLO con
/// la ventana abierta (Doc/08 §8: sin ventana, consumo ~0 — el temporizador vive en la vista
/// y además este VM se niega a refrescar si <see cref="WindowVisible"/> es false).
///
/// Aviso de errores no vistos: banner persistente hasta que el usuario abra el trabajo con
/// error (o pulse «marcar vistos») — Doc/08 §4.2.
/// </summary>
public partial class QueueViewModel : ObservableObject
{
    /// <summary>Intervalo de refresco de la cola con la ventana abierta (Doc/08 §2: 3 s).</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);

    /// <summary>Límite del listado por llamada (Doc/08 §3: por defecto 50).</summary>
    public const int DefaultLimit = 50;

    private readonly IManagementClient _client;
    private readonly List<QueueJobRow> _rows = [];
    private readonly HashSet<long> _seenIncidentJobs = [];

    [ObservableProperty]
    private bool _windowVisible;

    [ObservableProperty]
    private string _stateFilter = QueueFilter.AllStates;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private QueueJobRow? _selectedJob;

    [ObservableProperty]
    private string _bannerText = string.Empty;

    [ObservableProperty]
    private bool _bannerVisible;

    [ObservableProperty]
    private string _detailTitle = string.Empty;

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private string _lastRefreshText = string.Empty;

    [ObservableProperty]
    private string? _loadError;

    /// <summary>Todas las filas del último refresco (sin filtrar). La vista enlaza <see cref="FilteredRows"/>.</summary>
    public ObservableCollection<QueueJobRow> FilteredRows { get; } = [];

    /// <summary>Opciones del filtro de estado (los 5 estados + todos + reimpresos).</summary>
    public IReadOnlyList<StateFilterOption> StateFilterOptions { get; } =
    [
        new(QueueFilter.AllStates, "Todos"),
        new("received", "Pendiente"),
        new("printing", "Imprimiéndose"),
        new("done", "Impreso"),
        new("error", "Error"),
        new(QueueFilter.DuplicateState, "Reimpreso tras corte"),
    ];

    /// <summary>Hay trabajos con error o reimpresos tras corte que el usuario no ha visto.</summary>
    public bool HasUnseenIncidents => _rows.Any(r =>
        (r.State == "error" || r.DuplicateSuspected) && !_seenIncidentJobs.Contains(r.JobId));

    public QueueViewModel(IManagementClient client)
    {
        _client = client;
    }

    partial void OnStateFilterChanged(string value) => ApplyFilters();

    partial void OnSearchTextChanged(string value) => ApplyFilters();

    /// <summary>Guard de auto-refresco: sin ventana abierta NO se pregunta por la pipe (Doc/08 §8).</summary>
    public bool ShouldAutoRefresh() => WindowVisible;

    /// <summary>Pide la cola a la pipe y recalcula filtros, banner y detalle. No lanza.</summary>
    public void Refresh()
    {
        if (!WindowVisible)
        {
            return; // sin ventana: CERO polling (criterio de aceptación de Doc/08 §8)
        }

        ManagementResponse response;
        try
        {
            response = _client.Send(ManagementProtocol.Operations.QueueList, new { limit = DefaultLimit });
        }
        catch (Exception ex) when (MainViewModel.IsPipeFault(ex))
        {
            LoadError = "El servicio Orvita Print no está en ejecución: la cola local no está disponible.";
            return;
        }

        if (!response.Ok || response.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            LoadError = response.Error is { } error ? $"La consulta de la cola falló: {error}" : "La consulta de la cola falló.";
            return;
        }

        LoadError = null;
        _rows.Clear();
        if (data.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array)
        {
            foreach (var job in jobs.EnumerateArray())
            {
                _rows.Add(QueueJobRow.FromJson(job));
            }
        }

        ApplyFilters();
        UpdateBanner();
        LastRefreshText = $"Actualizada: {DateTime.Now:HH:mm:ss}";
    }

    /// <summary>Recalcula el banner de errores no vistos (persistente hasta que el usuario los abra).</summary>
    private void UpdateBanner()
    {
        var unseen = _rows.Where(r =>
            (r.State == "error" || r.DuplicateSuspected)
            && !_seenIncidentJobs.Contains(r.JobId)).ToList();
        BannerVisible = unseen.Count > 0;
        BannerText = unseen.Count == 1
            ? $"Hay 1 trabajo con incidencia sin revisar (#{unseen[0].JobId})."
            : $"Hay {unseen.Count} trabajos con incidencia sin revisar.";
    }

    /// <summary>Reaplica el filtro de estado y la búsqueda sobre las filas del último refresco.</summary>
    public void ApplyFilters()
    {
        var filtered = QueueFilter.Apply(_rows, StateFilter, SearchText).ToList();
        FilteredRows.Clear();
        foreach (var row in filtered)
        {
            FilteredRows.Add(row);
        }
    }

    partial void OnSelectedJobChanged(QueueJobRow? value)
    {
        if (value is null)
        {
            return;
        }

        LoadDetail(value);
        if (value.State == "error" || value.DuplicateSuspected)
        {
            _seenIncidentJobs.Add(value.JobId);
            UpdateBanner();
        }
    }

    /// <summary>Detalle del trabajo: documento legible + marcas de tiempo completas (op queue.item).</summary>
    private void LoadDetail(QueueJobRow row)
    {
        ManagementResponse response;
        try
        {
            response = _client.Send(ManagementProtocol.Operations.QueueItem, new { job_id = row.JobId });
        }
        catch (Exception ex) when (MainViewModel.IsPipeFault(ex))
        {
            DetailTitle = $"Trabajo #{row.JobId}";
            DetailText = "El servicio Orvita Print no está en ejecución: no se puede leer el detalle.";
            return;
        }

        DetailTitle = $"Trabajo #{row.JobId} · {QueueStatusMapper.Label(row.State, row.DuplicateSuspected)}";
        if (!response.Ok || response.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            DetailText = response.Error is { } error ? $"No se pudo leer el detalle: {error}" : "No se pudo leer el detalle.";
            return;
        }

        var text = data.TryGetProperty("document_text", out var doc) && doc.ValueKind == JsonValueKind.String
            ? doc.GetString()
            : null;
        DetailText = DetailLines(row, text);
    }

    /// <summary>Cabecera del detalle con las marcas de tiempo completas (Doc/08 §4.2) + el documento.</summary>
    private static string DetailLines(QueueJobRow row, string? documentText)
    {
        var header = new List<string>
        {
            $"Pedido: {(row.OrderId is { } id ? $"#{id}" : "(sin nº de pedido)")}",
            $"Salida: {row.PrinterRole ?? "(sin rol)"} · copias: {row.Copies} · intentos: {row.Attempts}",
            $"Llegada:   {Format(row.CreatedAt)}",
            $"Última actividad: {Format(row.UpdatedAt)}",
            $"Impresión: {(row.PrintedAt is { } printed ? Format(printed) : "—")}",
        };
        if (row.DuplicateSuspected)
        {
            header.Add("⚠ Reimpreso tras un corte: puede haber salido duplicado (Doc/03 §6.4).");
        }

        if (!string.IsNullOrWhiteSpace(row.LastErrorDisplay))
        {
            header.Add($"Último error: {row.LastErrorDisplay}");
        }

        return string.Join(Environment.NewLine, header)
               + Environment.NewLine + Environment.NewLine
               + "— documento —" + Environment.NewLine
               + (string.IsNullOrWhiteSpace(documentText) ? "(sin documento)" : documentText);
    }

    [RelayCommand]
    private void MarkAllSeen()
    {
        foreach (var row in _rows.Where(r => r.State == "error" || r.DuplicateSuspected))
        {
            _seenIncidentJobs.Add(row.JobId);
        }

        UpdateBanner();
    }

    private static string Format(DateTimeOffset value) =>
        value == DateTimeOffset.MinValue ? "—" : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
