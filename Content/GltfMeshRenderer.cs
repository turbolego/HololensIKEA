using System;
using System.Numerics;
using HololensIKEA.Common;
using HololensIKEA.Services;

namespace HololensIKEA.Content
{
    /// <summary>
    /// Renders a glTF mesh using the same shader pipeline as ProductBoxRenderer.
    /// Vertex colors are baked from normals + base color for simple directional lighting.
    /// </summary>
    internal class GltfMeshRenderer : Disposer
    {
        private DeviceResources deviceResources;

        private SharpDX.Direct3D11.InputLayout inputLayout;
        private SharpDX.Direct3D11.Buffer vertexBuffer;
        private SharpDX.Direct3D11.Buffer indexBuffer;
        private SharpDX.Direct3D11.VertexShader vertexShader;
        private SharpDX.Direct3D11.GeometryShader geometryShader;
        private SharpDX.Direct3D11.PixelShader pixelShader;
        private SharpDX.Direct3D11.Buffer modelConstantBuffer;

        private ModelConstantBuffer modelConstantBufferData;
        private int indexCount = 0;
        private bool loadingComplete = false;
        private bool usingVprtShaders = false;

        private Vector3 position = new Vector3(0f, 0f, -2f);
        private Quaternion rotation = Quaternion.Identity;
        private Vector3 scale = Vector3.One;

        public Vector3 Position => position;
        public bool IsLoaded => loadingComplete;

        public GltfMeshRenderer(DeviceResources deviceResources)
        {
            this.deviceResources = deviceResources;
        }

        public void SetPosition(Vector3 pos) => position = pos;
        public void SetRotation(Quaternion rot) => rotation = rot;
        public void SetScale(Vector3 s) => scale = s;

        /// <summary>
        /// Loads mesh data from parsed glTF into GPU buffers.
        /// Call this after CreateDeviceDependentResourcesAsync completes.
        /// </summary>
        public async void CreateDeviceDependentResourcesAsync()
        {
            ReleaseDeviceDependentResources();

            usingVprtShaders = deviceResources.D3DDeviceSupportsVprt;
            var folder = Windows.ApplicationModel.Package.Current.InstalledLocation;

            var vsFileName = usingVprtShaders
                ? "Content\\Shaders\\VPRTVertexShader.cso"
                : "Content\\Shaders\\VertexShader.cso";

            var vsBytes = await DirectXHelper.ReadDataAsync(await folder.GetFileAsync(vsFileName));
            vertexShader = this.ToDispose(new SharpDX.Direct3D11.VertexShader(deviceResources.D3DDevice, vsBytes));

            SharpDX.Direct3D11.InputElement[] vertexDesc =
            {
                new SharpDX.Direct3D11.InputElement("POSITION", 0, SharpDX.DXGI.Format.R32G32B32_Float,  0, 0, SharpDX.Direct3D11.InputClassification.PerVertexData, 0),
                new SharpDX.Direct3D11.InputElement("COLOR",    0, SharpDX.DXGI.Format.R32G32B32_Float, 12, 0, SharpDX.Direct3D11.InputClassification.PerVertexData, 0),
            };
            inputLayout = this.ToDispose(new SharpDX.Direct3D11.InputLayout(deviceResources.D3DDevice, vsBytes, vertexDesc));

            if (!usingVprtShaders)
            {
                var gsBytes = await DirectXHelper.ReadDataAsync(await folder.GetFileAsync("Content\\Shaders\\GeometryShader.cso"));
                geometryShader = this.ToDispose(new SharpDX.Direct3D11.GeometryShader(deviceResources.D3DDevice, gsBytes));
            }

            var psBytes = await DirectXHelper.ReadDataAsync(await folder.GetFileAsync("Content\\Shaders\\PixelShader.cso"));
            pixelShader = this.ToDispose(new SharpDX.Direct3D11.PixelShader(deviceResources.D3DDevice, psBytes));

            modelConstantBuffer = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(
                deviceResources.D3DDevice, SharpDX.Direct3D11.BindFlags.ConstantBuffer, ref modelConstantBufferData));

