using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace HololensIKEA.Services
{
    /// <summary>
    /// Minimal binding to the MIT-licensed Evergine Draco tiny decoder. The
    /// native x86 DLL is packaged with the HoloLens app; IKEA model bytes are
    /// still downloaded at runtime and are never bundled with the app.
    /// </summary>
    internal static class DracoDecoder
    {
        private const string Library = "draco_tiny_dec";

        private enum AttributeType : int
        {
            Position = 0,
            Normal = 1
        }

        private enum DataType : uint
        {
            Int32 = 5,
            UInt8 = 2,
            UInt16 = 4,
            UInt32 = 6,
            Float32 = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Mesh
        {
            public IntPtr Ptr;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Attribute
        {
            public IntPtr Ptr;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Data
        {
            public DataType Type;
            public uint Size;
            public IntPtr Ptr;
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Draco_Decompress(IntPtr data, UIntPtr size, out Mesh mesh);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Draco_DecompressedMesh_Release(Mesh mesh);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint Draco_DecompressedMesh_GetNumVertices(Mesh mesh);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Draco_DecompressedMesh_GetAttributeByType(
            Mesh mesh, AttributeType type, int index, out Attribute attribute);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Draco_DecompressedMesh_GetIndices(Mesh mesh, out Data data);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint Draco_Attribute_GetNumComponents(Attribute attribute);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Draco_Attribute_GetData(Mesh mesh, Attribute attribute, out Data data);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Draco_Data_Release(Data data);

        public static bool TryDecode(
            byte[] compressed,
            out Vector3[] positions,
            out Vector3[] normals,
            out uint[] indices)
        {
            positions = null;
            normals = null;
            indices = null;
            if (compressed == null || compressed.Length == 0) return false;

            var handle = GCHandle.Alloc(compressed, GCHandleType.Pinned);
            Mesh mesh = new Mesh();
            try
            {
                var result = Draco_Decompress(handle.AddrOfPinnedObject(),
                    (UIntPtr)compressed.Length, out mesh);
                if (result != 0 || mesh.Ptr == IntPtr.Zero) return false;

                uint vertexCount = Draco_DecompressedMesh_GetNumVertices(mesh);
                if (vertexCount == 0 || vertexCount > int.MaxValue) return false;
                positions = ReadVector3(mesh, AttributeType.Position, (int)vertexCount);
                normals = ReadVector3(mesh, AttributeType.Normal, (int)vertexCount);
                indices = ReadIndices(mesh);
                if (positions == null || indices == null || indices.Length < 3) return false;
                if (normals == null || normals.Length != positions.Length)
                {
                    normals = new Vector3[positions.Length];
                    for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.UnitY;
                }
                return true;
            }
            finally
            {
                if (mesh.Ptr != IntPtr.Zero) Draco_DecompressedMesh_Release(mesh);
                handle.Free();
            }
        }

        private static Vector3[] ReadVector3(Mesh mesh, AttributeType type, int count)
        {
            Attribute attribute;
            Draco_DecompressedMesh_GetAttributeByType(mesh, type, 0, out attribute);
            if (attribute.Ptr == IntPtr.Zero || Draco_Attribute_GetNumComponents(attribute) < 3)
                return null;
            Data data;
            Draco_Attribute_GetData(mesh, attribute, out data);
            try
            {
                if (data.Ptr == IntPtr.Zero || data.Type != DataType.Float32) return null;
                var values = new float[count * 3];
                Marshal.Copy(data.Ptr, values, 0, values.Length);
                var result = new Vector3[count];
                for (int i = 0; i < count; i++)
                    result[i] = new Vector3(values[i * 3], values[i * 3 + 1], values[i * 3 + 2]);
                return result;
            }
            finally
            {
                if (data.Ptr != IntPtr.Zero) Draco_Data_Release(data);
            }
        }

        private static uint[] ReadIndices(Mesh mesh)
        {
            Data data;
            Draco_DecompressedMesh_GetIndices(mesh, out data);
            try
            {
                if (data.Ptr == IntPtr.Zero || data.Size == 0) return null;
                int count = checked((int)(data.Size / SizeOf(data.Type)));
                var result = new uint[count];
                // Evergine's native wrapper exports mesh indices as DT_INT32
                // (the values are non-negative triangle indices, but the
                // wrapper's DataType is signed). Treat both 32-bit forms as
                // unsigned index values after copying the raw bits.
                if (data.Type == DataType.Int32 || data.Type == DataType.UInt32)
                {
                    var values = new int[count];
                    Marshal.Copy(data.Ptr, values, 0, count);
                    for (int i = 0; i < count; i++) result[i] = unchecked((uint)values[i]);
                }
                else if (data.Type == DataType.UInt16)
                {
                    var values = new short[count];
                    Marshal.Copy(data.Ptr, values, 0, count);
                    for (int i = 0; i < count; i++) result[i] = unchecked((ushort)values[i]);
                }
                else if (data.Type == DataType.UInt8)
                {
                    var values = new byte[count];
                    Marshal.Copy(data.Ptr, values, 0, count);
                    for (int i = 0; i < count; i++) result[i] = values[i];
                }
                else return null;
                return result;
            }
            finally
            {
                if (data.Ptr != IntPtr.Zero) Draco_Data_Release(data);
            }
        }

        private static int SizeOf(DataType type)
        {
            if (type == DataType.UInt8) return 1;
            if (type == DataType.UInt16) return 2;
            if (type == DataType.Int32 || type == DataType.UInt32 || type == DataType.Float32) return 4;
            return 0;
        }
    }
}
