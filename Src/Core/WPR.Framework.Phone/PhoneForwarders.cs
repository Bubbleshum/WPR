using System.Runtime.CompilerServices;

// WP7's Microsoft.Phone defined its own System.Diagnostics.Stopwatch (Silverlight's mscorlib had
// none). Games compiled against it reference [Microsoft.Phone]Stopwatch; .NET's is the same API.
[assembly: TypeForwardedTo(typeof(System.Diagnostics.Stopwatch))]
