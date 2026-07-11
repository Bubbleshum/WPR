using System;
using System.Collections.ObjectModel;

namespace System.Windows.Navigation
{
    /// <summary>
    /// Shim for <c>System.Windows.Navigation.UriMapper</c>. Holds an ordered collection of
    /// <see cref="UriMapping"/> rules and, on <see cref="MapUri"/>, returns the result of the
    /// first rule that matches; if none match the original URI is returned unchanged.
    /// <c>UriMappings</c> is the content property so it can be populated from XAML.
    /// </summary>
    public sealed class UriMapper : UriMapperBase
    {
        public Collection<UriMapping> UriMappings { get; } = new Collection<UriMapping>();

        public override Uri MapUri(Uri uri)
        {
            if (uri == null)
                return uri!;

            foreach (UriMapping mapping in UriMappings)
            {
                Uri? mapped = mapping.MapUri(uri);
                if (mapped != null)
                    return mapped;
            }
            return uri;
        }
    }
}
