using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HololensIKEA.Services
{
    public class GltfMeshData
    {
        public Vector3[] Positions { get; set; }
        public Vector3[] Normals { get; set; }
        public ushort[] Indices { get; set; }
        public Vector3[] Colors { get; set; }
        public Vector4 BaseColor { get; set; } = new Vector4(0.72f, 0.72f, 0.72f, 1f);
        public Vector3 BoundsMeters { get; set; }
        public Vector3 CenterOffset { get; set; }
    }

    /// <summary>
    /// Resolves an IKEA product page to the GLB URL exposed by IKEA's
    /// model-viewer/gltf-model markup, following the discovery strategy of
    /// apinanaivot/IKEA-3D-Model-Download-Button, then parses the GLB.
    /// </summary>
    public sealed class ModelService3D
    {
        private static readonly Regex ModelAttribute = new Regex(
            @"(?:src|gltf-model)\s*=\s*[""']([^""']*(?:\.glb|glb_draco)[^""']*)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AbsoluteModelUrl = new Regex(
            @"https?://[^""'<>\s]+(?:\.glb|glb_draco)[^""'<>\s]*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public async Task<GltfMeshData> FetchModelAsync(string productPageUrl, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(productPageUrl)) return null;
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) })
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 HoloLensIKEA/1.0");
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
                    var pageUri = new Uri(productPageUrl, UriKind.Absolute);
                    // Resolve the deterministic Rotera static URL before fetching the
                    // HTML page. The product repository already fetched the page, and
                    // this avoids another page request failing and leaving the app in
                    // an empty 1 m placeholder state.
                    var modelUrl = await FindRoteraModelUrlAsync(pageUri, client, ct);
                    if (modelUrl != null)
                        return await DownloadAndParseGlbAsync(modelUrl.ToString(), client, ct);
                    using (var request = new HttpRequestMessage(HttpMethod.Get, pageUri))
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct))
                    {
                        response.EnsureSuccessStatusCode();
                        var html = await response.Content.ReadAsStringAsync();
                        // IKEA's current Rotera URL is deterministic for a single-part
                        // article number. Try it first so a client-side page response or
                        // an empty API payload cannot leave the app showing a blank 1 m
                        // placeholder while the model is actually available.
                        modelUrl = await FindRoteraModelUrlAsync(pageUri, client, ct);
                        if (modelUrl == null) modelUrl = FindModelUrl(html, pageUri);
                        if (modelUrl == null)
                        {
                            Debug.WriteLine("[IKEA] No GLB URL found in product page or Rotera API.");
                            return null;
                        }
                        return await DownloadAndParseGlbAsync(modelUrl.ToString(), client, ct);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine("[IKEA] Model fetch failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Downloads and parses a GLB model from a known-good direct URL,
        /// skipping the (unreliable) product-page scraping step entirely.
        /// Use this when a Bookmark already carries a resolved GlbUrl.
        /// </summary>
        public async Task<GltfMeshData> FetchModelFromGlbUrlAsync(string glbUrl, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(glbUrl)) return null;
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) })
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 HoloLensIKEA/1.0");
                    return await DownloadAndParseGlbAsync(glbUrl, client, ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine("[IKEA] Direct GLB fetch failed: " + ex.Message);
                return null;
            }
        }

        private static async Task<GltfMeshData> DownloadAndParseGlbAsync(string glbUrl, HttpClient client, CancellationToken ct)
        {
            Debug.WriteLine("[IKEA] Downloading GLB: " + glbUrl);
            using (var modelRequest = new HttpRequestMessage(HttpMethod.Get, glbUrl))
            using (var modelResponse = await client.SendAsync(modelRequest, HttpCompletionOption.ResponseContentRead, ct))
            {
                modelResponse.EnsureSuccessStatusCode();
                var bytes = await modelResponse.Content.ReadAsByteArrayAsync();
                return ParseGlb(bytes);
            }
        }

        public static Uri FindModelUrl(string html, Uri pageUri)
        {
            if (string.IsNullOrEmpty(html)) return null;
            html = html.Replace("&", "&").Replace("\\/", "/").Replace("\\u0026", "&");
            string raw = null;
            var match = ModelAttribute.Match(html);
            if (match.Success) raw = match.Groups[1].Value;
            if (string.IsNullOrEmpty(raw))
            {
                match = AbsoluteModelUrl.Match(html);
                if (match.Success) raw = match.Value;
            }
            if (string.IsNullOrEmpty(raw)) return null;
            raw = raw.Replace("\"\"", "\"").Replace("&#x2F;", "/");
            if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute) &&
                (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
                return absolute;
            if (Uri.TryCreate(pageUri, raw, out var relative)) return relative;
            return null;
        }

        private static async Task<Uri> FindRoteraModelUrlAsync(Uri pageUri, HttpClient client, CancellationToken ct)
        {
            var match = Regex.Match(pageUri.AbsolutePath, @"^/([^/]+)/([^/]+)/p/[^/]*-(\d{8})/?$", RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            var articleNumber = match.Groups[3].Value;
            var staticUrl = "https://web-api.ikea.com/" + match.Groups[1].Value + "/" +
                match.Groups[2].Value + "/rotera/static/models/" + articleNumber + "-mini.glb";
            using (var staticRequest = new HttpRequestMessage(HttpMethod.Head, staticUrl))
            {
                using (var staticResponse = await client.SendAsync(staticRequest, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (staticResponse.IsSuccessStatusCode) return new Uri(staticUrl);
                }
            }
            var endpoint = string.Format("https://web-api.ikea.com/{0}/{1}/rotera/data/model/{2}/",
                match.Groups[1].Value, match.Groups[2].Value, articleNumber);
            using (var request = new HttpRequestMessage(HttpMethod.Get, endpoint))
            {
                request.Headers.TryAddWithoutValidation("Accept", "application/json;version=2");
                request.Headers.TryAddWithoutValidation("x-client-id", "4863e7d2-1428-4324-890b-ae5dede24fc6");
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct))
                {
                    if (!response.IsSuccessStatusCode) return null;
                    var modelUrl = JObject.Parse(await response.Content.ReadAsStringAsync())["modelUrl"]?.ToString();
                    return Uri.TryCreate(modelUrl, UriKind.Absolute, out var uri) &&
                           Regex.IsMatch(uri.AbsoluteUri, @"\.glb(?:[?#]|$)", RegexOptions.IgnoreCase) ? uri : null;
                }
            }
        }

        public static GltfMeshData ParseGlb(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 20) return null;
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadUInt32() != 0x46546C67 || reader.ReadUInt32() != 2) return null;
                reader.ReadUInt32();
                JObject gltf = null;
                byte[] bin = null;
                while (stream.Position + 8 <= stream.Length)
                {
                    var length = reader.ReadUInt32();
                    var type = reader.ReadUInt32();
                    if (length > stream.Length - stream.Position) return null;
                    var chunk = reader.ReadBytes((int)length);
                    if (type == 0x4E4F534A) gltf = JObject.Parse(Encoding.UTF8.GetString(chunk).TrimEnd('\0', ' ', '\n', '\r'));
                    else if (type == 0x004E4942) bin = chunk;
                }
                return gltf == null || bin == null ? null : ParseGltf(gltf, bin);
            }
        }

        private static GltfMeshData ParseGltf(JObject root, byte[] bin)
        {
            var accessors = root["accessors"] as JArray;
            var views = root["bufferViews"] as JArray;
            var meshes = root["meshes"] as JArray;
            if (accessors == null || views == null || meshes == null) return null;
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var colors = new List<Vector3>();
            var indices = new List<ushort>();
            foreach (var mesh in meshes)
            {
                foreach (var primitive in ((IEnumerable)(mesh["primitives"] as JArray ?? new JArray())).Cast<JObject>())
                {
                    var draco = primitive["extensions"]?["KHR_draco_mesh_compression"] as JObject;
                    if (draco != null)
                    {
                        var dracoViewIndex = draco["bufferView"]?.Value<int>() ?? -1;
                        if (dracoViewIndex < 0 || dracoViewIndex >= views.Count)
                        {
                            Debug.WriteLine("[IKEA] Draco primitive has no valid bufferView.");
                            return null;
                        }
                        var dracoView = views[dracoViewIndex];
                        var dracoStart = dracoView["byteOffset"]?.Value<int>() ?? 0;
                        var dracoLength = dracoView["byteLength"]?.Value<int>() ?? 0;
                        if (dracoStart < 0 || dracoLength <= 0 || dracoStart + dracoLength > bin.Length)
                        {
                            Debug.WriteLine("[IKEA] Draco bufferView is outside the GLB binary chunk.");
                            return null;
                        }
                        var compressed = new byte[dracoLength];
                        Buffer.BlockCopy(bin, dracoStart, compressed, 0, dracoLength);
                        if (!DracoDecoder.TryDecode(compressed, out var decodedPositions,
                            out var decodedNormals, out var decodedIndices))
                        {
                            Debug.WriteLine("[IKEA] Draco mesh decoding failed.");
                            return null;
                        }
                        AppendPrimitive(decodedPositions, decodedNormals, decodedIndices,
                            positions, normals, colors, indices, MaterialColor(root, primitive));
                        continue;
                    }
                    var attrs = primitive["attributes"];
                    var posIndex = attrs?["POSITION"]?.Value<int>();
                    if (!posIndex.HasValue) continue;
                    var localPositions = ReadVec3(posIndex.Value, accessors, views, bin);
                    if (localPositions == null || localPositions.Length == 0) continue;
                    var normIndex = attrs?["NORMAL"]?.Value<int>();
                    var localNormals = normIndex.HasValue ? ReadVec3(normIndex.Value, accessors, views, bin) : null;
                    if (localNormals == null || localNormals.Length != localPositions.Length)
                    {
                        localNormals = new Vector3[localPositions.Length];
                        for (var i = 0; i < localNormals.Length; i++) localNormals[i] = Vector3.UnitY;
                    }
                    var indexIndex = primitive["indices"]?.Value<int>();
                    var localIndices = indexIndex.HasValue ? ReadIndices(indexIndex.Value, accessors, views, bin) : null;
                    if (localIndices == null)
                    {
                        localIndices = new uint[localPositions.Length];
                        for (var i = 0; i < localIndices.Length; i++) localIndices[i] = (uint)i;
                    }
                    AppendPrimitive(localPositions, localNormals, localIndices,
                        positions, normals, colors, indices, MaterialColor(root, primitive));
                }
            }
            if (positions.Count == 0 || indices.Count < 3) return null;
            var maxIndex = 0;
            for (var i = 0; i < indices.Count; i++)
            {
                if (indices[i] > maxIndex) maxIndex = indices[i];
            }
            if (maxIndex >= positions.Count)
            {
                Debug.WriteLine("[IKEA] Invalid mesh indices: max=" + maxIndex + " vertices=" + positions.Count);
                return null;
            }
            var min = positions[0];
            var max = positions[0];
            foreach (var p in positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            var center = (min + max) * 0.5f;
            var range = max - min;
            var maxRange = Math.Max(range.X, Math.Max(range.Y, range.Z));
            var scale = maxRange > 20f ? 0.001f : 1f;
            for (var i = 0; i < positions.Count; i++) positions[i] = (positions[i] - center) * scale;
            Debug.WriteLine("[IKEA] Parsed mesh: vertices=" + positions.Count +
                " indices=" + indices.Count + " triangles=" + (indices.Count / 3) +
                " materials=" + colors.Distinct().Count() + " maxIndex=" + maxIndex);
            return new GltfMeshData { Positions = positions.ToArray(), Normals = normals.ToArray(), Colors = colors.ToArray(), Indices = indices.ToArray(), BoundsMeters = range * scale, CenterOffset = center * scale };
        }

        private static Vector3 MaterialColor(JObject root, JToken primitive)
        {
            var materialIndex = primitive["material"]?.Value<int>() ?? -1;
            var materials = root["materials"] as JArray;
            var color = materials != null && materialIndex >= 0 && materialIndex < materials.Count
                ? materials[materialIndex]["pbrMetallicRoughness"]?["baseColorFactor"] as JArray
                : null;
            if (color == null || color.Count < 3) return new Vector3(0.72f, 0.72f, 0.72f);
            return new Vector3(color[0].Value<float>(), color[1].Value<float>(), color[2].Value<float>());
        }

        private static void AppendPrimitive(
            Vector3[] localPositions, Vector3[] localNormals, uint[] localIndices,
            List<Vector3> positions, List<Vector3> normals, List<Vector3> colors,
            List<ushort> indices, Vector3 color)
        {
            var baseVertex = positions.Count;
            positions.AddRange(localPositions);
            normals.AddRange(localNormals);
            for (var i = 0; i < localPositions.Length; i++) colors.Add(color);
            foreach (var index in localIndices)
            {
                var absolute = (ulong)baseVertex + index;
                if (absolute > ushort.MaxValue)
                    throw new InvalidOperationException("GLB mesh exceeds the renderer's 16-bit index limit.");
                indices.Add((ushort)absolute);
            }
        }

        private static Vector3[] ReadVec3(int accessorIndex, JArray accessors, JArray views, byte[] bin)
        {
            var accessor = accessors[accessorIndex];
            var count = accessor["count"]?.Value<int>() ?? 0;
            var viewIndex = accessor["bufferView"]?.Value<int>() ?? -1;
            if (viewIndex < 0 || viewIndex >= views.Count) return null;
            var view = views[viewIndex];
            var start = (view["byteOffset"]?.Value<int>() ?? 0) + (accessor["byteOffset"]?.Value<int>() ?? 0);
            var stride = view["byteStride"]?.Value<int>() ?? 12;
            var result = new Vector3[count];
            for (var i = 0; i < count; i++)
            {
                var offset = start + i * stride;
                if (offset + 12 > bin.Length) return null;
                result[i] = new Vector3(BitConverter.ToSingle(bin, offset), BitConverter.ToSingle(bin, offset + 4), BitConverter.ToSingle(bin, offset + 8));
            }
            return result;
        }

        private static uint[] ReadIndices(int accessorIndex, JArray accessors, JArray views, byte[] bin)
        {
            var accessor = accessors[accessorIndex];
            var count = accessor["count"]?.Value<int>() ?? 0;
            var viewIndex = accessor["bufferView"]?.Value<int>() ?? -1;
            if (viewIndex < 0 || viewIndex >= views.Count) return null;
            var view = views[viewIndex];
            var componentType = accessor["componentType"]?.Value<int>() ?? 5123;
            var start = (view["byteOffset"]?.Value<int>() ?? 0) + (accessor["byteOffset"]?.Value<int>() ?? 0);
            var elementSize = componentType == 5125 ? 4 : componentType == 5121 ? 1 : 2;
            var stride = view["byteStride"]?.Value<int>() ?? elementSize;
            var result = new uint[count];
            for (var i = 0; i < count; i++)
            {
                var offset = start + i * stride;
                if (offset + elementSize > bin.Length) return null;
                result[i] = componentType == 5125 ? BitConverter.ToUInt32(bin, offset) : componentType == 5121 ? bin[offset] : BitConverter.ToUInt16(bin, offset);
            }
            return result;
        }
    }
}
