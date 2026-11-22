using Orvita.Print.Contracts;
using Xunit;

namespace Orvita.Print.Core.Tests;

/// <summary>Tabla de verdad de los códigos de error (Doc/04 §6) y de las dos formas de cuerpo.</summary>
public sealed class ErrorCodeTests
{
    [Theory]
    [InlineData("orp_invalid_token", 401, AgentAction.Stop)]
    [InlineData("orp_token_revoked", 401, AgentAction.Stop)]
    [InlineData("orp_not_found", 404, AgentAction.Discard)]
    [InlineData("orp_already_claimed", 409, AgentAction.Continue)]
    [InlineData("orp_job_expired", 410, AgentAction.Discard)]
    [InlineData("orp_rate_limited", 429, AgentAction.Backoff)]
    [InlineData("orp_invalid_payload", 400, AgentAction.MarkFailed)]
    [InlineData("orp_pairing_invalid", 400, AgentAction.Retry)]
    [InlineData("orp_pairing_expired", 410, AgentAction.Retry)]
    [InlineData("orp_pairing_locked", 429, AgentAction.Backoff)]
    public void Cada_codigo_de_04_6_mapea_su_HTTP_y_su_accion(string wire, int http, AgentAction action)
    {
        var code = ErrorCodes.TryFromWire(wire);
        Assert.NotNull(code);
        Assert.Equal(http, code!.Value.HttpStatus());
        Assert.Equal(action, code.Value.Action());
    }

    [Theory]
    [InlineData("orp_printer_not_found", AgentAction.MarkFailed)]
    [InlineData("orp_unsupported_document", AgentAction.MarkFailed)]
    public void Los_codigos_de_ack_del_agente_marcan_failed(string wire, AgentAction action)
    {
        var code = ErrorCodes.TryFromWire(wire);
        Assert.NotNull(code);
        Assert.Equal(0, code!.Value.HttpStatus());
        Assert.Equal(action, code.Value.Action());
    }

    [Theory]
    [InlineData("orp_alien_code")]
    [InlineData("")]
    [InlineData(null)]
    public void Codigo_desconocido_o_vacio_no_resuelve(string? wire)
    {
        Assert.Null(ErrorCodes.TryFromWire(wire));
    }

    [Fact]
    public void Cuerpo_literal_409_se_parsea_sin_envoltorio()
    {
        var env = ErrorEnvelope.Parse("""{"error":"orp_already_claimed","status":"claimed"}""");
        Assert.NotNull(env);
        Assert.Equal("orp_already_claimed", env!.PrimaryCode);
        Assert.Equal("claimed", env.Status);
        Assert.Equal(ErrorCode.OrpAlreadyClaimed, ErrorCodes.TryFromWire(env.PrimaryCode));
    }

    [Fact]
    public void Cuerpo_literal_410_se_parsea_sin_envoltorio()
    {
        var env = ErrorEnvelope.Parse("""{"error":"orp_job_expired"}""");
        Assert.NotNull(env);
        Assert.Equal(ErrorCode.OrpJobExpired, ErrorCodes.TryFromWire(env!.PrimaryCode));
    }

    [Fact]
    public void Envoltorio_estandar_de_WordPress_401_se_parsea()
    {
        const string json = """
            {"code":"orp_invalid_token","message":"Token desconocido","data":{"status":401}}
            """;
        var env = ErrorEnvelope.Parse(json);
        Assert.NotNull(env);
        Assert.Equal("orp_invalid_token", env!.PrimaryCode);
        Assert.Equal("Token desconocido", env.Message);
        Assert.Equal(401, env.Data!.Status);
        Assert.Equal(ErrorCode.OrpInvalidToken, ErrorCodes.TryFromWire(env.PrimaryCode));
    }

    [Fact]
    public void Envoltorio_estandar_de_WordPress_429_se_parsea()
    {
        const string json = """
            {"code":"orp_rate_limited","message":"Demasiadas peticiones","data":{"status":429}}
            """;
        var env = ErrorEnvelope.Parse(json);
        Assert.Equal(ErrorCode.OrpRateLimited, ErrorCodes.TryFromWire(env!.PrimaryCode));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html>gateway timeout</html>")]
    [InlineData(null)]
    public void Cuerpo_basura_devuelve_null_sin_excepcion(string? body)
    {
        Assert.Null(ErrorEnvelope.Parse(body));
    }
}
