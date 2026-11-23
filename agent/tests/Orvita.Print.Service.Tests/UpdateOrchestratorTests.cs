using Microsoft.Extensions.Logging.Abstractions;
using Orvita.Print.Core;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Service.Tests;

/// <summary>
/// <see cref="UpdateOrchestrator"/> (F2-11): un ciclo de chequeo — sin novedad, con novedad
/// (aplica), con fallo de validación (conserva la versión, registra el código, NO aplica) y
/// con fallo inesperado. Nunca lanza hacia fuera.
/// </summary>
public sealed class UpdateOrchestratorTests
{
    private sealed class FakeChecker(Func<Version, CancellationToken, Task<PreparedUpdate?>> check) : IUpdateChecker
    {
        public int Calls { get; private set; }

        public Version? LastCurrentVersion { get; private set; }

        public Task<PreparedUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
        {
            Calls++;
            LastCurrentVersion = currentVersion;
            return check(currentVersion, cancellationToken);
        }
    }

    private sealed class FakeApplier(UpdateStatus status, Action<PreparedUpdate> onApply) : IUpdateApplier
    {
        public List<PreparedUpdate> Applied { get; } = [];

        public Task ApplyAsync(PreparedUpdate update, CancellationToken cancellationToken)
        {
            onApply(update);
            Applied.Add(update);
            // El applier REAL es quien registra el resultado (con el detalle de msiexec);
            // el fake replica la parte esencial para que el estado quede como en producción.
            status.RecordApplied(update.Manifest.Version, "applied");
            return Task.CompletedTask;
        }
    }

    private static PreparedUpdate Prepare(string version = "1.2.0") => new()
    {
        Manifest = new UpdateManifest
        {
            Version = version,
            Url = $"https://updates.orvita.example/agent/OrvitaPrint-{version}.msi",
            Sha256 = new string('a', 64),
            MinSupported = "1.0.0",
            Signature = "AAAA",
        },
        InstallerPath = Path.Combine(Path.GetTempPath(), $"orp-orch-{Guid.NewGuid():N}.msi"),
    };

    [Fact]
    public async Task Sin_novedad_registra_up_to_date_y_no_aplica()
    {
        var checker = new FakeChecker((_, _) => Task.FromResult<PreparedUpdate?>(null));
        var status = new UpdateStatus();
        var applier = new FakeApplier(status, _ => { });
        var orchestrator = new UpdateOrchestrator(checker, applier, status, NullLogger.Instance);

        Assert.False(await orchestrator.CheckAndApplyAsync(CancellationToken.None));

        Assert.Equal(1, checker.Calls);
        Assert.Equal(new Version(1, 0, 0), checker.LastCurrentVersion); // «1.0.0-dev» → «1.0.0»
        Assert.Empty(applier.Applied);
        Assert.Equal("up_to_date", status.Snapshot().Result);
    }

    [Fact]
    public async Task Con_novedad_aplica_y_devuelve_true()
    {
        var update = Prepare();
        var checker = new FakeChecker((_, _) => Task.FromResult<PreparedUpdate?>(update));
        var status = new UpdateStatus();
        var applier = new FakeApplier(status, _ => { });
        var orchestrator = new UpdateOrchestrator(checker, applier, status, NullLogger.Instance);

        Assert.True(await orchestrator.CheckAndApplyAsync(CancellationToken.None));

        var applied = Assert.Single(applier.Applied);
        Assert.Same(update, applied);
        Assert.Equal("applied", status.Snapshot().Result);
    }

    [Fact]
    public async Task UpdateException_conserva_la_version_y_no_aplica()
    {
        var checker = new FakeChecker((_, _) => throw new UpdateException("update_signature_invalid", "firma inválida"));
        var status = new UpdateStatus();
        var applier = new FakeApplier(status, _ => throw new InvalidOperationException("NO debe aplicar"));
        var orchestrator = new UpdateOrchestrator(checker, applier, status, NullLogger.Instance);

        Assert.False(await orchestrator.CheckAndApplyAsync(CancellationToken.None));

        Assert.Empty(applier.Applied);
        var (_, result, _, error) = status.Snapshot();
        Assert.Equal("failed", result);
        Assert.StartsWith("update_signature_invalid:", error);
    }

    [Fact]
    public async Task Fallo_inesperado_tambien_se_registra_y_no_lanza()
    {
        var checker = new FakeChecker((_, _) => throw new InvalidOperationException("red rota"));
        var status = new UpdateStatus();
        var applier = new FakeApplier(status, _ => throw new InvalidOperationException("NO debe aplicar"));
        var orchestrator = new UpdateOrchestrator(checker, applier, status, NullLogger.Instance);

        Assert.False(await orchestrator.CheckAndApplyAsync(CancellationToken.None));

        Assert.Empty(applier.Applied);
        Assert.StartsWith("update_internal_error:", status.Snapshot().Error);
    }
}
