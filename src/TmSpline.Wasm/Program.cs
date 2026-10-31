using System.Runtime.InteropServices.JavaScript;
using TmSpline.Core.Geometry;

// Entry point is required by the WebAssembly SDK; the editor calls the exports below.
return;

namespace TmSpline.Wasm
{
    public static partial class Interop
    {
        /// <summary>Builds the preview mesh for a track JSON document. See <see cref="PreviewMesh"/> for the layout.</summary>
        /// <param name="quality">0 = coarse (while dragging), 1 = the export mesh at Low quality (finer qualities only add strips and columns).</param>
        [JSExport]
        public static byte[] BuildPreview(string trackJson, int quality) => PreviewMesh.Build(trackJson, quality);
    }
}
