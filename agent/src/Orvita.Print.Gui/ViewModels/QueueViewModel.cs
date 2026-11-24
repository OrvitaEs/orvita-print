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

    /// <summary>Confirmación de «Reimprimir» sobre un trabajo ya impreso (¿seguro?): la inyecta la vista (diálogo). null → no se reimprime.</summary>
    public Func<string, Task<bool>>? ConfirmReprintDone { get; set; }

    /// <summary>Balón de bandeja para una reimpresión que vuelve a fallar (la vista/enrutador lo muestra).</summary>
    public event Action<TrayBalloon>? BalloonRequested;

    [ObservableProperty]
    private bool _windowVisible;

    /// <summary>Una reimpresión en marcha: la fila de acciones se desactiva (no imprimir dos veces).</summary>
    [ObservableProperty]
    private bool _retryInProgress;

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

    // ----------------------------------------------- señales para el dibujo (GUI-09)

    [ObservableProperty]
    private string _emptyStateText = string.Empty;

    /// <summary>La tarjeta de estado vacío solo se muestra cuando no hay filas filtradas.</summary>
    public bool ShowEmptyState => FilteredRows.Count == 0;

    /// <summary>La tarjeta de detalle se muestra solo con un trabajo leído (o intentado).</summary>
    public bool HasDetail => DetailText.Length > 0;

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

    /// <summary>Hay trabajos con error o reimpresos tras corte que el usuario no ha visto. Los errores YA revisados (marca local GUI-15) no cuentan.</summary>
    public bool HasUnseenIncidents => UnseenIncidents().Count > 0;

    private List<QueueJobRow> UnseenIncidents() =>
        _rows.Where(r =>
                (r.State == "error" || r.DuplicateSuspected)
                && r.ReviewedAt is null
                && !_seenIncidentJobs.Contains(r.JobId))
            .ToList();

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
            // La pipe no responde: la tabla se vacía y el estado vacío muestra el aviso de
            // reconexión (GUI-09). El outbox NO se toca: esto es solo la vista local.
            LoadError = "El servicio Orvita Print no está en ejecución: la cola local no está disponible.";
            _rows.Clear();
            ApplyFilters();
            return;
        }

        if (!response.Ok || response.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            LoadError = response.Error is { } error ? $"La consulta de la cola falló: {error}" : "La consulta de la cola falló.";
            _rows.Clear();
            ApplyFilters();
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

    /// <summary>Recalcula el banner de errores no vistos (persistente hasta que el usuario los abra).
    /// El contador cuenta SOLO errores NO revisados (GUI-15, decisión D).</summary>
    private void UpdateBanner()
    {
        var unseen = UnseenIncidents();
        BannerVisible = unseen.Count > 0;
        BannerText = unseen.Count == 1
            ? $"Hay 1 trabajo con incidencia sin revisar (#{unseen[0].JobId})."
            : $"Hay {unseen.Count} trabajos con incidencia sin revisar.";
    }

    /// <summary>Refresca las señales del estado vacío y su texto (GUI-09, solo dibujo).</summary>
    private void UpdateEmptyState()
    {
        EmptyStateText = LoadError
            ?? (FilteredRows.Count == 0 ? "No hay trabajos en la cola con los filtros actuales." : string.Empty);
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    partial void OnLoadErrorChanged(string? value) => UpdateEmptyState();

    partial void OnDetailTextChanged(string value) => OnPropertyChanged(nameof(HasDetail));

    /// <summary>Reaplica el filtro de estado y la búsqueda sobre las filas del último refresco.</summary>
    public void ApplyFilters()
    {
        var filtered = QueueFilter.Apply(_rows, StateFilter, SearchText).ToList();
        FilteredRows.Clear();
        foreach (var row in filtered)
        {
            FilteredRows.Add(row);
        }

        UpdateEmptyState();
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

    // ------------------------------------------------------ acciones de fila (GUI-15)

    /// <summary>Entrada de la vista (botón de fila / detalle): reimprimir con confirmación para los «done».</summary>
    public async Task RequestRetryAsync(QueueJobRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.State == "done" && ConfirmReprintDone is { } confirm)
        {
            var accepted = await confirm(
                $"El trabajo #{row.JobId} ya está impreso. ¿Seguro? Imprimirá OTRA copia del ticket.");
            if (!accepted)
            {
                return;
            }
        }

        await RetryJobCommand.ExecuteAsync(row);
    }

    [RelayCommand]
    private async Task RetryJobAsync(QueueJobRow? row)
    {
        if (row is null || RetryInProgress)
        {
            return; // sin doble clic: no imprimir dos copias por accidente
        }

        RetryInProgress = true;
        try
        {
            // La impresión tarda (segundos): fuera del hilo de UI (Doc/08 §2).
            var response = await Task.Run(() =>
                _client.Send(ManagementProtocol.Operations.QueueRetry, new { job_id = row.JobId }));

            if (response.Ok && response.Data is { ValueKind: JsonValueKind.Object } data)
            {
                ApplyQueueJobUpdate(data);
            }
            else
            {
                LoadError = response.Error is { } error
                    ? $"No se pudo reimprimir el trabajo #{row.JobId}: {error}"
                    : $"No se pudo reimprimir el trabajo #{row.JobId}.";
            }
        }
        catch (Exception ex) when (MainViewModel.IsPipeFault(ex))
        {
            LoadError = "El servicio Orvita Print no está en ejecución: no se pudo reimprimir.";
        }
        finally
        {
            RetryInProgress = false;
        }
    }

    /// <summary>Marca local «revisada» (sin confirmación: es reversible y no imprime nada).</summary>
    public async Task RequestReviewAsync(QueueJobRow? row) =>
        await ReviewJobCommand.ExecuteAsync(row);

    [RelayCommand]
    private async Task ReviewJobAsync(QueueJobRow? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            var response = await Task.Run(() =>
                _client.Send(ManagementProtocol.Operations.QueueReview, new { job_id = row.JobId, reviewed = true }));

            if (response.Ok && response.Data is { ValueKind: JsonValueKind.Object } data)
            {
                ApplyQueueJobUpdate(data);
            }
            else
            {
                LoadError = response.Error is { } error
                    ? $"No se pudo marcar el trabajo #{row.JobId} como revisado: {error}"
                    : $"No se pudo marcar el trabajo #{row.JobId} como revisado.";
            }
        }
        catch (Exception ex) when (MainViewModel.IsPipeFault(ex))
        {
            LoadError = "El servicio Orvita Print no está en ejecución: no se pudo marcar como revisada.";
        }
    }

    /// <summary>
    /// Aplica al vuelo el resultado de queue.retry/queue.review (la fila con el estado final
    /// que devuelve la op): sustituye la fila, rearma el banner si volvió a fallar y refresca
    /// el detalle si es el trabajo seleccionado.
    /// </summary>
    private void ApplyQueueJobUpdate(JsonElement jobWire)
    {
        var updated = QueueJobRow.FromJson(jobWire);
        var index = _rows.FindIndex(r => r.JobId == updated.JobId);
        if (index >= 0)
        {
            _rows[index] = updated;
        }
        else
        {
            _rows.Add(updated);
        }

        if (updated.State == "error")
        {
            // Nueva incidencia (o re-fallo): vuelve al banner Y notifica (GUI-15).
            _seenIncidentJobs.Remove(updated.JobId);
            BalloonRequested?.Invoke(new TrayBalloon(
                "Orvita Print · incidencia de impresión",
                $"Trabajo #{updated.JobId} volvió a fallar: {updated.LastErrorDisplay ?? "error de impresión"}.",
                Error: true));
        }

        ApplyFilters();
        UpdateBanner();

        // El detalle abierto queda pegado al trabajo: lo recargamos con el estado nuevo
        // SIN marcarlo como visto — un re-fallo tras reimprimir vuelve al banner (GUI-15).
        if (SelectedJob is { } selected && selected.JobId == updated.JobId)
        {
            LoadDetail(updated);
        }
    }

    private static string Format(DateTimeOffset value) =>
        value == DateTimeOffset.MinValue ? "—" : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