            // Shaders loaded, but no mesh yet. loadingComplete set when SetMeshData is called.
        }

        /// <summary>
        /// Uploads mesh data to GPU. Can be called at any time after shaders are loaded.
        /// </summary>
        public void SetMeshData(GltfMeshData meshData)
        {
            if (meshData == null || meshData.Positions.Length == 0) return;

            // Clean previous mesh buffers
            this.RemoveAndDispose(ref vertexBuffer);
            this.RemoveAndDispose(ref indexBuffer);

            // Simple directional lighting: dot(normal, lightDir) gives shading
            Vector3 lightDir = Vector3.Normalize(new Vector3(0.5f, 1f, 0.3f));
            float ambient = 0.35f;
            float diffuseStrength = 0.65f;
            Vector3 baseColor = new Vector3(meshData.BaseColor.X, meshData.BaseColor.Y, meshData.BaseColor.Z);

            var vertices = new VertexPositionColor[meshData.Positions.Length];
            for (int i = 0; i < meshData.Positions.Length; i++)
            {
                var pos = meshData.Positions[i];
                var norm = meshData.Normals[i];
                var materialColor = meshData.Colors != null && meshData.Colors.Length == meshData.Positions.Length
                    ? meshData.Colors[i]
                    : baseColor;
                float ndotl = Math.Max(0, Vector3.Dot(norm, lightDir));
                float brightness = ambient + diffuseStrength * ndotl;
                Vector3 col = materialColor * brightness;
                // Clamp
                col = Vector3.Max(Vector3.Zero, Vector3.Min(Vector3.One, col));
                vertices[i] = new VertexPositionColor(pos, col);
            }

            vertexBuffer = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(
                deviceResources.D3DDevice, SharpDX.Direct3D11.BindFlags.VertexBuffer, vertices));

            indexBuffer = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(
                deviceResources.D3DDevice, SharpDX.Direct3D11.BindFlags.IndexBuffer, meshData.Indices));

            indexCount = meshData.Indices.Length;
            scale = Vector3.One; // mesh is already in meters
            loadingComplete = true;
        }

        public void Update(StepTimer timer)
        {
            Matrix4x4 modelTransform =
                Matrix4x4.CreateScale(scale) *
                Matrix4x4.CreateFromQuaternion(rotation) *
                Matrix4x4.CreateTranslation(position);

            modelConstantBufferData.model = Matrix4x4.Transpose(modelTransform);

            if (!loadingComplete) return;

            deviceResources.D3DDeviceContext.UpdateSubresource(ref modelConstantBufferData, modelConstantBuffer);
        }

        public void Render()
        {
            if (!loadingComplete) return;

            var context = deviceResources.D3DDeviceContext;

            int stride = SharpDX.Utilities.SizeOf<VertexPositionColor>();
            context.InputAssembler.SetVertexBuffers(0, new SharpDX.Direct3D11.VertexBufferBinding(vertexBuffer, stride, 0));
            context.InputAssembler.SetIndexBuffer(indexBuffer, SharpDX.DXGI.Format.R16_UInt, 0);
            context.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
            context.InputAssembler.InputLayout = inputLayout;

            context.VertexShader.SetShader(vertexShader, null, 0);
            context.VertexShader.SetConstantBuffers(0, modelConstantBuffer);

            if (!usingVprtShaders)
                context.GeometryShader.SetShader(geometryShader, null, 0);

            context.PixelShader.SetShader(pixelShader, null, 0);

            context.DrawIndexedInstanced(indexCount, 2, 0, 0, 0);
        }

        public void ReleaseDeviceDependentResources()
        {
            loadingComplete = false;
            this.RemoveAndDispose(ref vertexShader);
            this.RemoveAndDispose(ref inputLayout);
            this.RemoveAndDispose(ref vertexBuffer);
            this.RemoveAndDispose(ref indexBuffer);
            this.RemoveAndDispose(ref geometryShader);
            this.RemoveAndDispose(ref pixelShader);
            this.RemoveAndDispose(ref modelConstantBuffer);
        }
    }
}
