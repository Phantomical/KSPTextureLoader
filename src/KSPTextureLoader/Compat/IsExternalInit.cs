using System.ComponentModel;

namespace System.Runtime.CompilerServices;

/// <summary>
/// Polyfill for the marker type the C# compiler requires to emit <c>init</c>
/// accessors (and therefore <c>record</c>/<c>record struct</c> members). It ships
/// with .NET 5+ but not with .NET Framework, so we define it ourselves.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit { }
