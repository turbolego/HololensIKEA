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
        private static readonly Vector3 ColorMove      = new Vector3(0.55f, 0.55f, 1.00f); // blue    (move)

        // ── Trashcan button placement (fixed physical offset, independent of box size) ─
        private const float TrashRightOffsetMeters     = 0.20f;  // 20 cm right of the model's right side
        private const float TrashTopOffsetMeters       = 0.30f;  // 30 cm above the model's top
        private const float TrashButtonHalfWidthMeters = 0.06f;
        private const float TrashButtonHalfHeightMeters = 0.06f;
        private Vector3                   _lastTrashDims = Vector3.Zero;

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
        private VertexPositionColor[]     _verts   = new VertexPositionColor[44];  // handles + trash + 3 command buttons
        private bool                      _usingVprt;
        private bool                      _loadingComplete;
        private bool                      _visible = true;
        private ManipulationZone          _currentZone = ManipulationZone.None;
        private bool                      _showTrashcan = false;       // true = gazed-on (for initial display)
        private bool                      _trashcanVisible = false;    // true = user toggled on, stays until tapped away
        private bool                      _commandBarVisible = false;

        // World-space bounding box for trashcan hit-testing.
        public Vector3  TrashcanWorldPos   { get; private set; }
        public Vector3  TrashcanHalfExt    { get; private set; }
        public Vector3[] CommandWorldPos { get; } = new Vector3[3];
        public Vector3[] CommandHalfExt { get; } = new Vector3[3];
        public bool CommandBarVisible
        {
            get => _commandBarVisible;
            set
            {
                if (_commandBarVisible == value) return;
                _commandBarVisible = value;
                RebuildVertexColors();
            }
        }

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

        /// <summary>Gets or sets whether the trashcan button is visible (gaze-driven toggle for initial display).</summary>
        public bool ShowTrashcan
        {
            get => _showTrashcan;
            set
            {
                if (_showTrashcan == value) return;
                _showTrashcan = value;
                RebuildVertexColors();
            }
        }

        /// <summary>True when the user has toggled the trashcan on via double-tap. Stays visible independent of gaze.</summary>
        public bool TrashcanVisible => _trashcanVisible;

        /// <summary>Toggles the trashcan button on/off. Caller is responsible for keeping it in sync with ShowTrashcan.</summary>
        public void SetTrashcanVisible(bool visible)
        {
            if (_trashcanVisible == visible) return;
            _trashcanVisible = visible;
            RebuildVertexColors();
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

            if (dims != _lastTrashDims)
                RebuildTrashcanQuad(dims);
        }

        /// <summary>Pushes updated CB + vertex colors to the GPU.</summary>
        public void Update()
        {
            if (!_loadingComplete) return;
            _dr.D3DDeviceContext.UpdateSubresource(ref _cbData, _modelCB);
            _dr.D3DDeviceContext.UpdateSubresource(_verts, _vb, 0, 0, 0);
        }

        /// <summary>Draws the four handle quads, the trashcan button, and a simple trash can icon using instanced stereo (2 eyes).</summary>
        public void Render()
        {
            if (!_loadingComplete || (!_visible && !_commandBarVisible)) return;

            var ctx    = _dr.D3DDeviceContext;
            int stride = SharpDX.Utilities.SizeOf<VertexPositionColor>();
            int indexCount = (_visible ? (_showTrashcan || _trashcanVisible ? 48 : 24) : 0) + (_commandBarVisible ? 18 : 0);

            ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_vb, stride, 0));
            ctx.InputAssembler.SetIndexBuffer(_ib, SharpDX.DXGI.Format.R16_UInt, 0);
            ctx.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
            ctx.InputAssembler.InputLayout       = _inputLayout;

            ctx.VertexShader.SetShader(_vs, null, 0);
            ctx.VertexShader.SetConstantBuffers(0, _modelCB);

            if (!_usingVprt)
                ctx.GeometryShader.SetShader(_gs, null, 0);

            ctx.PixelShader.SetShader(_ps, null, 0);

            ctx.DrawIndexedInstanced(indexCount, 2, 0, 0, 0);
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

                // ── Index buffer — 4 handles + optional button + icon ───────
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
                    // handle 4 (trashcan button) — indices 24-29
                    16,18,19,  16,19,17,
                    // Trash can icon — body (indices 30-35)
                    20,22,23,  20,23,21,
                    // Trash can icon — lid (indices 36-41)
                    24,26,27,  24,27,25,
                    // Trash can icon — handle (indices 42-47)
                    28,30,31,  28,31,29,
                    // Command bar: Move, Rotate, Delete (indices 48-65)
                    32,34,35, 32,35,33,
                    36,38,39, 36,39,37,
                    40,42,43, 40,43,41,
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
        /// Defines four thin rectangular strips in local unit-box space plus a
        /// large trash can button above the top-center edge, with a simple icon.
        ///
        ///   z = 0.52  — front face is at z = 0.50, so handles float slightly in front.
        ///   thickness = 0.08 local units (8% of each box dimension after scaling).
        ///   halfLen   = 0.30 local units (covers 60% of the face edge, centered).
        ///
        /// Trashcan button (verts 16-31): positioned by RebuildTrashcanQuad at a
        /// fixed physical offset (20 cm right of the box, 30 cm above the box),
        /// independent of the box's dimensions.
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

            // Trashcan button (verts 16-31): fixed physical offset from the box,
            // computed relative to the box's local unit-space so it lands at a
            // constant real-world position regardless of box dimensions.
            RebuildTrashcanQuad(Vector3.One);

            // ── Dedicated command bar below the model ────────────────────
            // Independent targets for Move, Rotate, and Delete. Keeping this
            // bar below the model avoids the top-edge rotation conflict.
            const float commandY0 = -0.78f;
            const float commandY1 = -0.62f;
            SetQuad(32, -0.48f, commandY0, -0.18f, commandY1, z);
            SetQuad(36, -0.15f, commandY0,  0.15f, commandY1, z);
            SetQuad(40,  0.18f, commandY0,  0.48f, commandY1, z);

            RebuildVertexColors();
        }

        /// <summary>
        /// Positions the trashcan button + icon quads (verts 16-31) so that, once
        /// scaled by the box's Scale(dims) model matrix, the button sits at a fixed
        /// physical offset (20 cm right of the box, 30 cm above the box) regardless
        /// of the box's actual dimensions. Must be called whenever dims change.
        /// </summary>
        private void RebuildTrashcanQuad(Vector3 dims)
        {
            if (dims.X <= 0f || dims.Y <= 0f) return;

            float btnCX = 0.5f + TrashRightOffsetMeters / dims.X;
            float btnCY = 0.5f + TrashTopOffsetMeters   / dims.Y;
            float btnHX = TrashButtonHalfWidthMeters  / dims.X;
            float btnHY = TrashButtonHalfHeightMeters / dims.Y;
            const float btnZ  = 0.52f;
            const float iconZ = btnZ + 0.01f;

            // Button background (verts 16-19)
            SetQuad(16, btnCX - btnHX, btnCY - btnHY, btnCX + btnHX, btnCY + btnHY, btnZ);

            // Icon body — wide rectangle filling lower ~60% of button (verts 20-23)
            float bodyW    = btnHX * 0.75f;
            float bodyH    = btnHY * 0.85f;
            float bodyYBot = btnCY - btnHY * 0.3f;
            float bodyYTop = bodyYBot + bodyH;
            SetQuad(20, btnCX - bodyW, bodyYBot, btnCX + bodyW, bodyYTop, iconZ);

            // Icon lid — slightly wider rectangle at top (verts 24-27)
            float lidW    = btnHX * 0.85f;
            float lidH    = btnHY * 0.35f;
            float lidYBot = bodyYTop - btnHY * 0.1f;
            float lidYTop = lidYBot + lidH;
            SetQuad(24, btnCX - lidW, lidYBot, btnCX + lidW, lidYTop, iconZ);

            // Icon handle — small loop on top of lid (verts 28-31)
            float handleW    = btnHX * 0.30f;
            float handleH    = btnHY * 0.45f;
            float handleYBot = lidYTop;
            float handleYTop = handleYBot + handleH;
            SetQuad(28, btnCX - handleW, handleYBot, btnCX + handleW, handleYTop, iconZ);

            _lastTrashDims = dims;
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

            // Trashcan button + icon: bright red when visible, transparent black when hidden.
            bool iconOn = _showTrashcan || _trashcanVisible;
            var tcColor  = iconOn ? new Vector3(1.0f, 0.20f, 0.10f)  : Vector3.Zero;
            var iconColor = iconOn ? new Vector3(1.0f, 1.0f, 1.0f)   : Vector3.Zero; // white icon

            // Button background (verts 16-19)
            for (int i = 16; i < 20; i++) _verts[i].color = tcColor;
            // Icon body (verts 20-23)
            for (int i = 20; i < 24; i++) _verts[i].color = iconColor;
            // Icon lid (verts 24-27)
            for (int i = 24; i < 28; i++) _verts[i].color = iconColor;
            // Icon handle (verts 28-31)
            for (int i = 28; i < 32; i++) _verts[i].color = iconColor;

            var commandColors = new[] {
                new Vector3(0.20f, 0.55f, 1.00f), // Move
                new Vector3(1.00f, 0.80f, 0.10f), // Rotate
                new Vector3(1.00f, 0.20f, 0.10f), // Delete
            };
            for (int button = 0; button < 3; button++)
                for (int i = 32 + button * 4; i < 36 + button * 4; i++)
                    _verts[i].color = _commandBarVisible ? commandColors[button] : Vector3.Zero;
        }

        /// <summary>
        /// Recomputes the world-space position and half-extents of the trashcan
        /// so the caller can use them for ray-hit testing. Must be called after
        /// SetTransform every frame.
        /// </summary>
        public void UpdateTrashcanBounds(Vector3 position, Vector3 dims, Quaternion rotation)
        {
            // Fixed physical offset: 20 cm right of the right edge, 30 cm above the top edge.
            // Computed directly in world units so the target matches the visual button
            // regardless of the box's dimensions (see RebuildTrashcanQuad).
            var rightVec = Vector3.Transform(Vector3.UnitX, rotation) * (dims.X * 0.5f + TrashRightOffsetMeters);
            var topVec   = Vector3.Transform(Vector3.UnitY, rotation) * (dims.Y * 0.5f + TrashTopOffsetMeters);
            TrashcanWorldPos = position + rightVec + topVec;

            var hx = Vector3.Transform(Vector3.UnitX, rotation) * TrashButtonHalfWidthMeters;
            var hy = Vector3.Transform(Vector3.UnitY, rotation) * TrashButtonHalfHeightMeters;
            var hz = Vector3.Transform(Vector3.UnitZ, rotation) * 0.01f;
            TrashcanHalfExt = new Vector3(
                Math.Abs(hx.X) + Math.Abs(hy.X) + Math.Abs(hz.X),
                Math.Abs(hx.Y) + Math.Abs(hy.Y) + Math.Abs(hz.Y),
                Math.Abs(hx.Z) + Math.Abs(hy.Z) + Math.Abs(hz.Z));

            for (int button = 0; button < 3; button++)
            {
                float cx = -0.33f + button * 0.33f;
                var localPt = new Vector3(cx * dims.X, -0.70f * dims.Y, 0.52f * dims.Z);
                var centre = Vector3.Transform(localPt, rotation) + position;
                CommandWorldPos[button] = centre;
                var bx = Vector3.Transform(Vector3.UnitX, rotation) * 0.15f * dims.X;
                var by = Vector3.Transform(Vector3.UnitY, rotation) * 0.08f * dims.Y;
                var bz = Vector3.Transform(Vector3.UnitZ, rotation) * 0.01f * dims.Z;
                CommandHalfExt[button] = new Vector3(
                    Math.Abs(bx.X) + Math.Abs(by.X) + Math.Abs(bz.X),
                    Math.Abs(bx.Y) + Math.Abs(by.Y) + Math.Abs(bz.Y),
                    Math.Abs(bx.Z) + Math.Abs(by.Z) + Math.Abs(bz.Z));
            }
        }

        private static Vector3 ColorForHandle(bool active, bool isYAxis)
        {
            if (!active) return ColorInactive;
            return isYAxis ? ColorRotateY : ColorRotateX;
        }
    }
}
