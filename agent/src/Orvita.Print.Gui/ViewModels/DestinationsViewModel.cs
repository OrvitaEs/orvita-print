using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orvita.Print.Core;
using Orvita.Print.Gui.Management;
using Orvita.Print.Gui.Models;

namespace Orvita.Print.Gui.ViewModels;

/// <summary>Opción del combo de tipo: valor del protocolo + etiqueta en español.</summary>
public sealed record KindOption(string Value, string Display);

/// <summary>Fila de salida (destination.list): mismos campos que ve el CLI (Doc/03 §6.7).</summary>
public sealed class DestinationRow
{
    public string Role { get; init; } = string.Empty;

    public string? Label { get; init; }

    public string? PrinterName { get; init; }

    public int Copies { get; init; }

    public bool Enabled { get; init; }

    /// <summary>Kind en crudo (auto/raw/gdi/tcp/pdf).</summary>
    public string Kind { get; init; } = "auto";

    public string? Address { get; init; }

    /// <summary>El destino pdf es solo para demos: se marca como tal (Doc/08 §4.3).</summary>
    public bool IsDemo => Kind == "pdf";

    public string KindDisplay => IsDemo ? "pdf (solo demos)" : Kind;

    /// <summary>Descripción compuesta de la fila (etiqueta · tipo; el tipo es obligatorio).</summary>
    public string Description => string.Join(" · ",
        new[] { Label, KindDisplay }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public string PrinterDisplay => IsDemo
        ? $"{PdfKindPrinterName} → {Address ?? "(carpeta por defecto)"}"
        : PrinterName ?? "(sin impresora)";

    public const string PdfKindPrinterName = "Microsoft Print to PDF";

    public static DestinationRow FromJson(JsonElement d) => new()
    {
        Role = StatusSnapshot.GetString(d, "role") ?? string.Empty,
        Label = StatusSnapshot.GetString(d, "label"),
        PrinterName = StatusSnapshot.GetString(d, "printer_name"),
        Copies = StatusSnapshot.GetInt32(d, "copies"),
        Enabled = d.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True,
        Kind = StatusSnapshot.GetString(d, "kind") ?? "auto",
        Address = StatusSnapshot.GetString(d, "address"),
    };
}

/// <summary>Impresora instalada para el selector (viene en destination.list, Doc/03 §6.6).</summary>
public sealed class PrinterOption
{
    public required string Name { get; init; }

    public bool IsDefault { get; init; }

    public string? Hint { get; init; }

    public string Display => IsDefault ? $"{Name} (predeterminada)" : Name;

    public static PrinterOption FromJson(JsonElement p) => new()
    {
        Name = StatusSnapshot.GetString(p, "name") ?? string.Empty,
        IsDefault = p.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.True,
        Hint = StatusSnapshot.GetString(p, "hint"),
    };
}

/// <summary>
/// Pestaña «Salidas» (GUI-05, Doc/08 §4.3): CRUD con el MISMO contrato que el CLI —
/// <c>destination.add/list/remove</c> por la pipe. El selector de impresoras se alimenta de
/// la MISMA respuesta de <c>destination.list</c>, que ya trae las impresoras instaladas
/// (decisión documentada: NO hace falta una op «printers» — Doc/08 §3 no la prevé y así el
/// añadido de protocolo queda exactamente en queue.list/queue.item). El aviso de impresora
/// duplicada lo calcula el servicio en destination.add y además se muestra en la lista.
/// </summary>
public partial class DestinationsViewModel : ObservableObject
{
    private readonly IManagementClient _client;

    public ObservableCollection<DestinationRow> Destinations { get; } = [];

    public ObservableCollection<PrinterOption> Printers { get; } = [];

    // ---------------------------------------------------- formulario (añadir/editar)
    [ObservableProperty]
    private string _formRole = "default";

    [ObservableProperty]
    private string _formLabel = string.Empty;

    [ObservableProperty]
    private string _formKind = "auto";

