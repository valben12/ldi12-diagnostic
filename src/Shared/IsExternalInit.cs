// Le compilateur C# 9 émet une référence à System.Runtime.CompilerServices.IsExternalInit
// pour chaque accesseur « init ». Le type n'existe pas dans le .NET Framework : on le fournit.
// C'est le seul shim nécessaire pour utiliser C# 9 sur net462.

namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}
