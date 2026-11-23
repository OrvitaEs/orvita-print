using Orvita.Print.Cli;

// orvitaprint — cliente de la pipe de gestión del servicio Orvita Print (F2-09, ADR-13).
// Un comando, una conexión, una respuesta. El CLI NUNCA toca el almacén local: todo pasa por
// la pipe (un solo escritor: el servicio) y el token JAMÁS se muestra por pantalla (S-05).
return await CliApp.RunAsync(args, Console.Out, Console.Error).ConfigureAwait(false);