    [ObservableProperty]
    private PrinterOption? _formPrinter;

    [ObservableProperty]
    private int _formCopies = 1;

    [ObservableProperty]
    private bool _formEnabled = true;

    [ObservableProperty]
    private string _formAddress = string.Empty;

    [ObservableProperty]
    private string _warningsText = string.Empty;

    [ObservableProperty]
    private string? _duplicateNote;

    [ObservableProperty]
    private string? _loadError;

    /// <summary>Opciones del tipo de salida (mismo vocabulario que el CLI: auto/raw/gdi/tcp/pdf).</summary>
    public IReadOnlyList<KindOption> KindOptions { get; } =
    [
        new("auto", "Automático (RAW si puede, GDI si no)"),
        new("raw", "Spooler RAW (ESC/POS directo)"),
        new("gdi", "Spooler GDI (driver gráfico)"),
        new("tcp", "Red — puerto 9100"),
        new("pdf", "PDF — solo demos"),
    ];

    /// <summary>Confirmación de borrado inyectada por la vista (diálogo); null → no se borra.</summary>
    public Func<string, Task<bool>>? ConfirmRemove { get; set; }

    public bool IsPdfKind => FormKind == "pdf";

    /// <summary>En tcp/pdf la impresora no se elige (tcp va por address; pdf la fija el servicio).</summary>
    public bool PrinterSelectionEnabled => FormKind is "auto" or "raw" or "gdi";

    public DestinationsViewModel(IManagementClient client)
    {
        _client = client;
    }

    partial void OnFormKindChanged(string value)
    {
        OnPropertyChanged(nameof(IsPdfKind));
        OnPropertyChanged(nameof(PrinterSelectionEnabled));
        if (value == "pdf")
        {
            FormAddress = string.Empty; // vacío → la carpeta por defecto del transporte
        }
    }

    /// <summary>Carga salidas + impresoras instaladas (destinado a la pestaña y su selector).</summary>
    public void Load()
    {
        ManagementResponse response;
        try
        {
            response = _client.Send(ManagementProtocol.Operations.DestinationList);
        }
        catch (Exception ex) when (MainViewModel.IsPipeFault(ex))
        {
            LoadError = "El servicio Orvita Print no está en ejecución: no se pueden leer las salidas.";
            return;
        }

        if (!response.Ok || response.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            LoadError = response.Error is { } error ? $"No se pudieron leer las salidas: {error}" : "No se pudieron leer las salidas.";
            return;
        }

        LoadError = null;
        Destinations.Clear();
        if (data.TryGetProperty("destinations", out var destinations) && destinations.ValueKind == JsonValueKind.Array)
        {
            foreach (var destination in destinations.EnumerateArray())
            {
                Destinations.Add(DestinationRow.FromJson(destination));
            }
        }

        Printers.Clear();
        if (data.TryGetProperty("printers", out var printers) && printers.ValueKind == JsonValueKind.Array)
        {
            foreach (var printer in printers.EnumerateArray())
            {
                Printers.Add(PrinterOption.FromJson(printer));
            }
        }

        DuplicateNote = DuplicatePrinterNote(Destinations);
    }

