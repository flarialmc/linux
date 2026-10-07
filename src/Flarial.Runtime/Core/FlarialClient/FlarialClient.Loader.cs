using System.Collections.Generic;
using Flarial.Runtime.Exceptions;
using Flarial.Runtime.Game;
using Flarial.Runtime.Services;
using System.Threading.Tasks;

namespace Flarial.Runtime.Core;

partial class FlarialClient
{
    /// <summary>
    /// The launcher payload the client reads from its loader thread's description in DLL_PROCESS_ATTACH (JSON object, same bytes as
    /// upstream's FlarialClient.Loader): {"access_token":"..."}, or {"access_token":null} when nobody is signed in.
    /// </summary>
    internal static string CreatePayload(string? accessToken) => JsonService.Default.Write<Dictionary<string, string?>>(new() { ["access_token"] = accessToken });
}

partial class FlarialClient<T>
{
    /// <summary>
    /// Linux twin of upstream's FlarialClient.Loader (Flarial.Runtime.Windows/Core/FlarialClient.Loader.Windows.cs): same flow, but the
    /// game and the remote loader thread are handled by the platform backend (IGameService / IInjector).
    /// </summary>
    static class Loader
    {
        internal static bool Launch(Task<bool>? prepared)
        {
            // the game takes seconds to start and injection happens after it is ready: verify/download the DLL meanwhile
            var game = prepared is null ? null : Task.Run(Platform.Platform.Game.Launch);

            if (prepared is { } && !prepared.GetAwaiter().GetResult())
                return false;

            IReadOnlyList<string> imports;
            try { imports = new ModificationLibrary(_.FileName).AsImports(); }
            catch (LibraryLoadFailureException) { return false; }

            if ((game is null ? Platform.Platform.Game.Launch() : game.GetAwaiter().GetResult()) is not { } processId)
                return false;

            return Platform.Platform.Injector.Inject(imports, processId, CreatePayload(AccessToken));
        }
    }
}
