using System;
using System.Threading;

namespace Orvita.Print.Gui;

/// <summary>
/// Instancia única de la GUI (Doc/08 §2): mutex global por máquina. Sin dependencia de
/// sesión: dos usuarios de la misma máquina comparten el testigo (la GUI es una por equipo).
/// Pública para que las pruebas (GUI-08) verifiquen la exclusión mutua.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Global\Orvita.Print.Gui.SingleInstance";

    /// <summary>Intenta tomar el testigo; true solo en la primera instancia. El guard hay que liberarlo.</summary>
    public static bool TryAcquire(out IDisposable guard)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            guard = new MutexGuard(mutex);
            return true;
        }

        // No es nuestra: la otra instancia la posee. La soltamos sin reclamar propiedad.
        mutex.Dispose();
        guard = NullGuard.Instance;
        return false;
    }

    private sealed class MutexGuard(Mutex mutex) : IDisposable
    {
        private readonly Mutex _mutex = mutex;

        public void Dispose()
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            finally
            {
                _mutex.Dispose();
            }
        }
    }

    private sealed class NullGuard : IDisposable
    {
        public static readonly NullGuard Instance = new();
        public void Dispose()
        {
        }
    }
}
