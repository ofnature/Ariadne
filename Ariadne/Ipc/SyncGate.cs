using System;
using System.Threading.Tasks;

namespace Ariadne.Ipc;

// Bounded blocking wait for sync-shaped compat gates (decided 2026-08-25): vnavmesh serves
// Query.Mesh.* synchronously from an in-process mesh, ours cross a pipe as Task<T>, and
// consumers will not await. Blocking the caller briefly is safe here — the pipe client's
// continuations run on the thread pool (ConfigureAwait(false) throughout), never on the
// framework thread we'd be blocking — and warm queries answer in 1-3 ms; the budget only
// bites when Mnemosyne is cold or absent, where the honest answer is the fallback anyway.
// On timeout the underlying task keeps running and completes into nothing.
internal static class SyncGate
{
    public static T Wait<T>(Task<T> task, int budgetMs, T fallback)
    {
        try
        {
            return task.Wait(budgetMs) ? task.Result : fallback;
        }
        catch
        {
            return fallback; // faulted/cancelled — compat gates never throw into consumers
        }
    }
}
