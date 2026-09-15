using System;
using System.Diagnostics;
using System.Numerics;
using SharpDX.Direct3D11;
using HololensIKEA.Common;

namespace HololensIKEA.Content
{
    /// <summary>
    /// Which manipulation zone the user's gaze is currently hitting.
    /// </summary>
    public enum ManipulationZone
    {
        None,
        MoveCenter,
        RotateLeft,
        RotateRight,
        RotateTop,
        RotateBottom,
    }

    /// <summary>
    /// Renders four thin colored quads along the edges of the product box front face,
    /// providing visual affordance for the rotation gesture.
    ///
    ///   Left / Right handles  (cyan dim → yellow active) = Y-axis rotation
    ///   Top  / Bottom handles (cyan dim → green  active) = X-axis rotation
    ///
    /// The handles share the product's Scale × Rotation × Translation model matrix
    /// so they stay attached as the product moves and rotates.
    /// </summary>
    internal sealed class ProductManipulationHandles : Disposer
    {
        // ── Color palette ─────────────────────────────────────────────────
        private static readonly Vector3 ColorInactive  = new Vector3(0.15f, 0.50f, 0.55f); // dim teal
        private static readonly Vector3 ColorRotateY   = new Vector3(1.00f, 0.85f, 0.10f); // yellow  (Y-axis)
        private static readonly Vector3 ColorRotateX   = new Vector3(0.20f, 1.00f, 0.45f); // green   (X-axis)

        // ── D3D objects ───────────────────────────────────────────────────
        private readonly DeviceResources _dr;
        private InputLayout    _inputLayout;
        private VertexShader   _vs;
        private GeometryShader _gs;         // null on VPRT devices
        private PixelShader    _ps;
        private SharpDX.Direct3D11.Buffer _vb;      // 16 vertices (4 quads × 4 verts)
        private SharpDX.Direct3D11.Buffer _ib;      // 24 indices  (4 quads × 6 idx)
        private SharpDX.Direct3D11.Buffer _modelCB;

        private ModelConstantBuffer       _cbData  = new ModelConstantBuffer { model = Matrix4x4.Identity };
        private VertexPositionColor[]     _verts   = new VertexPositionColor[16];
        private bool                      _usingVprt;
        private bool                      _loadingComplete;
        private bool                      _visible = true;
        private ManipulationZone          _currentZone = ManipulationZone.None;

        // ── Construction ─────────────────────────────────────────────────

        public ProductManipulationHandles(DeviceResources dr)
        {
            _dr = dr;
            BuildHandlePositions();         // fill CPU array before async load starts
            CreateDeviceDependentResourcesAsync();
        }

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>Gets or sets whether the manipulation handles are visible.</summary>
        public bool IsVisible
        {
            get => _visible;
            set => _visible = value;
        }

        /// <summary>Highlights the handle corresponding to the given zone.</summary>
        public void SetHighlight(ManipulationZone zone)
        {
            if (_currentZone == zone) return;
            _currentZone = zone;
            RebuildVertexColors();
        }

        /// <summary>Must be called every frame (inside timer.Tick) to keep the model transform current.</summary>
        public void SetTransform(Vector3 position, Vector3 dims, Quaternion rotation)
        {
            var m = Matrix4x4.CreateScale(dims.X, dims.Y, dims.Z)
                  * Matrix4x4.CreateFromQuaternion(rotation)
                  * Matrix4x4.CreateTranslation(position);
            _cbData.model = Matrix4x4.Transpose(m);
        }

        /// <summary>Pushes updated CB + vertex colors to the GPU.</summary>
        public void Update()
        {
            if (!_loadingComplete) return;
            _dr.D3DDeviceContext.UpdateSubresource(ref _cbData, _modelCB);
            _dr.D3DDeviceContext.UpdateSubresource(_verts, _vb, 0, 0, 0);
        }

        /// <summary>Draws the four rotation handle quads using instanced stereo (2 eyes).</summary>
        public void Render()
        {
            if (!_loadingComplete || !_visible) return;

            var ctx    = _dr.D3DDeviceContext;
            int stride = SharpDX.Utilities.SizeOf<VertexPositionColor>();

            ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_vb, stride, 0));
            ctx.InputAssembler.SetIndexBuffer(_ib, SharpDX.DXGI.Format.R16_UInt, 0);
            ctx.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
            ctx.InputAssembler.InputLayout       = _inputLayout;

            ctx.VertexShader.SetShader(_vs, null, 0);
            ctx.VertexShader.SetConstantBuffers(0, _modelCB);

            if (!_usingVprt)
                ctx.GeometryShader.SetShader(_gs, null, 0);

            ctx.PixelShader.SetShader(_ps, null, 0);

