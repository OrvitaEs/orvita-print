using Orvita.Print.Cli;

namespace Orvita.Print.Cli;

/// <summary>Ayuda de <c>orvitaprint</c> (F2-09): clara, en español, por comando.</summary>
public static class HelpTexts
{
    public static string General() => """
        orvitaprint — gestión del agente Orvita Print (sin UI; ADR-13)

        Uso: orvitaprint <comando> [opciones]

        Comandos:
          pair            Empareja el agente con tu tienda (URL + código del panel).
          destination     Salidas de impresión: add | list | remove
          status          Estado del agente: emparejamiento, poller, cola, último error.
          test            Imprime el ticket de prueba en una salida (--role).
          doctor          Diagnóstico: resumen y ZIP exportable (--zip, --out <ruta>).
          restart-poller  Reinicia el bucle de trabajo sin reiniciar el servicio (avanzado).
          help            Ayuda de un comando: orvitaprint help pair

        Global: --pipe <nombre> (avanzado; por defecto orvitaprint-management)

        Códigos de salida: 0 ok · 1 operación fallida · 2 uso incorrecto · 3 servicio no en ejecución.
        El token del agente JAMÁS se muestra por pantalla ni se guarda sin cifrar (S-01/S-05).

        Ejemplos:
          orvitaprint pair --server https://mi-tienda.es/wp-json/orp-print/v1 482913
          orvitaprint destination add --printer "EPSON TM-T20III" --role default --copies 2
          orvitaprint destination list
          orvitaprint status
          orvitaprint test
        """;

    public static string Pair() => """
        orvitaprint pair — emparejar el agente con la tienda (Doc/04 §5.2)

        Uso: orvitaprint pair --server <url> [--name <nombre>] [--allow-insecure-http] <código>

          --server <url>            URL del servidor. Ej.: https://mi-tienda.es/wp-json/orp-print/v1
          --name <nombre>           Nombre visible del agente (por defecto: "TPV <equipo>").
          --allow-insecure-http     SOLO desarrollo (p. ej. wp-env): permite HTTP sin TLS (S-03).
          <código>                  Código de 6 dígitos generado en el panel (10 min de validez).

        La URL queda guardada junto con el token (cifrado con DPAPI) en el almacén local:
        emparejar de nuevo con otra URL cambia de servidor sin reinstalar (Doc/03 §6.2.1).
        Si el código no vale: 400 (inválido), 410 (caducado) o 429 (bloqueado por intentos).
        """;

    public static string DestinationAdd() => """
        orvitaprint destination add — crea o reemplaza una salida (Doc/03 §6.7)

        Uso: orvitaprint destination add --printer "<nombre exacto>" [opciones]

          --printer "<nombre>"   Nombre EXACTO de la impresora en Windows (destination list lo muestra).
          --role <rol>           Rol de la salida (por defecto: default). Único: si existe, se reemplaza.
          --copies <1..99>       Copias por defecto de la salida (por defecto: 1).
          --label "<texto>"      Nombre visible («Mostrador», «Cocina»…).
          --kind <tipo>          auto (RAW con respaldo GDI), raw, gdi, tcp o pdf (por defecto: auto).
          --address <ruta|host>  TCP 9100: host[:puerto] de la impresora de red.
                                 pdf: DIRECTORIO donde escribir los PDFs.
          --enabled true|false   Salida activa (por defecto: true).

        Salida pdf (pruebas de integración y demos): imprime siempre en «Microsoft Print to PDF»
        sin diálogos; no acepta --printer. Cada trabajo genera un PDF con nombre autogenerado
        (ticket-<orden>-<trabajo>-<fecha>.pdf) en --address (por defecto
        %ProgramData%\OrvitaPrint\prints\; el directorio se crea si no existe). Las copias del
        trabajo van repetidas dentro del mismo PDF (una sola pasada).

        Avisa si la impresora no aparece instalada o si otra salida ya apunta a la misma
        impresora (para dos copias existe --copies; Doc/03 §6.7).
        """;

    public static string DestinationList() => """
        orvitaprint destination list — salidas configuradas e impresoras instaladas

        Uso: orvitaprint destination list

        Muestra las salidas del agente (role, impresora, copias, activa) y las impresoras
        instaladas en Windows con sus nombres EXACTOS (es lo que se pasa a --printer).
        """;

    public static string DestinationRemove() => """
        orvitaprint destination remove — elimina una salida por su rol

        Uso: orvitaprint destination remove --role <rol>

        Los trabajos ya en cola no se tocan; el servidor deja de crear trabajos para ese rol
        en el próximo heartbeat (el agente reporta sus salidas en cada latido, Doc/04 §5.3).
        """;

    public static string Test() => """
        orvitaprint test — ticket de prueba en una salida

        Uso: orvitaprint test [--role <rol>]

        Imprime el documento de prueba (con «Pizza Margarita · jamón, piña, ñ, €, áéíóú» y la
        leyenda «No válido como factura») por el MISMO transporte que un trabajo real.
        Sin --role se usa la salida «default».
        """;

    public static string Doctor() => """
        orvitaprint doctor — resumen de diagnóstico y ZIP exportable

        Uso: orvitaprint doctor [--zip] [--out <ruta>]

          --zip              El servicio genera el ZIP de diagnóstico en su carpeta «diagnostics»
                             (logs de los últimos 7 días + diagnostics.json) y muestra la ruta.
          --out <ruta>       Además copia el ZIP a esa ruta (implica --zip).

        Versiones, rutas (datos, BD, logs), estado del emparejamiento y del poller, cola local
        e impresoras instaladas. Nunca muestra secretos (S-05): el token no está en pantalla,
        en el log ni en el ZIP (el informe lleva la URL del servidor sin token y sin blob DPAPI).
        El ZIP contiene solo diagnósticos: order_id/job_id, nunca datos de los tickets.
        """;

    public static string RestartPoller() => """
        orvitaprint restart-poller — reinicia el bucle de trabajo (avanzado)

        Uso: orvitaprint restart-poller

        Para y arranca de nuevo el poller (fetch → claim → imprimir → ack) sin reiniciar el
        servicio. Útil tras un cambio de configuración del servidor o un re-pair manual.
        Requiere el agente emparejado.
        """;

    public static string For(string topic) => topic switch
    {
        "pair" => Pair(),
        "destination" => "orvitaprint destination add | list | remove\n\n" + DestinationAdd() + "\n" + DestinationList() + "\n" + DestinationRemove(),
        "destination add" => DestinationAdd(),
        "destination list" => DestinationList(),
        "destination remove" => DestinationRemove(),
        "status" => "Uso: orvitaprint status\n\nEstado del agente para el operador: emparejamiento (con el token OCULTO,\n"
                    + "S-05), última vez visto por el servidor, estado del poller, cola local\n"
                    + "(pendientes/impresos/errores), último trabajo y salidas. (Doc/03 §11)",
        "test" => Test(),
        "doctor" => Doctor(),
        "restart-poller" => RestartPoller(),
        "help" => General(),
        _ => General(),
    };
}