    /// <summary>Guarda la salida del formulario (destination.add = upsert por role). No lanza.</summary>
    public bool Save()
    {
        var args = new Dictionary<string, object?>
        {
            ["role"] = FormRole.Trim(),
            ["label"] = string.IsNullOrWhiteSpace(FormLabel) ? null : FormLabel.Trim(),
            ["kind"] = FormKind,
            ["copies"] = Math.Clamp(FormCopies, 1, 99),
            ["enabled"] = FormEnabled,
        };
        if (FormKind == "pdf")
        {
            // En pdf la impresora la fija el servicio (Microsoft Print to PDF); el directorio va en address.
            args["address"] = string.IsNullOrWhiteSpace(FormAddress) ? null : FormAddress.Trim();
        }
        else if (FormKind == "tcp")
        {
            args["address"] = FormAddress.Trim();
            args["printer"] = null;
        }
        else
        {
            if (FormPrinter is null)
            {
                WarningsText = "Elige una impresora de la lista (o usa el tipo tcp/pdf).";
                return false;
            }

            args["printer"] = FormPrinter.Name;
            args["address"] = null;
        }

        ManagementResponse response;
        try
        {
            response = _client.Send(ManagementProtocol.Operations.DestinationAdd, args);
        }
        catch (Exception ex) when (MainViewModel.IsPipeFault(ex))
        {
            WarningsText = "El servicio Orvita Print no está en ejecución: no se puede guardar la salida.";
            return false;
        }

        if (!response.Ok)
        {
            WarningsText = response.Error ?? "No se pudo guardar la salida.";
            return false;
        }

        var warnings = response.Data is { ValueKind: JsonValueKind.Object } data
            && data.TryGetProperty("warnings", out var w)
            && w.ValueKind == JsonValueKind.Array
            ? w.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
            : [];
        WarningsText = warnings.Count > 0
            ? string.Join(" ", warnings)
            : string.Empty; // guardado sin avisos

        Load();
        return true;
    }

    [RelayCommand]
    public async Task RemoveAsync(DestinationRow? row)
    {
        if (row is null || ConfirmRemove is null)
        {
            return;
        }

        if (!await ConfirmRemove(
            $"¿Quitar la salida «{row.Role}»? El servidor la dará de baja en el próximo latido.").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            _client.Send(ManagementProtocol.Operations.DestinationRemove, new { role = row.Role });
        }
        catch (Exception ex) when (MainViewModel.IsPipeFault(ex))
        {
            return; // sin servicio no hay nada que quitar por ahora
        }

        Load();
    }

    [RelayCommand]
    public void EditRow(DestinationRow? row)
    {
        if (row is null)
        {
            return;
        }

        FormRole = row.Role;
        FormLabel = row.Label ?? string.Empty;
        FormKind = row.Kind;
        FormPrinter = Printers.FirstOrDefault(p => p.Name == row.PrinterName);
        FormCopies = row.Copies;
        FormEnabled = row.Enabled;
        FormAddress = row.Address ?? string.Empty;
    }

    [RelayCommand]
    public void ResetForm()
    {
        FormRole = "default";
        FormLabel = string.Empty;
        FormKind = "auto";
        FormPrinter = Printers.FirstOrDefault(p => p.IsDefault) ?? Printers.FirstOrDefault();
        FormCopies = 1;
        FormEnabled = true;
        FormAddress = string.Empty;
        WarningsText = string.Empty;
    }

    /// <summary>Imprimir prueba por esta salida (misma op «test» que el CLI con --role).</summary>
    public bool Test(string role)
    {
        try
        {
            var response = _client.Send(ManagementProtocol.Operations.Test, new { role });
            if (response.Ok)
            {
                WarningsText = string.Empty;
                return true;
            }

            WarningsText = response.Error ?? "La prueba falló.";
            return false;
        }
        catch (Exception ex) when (MainViewModel.IsPipeFault(ex))
        {
            WarningsText = "El servicio Orvita Print no está en ejecución.";
            return false;
        }
    }

    /// <summary>Aviso en la lista cuando DOS salidas apuntan a la misma impresora (Doc/08 §4.3).</summary>
    public static string? DuplicatePrinterNote(IReadOnlyList<DestinationRow> destinations)
    {
        var duplicated = destinations
            .Where(d => !d.IsDemo && !string.IsNullOrWhiteSpace(d.PrinterName))
            .GroupBy(d => d.PrinterName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        return duplicated is { } group
            ? $"Ojo: las salidas {string.Join(" y ", group.Select(d => $"«{d.Role}»"))} apuntan a la misma impresora «{group.Key}». Para dos copias usa copias por salida, no dos salidas."
            : null;
    }
}