            ctx.DrawIndexedInstanced(24, 2, 0, 0, 0);
        }

        // ── Device resource lifecycle ─────────────────────────────────────

        public async void CreateDeviceDependentResourcesAsync()
        {
            ReleaseDeviceDependentResources();

            _usingVprt = _dr.D3DDeviceSupportsVprt;
            var device = _dr.D3DDevice;
            var folder = Windows.ApplicationModel.Package.Current.InstalledLocation;

            try
            {
                // ── Vertex shader ─────────────────────────────────────────
                var vsFile  = _usingVprt
                    ? "Content\\Shaders\\VPRTVertexShader.cso"
                    : "Content\\Shaders\\VertexShader.cso";
                var vsBytes = await DirectXHelper.ReadDataAsync(await folder.GetFileAsync(vsFile));
                _vs = this.ToDispose(new VertexShader(device, vsBytes));

                var elements = new[]
                {
                    new InputElement("POSITION", 0, SharpDX.DXGI.Format.R32G32B32_Float,  0, 0,
                        InputClassification.PerVertexData, 0),
                    new InputElement("COLOR",    0, SharpDX.DXGI.Format.R32G32B32_Float, 12, 0,
                        InputClassification.PerVertexData, 0),
                };
                _inputLayout = this.ToDispose(new InputLayout(device, vsBytes, elements));

                // ── Geometry shader (non-VPRT only) ───────────────────────
                if (!_usingVprt)
                {
                    var gsBytes = await DirectXHelper.ReadDataAsync(
                        await folder.GetFileAsync("Content\\Shaders\\GeometryShader.cso"));
                    _gs = this.ToDispose(new GeometryShader(device, gsBytes));
                }

                // ── Pixel shader ──────────────────────────────────────────
                var psBytes = await DirectXHelper.ReadDataAsync(
                    await folder.GetFileAsync("Content\\Shaders\\PixelShader.cso"));
                _ps = this.ToDispose(new PixelShader(device, psBytes));

                // ── Vertex buffer (Default usage — updated via UpdateSubresource) ──
                _vb = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(device, BindFlags.VertexBuffer, _verts));

                // ── Index buffer — 4 rotation handles ─────────────────────
                // This matches the +Z face winding of ProductBoxRenderer (same shaders).
                var indices = new ushort[]
                {
                    // handle 0 (left)
                     0, 2, 3,   0, 3, 1,
                    // handle 1 (right)
                     4, 6, 7,   4, 7, 5,
                    // handle 2 (top)
                     8,10,11,   8,11, 9,
                    // handle 3 (bottom)
                    12,14,15,  12,15,13,
                };
                _ib = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(device, BindFlags.IndexBuffer, indices));

                // ── Model constant buffer ─────────────────────────────────
                _modelCB = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(device, BindFlags.ConstantBuffer, ref _cbData));

                _loadingComplete = true;
                Debug.WriteLine("[Handles] Resources ready (VPRT=" + _usingVprt + ")");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Handles] Init failed: " + ex.Message);
            }
        }

        public void ReleaseDeviceDependentResources()
        {
            _loadingComplete = false;
            this.RemoveAndDispose(ref _vs);
            this.RemoveAndDispose(ref _gs);
            this.RemoveAndDispose(ref _ps);
            this.RemoveAndDispose(ref _inputLayout);
            this.RemoveAndDispose(ref _vb);
            this.RemoveAndDispose(ref _ib);
            this.RemoveAndDispose(ref _modelCB);
        }

        // ── Geometry builders ─────────────────────────────────────────────

        /// <summary>
        /// Defines four thin rectangular strips in local unit-box space.
        ///
        ///   z = 0.52  — front face is at z = 0.50, so handles float slightly in front.
        ///   thickness = 0.08 local units (8% of each box dimension after scaling).
        ///   halfLen   = 0.30 local units (covers 60% of the face edge, centered).
        /// </summary>
        private void BuildHandlePositions()
        {
            const float z         = 0.52f;
            const float thickness = 0.08f;
            const float halfLen   = 0.30f;

            // Left  (RotateY) — vertical strip on the left edge
            SetQuad( 0, -0.5f - thickness, -halfLen, -0.5f,            halfLen, z);
            // Right (RotateY) — vertical strip on the right edge
            SetQuad( 4,  0.5f,             -halfLen,  0.5f + thickness, halfLen, z);
            // Top   (RotateX) — horizontal strip on the top edge
            SetQuad( 8, -halfLen,           0.5f,      halfLen,          0.5f + thickness, z);
            // Bottom(RotateX) — horizontal strip on the bottom edge
            SetQuad(12, -halfLen,          -0.5f - thickness, halfLen, -0.5f,  z);

            RebuildVertexColors();
        }

        /// <summary>Sets 4 vertices [base .. base+3] for a flat axis-aligned quad.</summary>
        private void SetQuad(int base_, float x0, float y0, float x1, float y1, float z)
        {
            var dummy = Vector3.Zero;  // color set by RebuildVertexColors
            _verts[base_ + 0] = new VertexPositionColor(new Vector3(x0, y0, z), dummy); // BL
            _verts[base_ + 1] = new VertexPositionColor(new Vector3(x1, y0, z), dummy); // BR
            _verts[base_ + 2] = new VertexPositionColor(new Vector3(x0, y1, z), dummy); // TL
            _verts[base_ + 3] = new VertexPositionColor(new Vector3(x1, y1, z), dummy); // TR
        }

        private void RebuildVertexColors()
        {
            var leftRight  = ColorForHandle(
                _currentZone == ManipulationZone.RotateLeft ||
                _currentZone == ManipulationZone.RotateRight, isYAxis: true);

            var topBottom  = ColorForHandle(
                _currentZone == ManipulationZone.RotateTop ||
                _currentZone == ManipulationZone.RotateBottom, isYAxis: false);

            for (int i =  0; i <  8; i++) _verts[i].color = leftRight;
            for (int i =  8; i < 16; i++) _verts[i].color = topBottom;
        }

        private static Vector3 ColorForHandle(bool active, bool isYAxis)
        {
            if (!active) return ColorInactive;
            return isYAxis ? ColorRotateY : ColorRotateX;
        }
    }
}
