//
// Comment out this preprocessor definition to disable all of the
// sample content.
//
// To remove the content after disabling it:
//     * Remove the unused code from this file.
//     * Delete the Content folder provided with this template.
//
#define DRAW_SAMPLE_CONTENT

using System;
using System.Diagnostics;
using System.Numerics;
using Windows.Gaming.Input;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Holographic;
using Windows.Perception.Spatial;
using Windows.UI.Input.Spatial;
using Windows.UI.Popups;

using HololensIKEA.Common;
using HololensIKEA.Content;
using HololensIKEA.Models;
using HololensIKEA.Services;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using System.Collections.Generic;

#if DRAW_SAMPLE_CONTENT
// HololensIKEA.Content already imported above
#endif

namespace HololensIKEA
{
    /// <summary>
    /// Updates, renders, and presents holographic content using Direct3D.
    /// </summary>
    internal class HolographicTemplateAppMain : IDisposable
    {

#if DRAW_SAMPLE_CONTENT
        // Spinning cube: loading indicator.
        private SpinningCubeRenderer        spinningCubeRenderer;
        // Product box: shown after a product is loaded.
        private ProductBoxRenderer          productBoxRenderer;
        // Product sprite: image overlay on box front face.
        private ProductSpriteRenderer       productSpriteRenderer;
        // Holographic keyboard: gaze + air-tap text input.
        private KeyboardInputHandler        keyboardInputHandler;

        private SpatialInputHandler         spatialInputHandler;
#endif

        // --- Product loading state machine ---
        private enum AppState { Idle, InputMode, Loading, ShowingProduct }
        private AppState                    appState = AppState.Idle;
        private string                      inputBuffer = "";
        private Task<RenderableProduct>     pendingProductLoad;
        private IkeaProductRepository       productRepository;
        private bool                        keyboardPositioned = false;
        private bool                        airTapInProgress = false;  // Track ongoing air-tap

        // --- Image / sprite loading ---
        private ProductImageLoader          imageLoader;
        private readonly object             _pendingImageLock = new object();
        private SharpDX.Direct3D11.ShaderResourceView _pendingImageSRV;
        private SharpDX.Direct3D11.ShaderResourceView _pendingDispSRV;
        private SharpDX.Direct3D11.ShaderResourceView _pendingSideSRV;
        private ContentBounds _pendingBounds;
        private ViewType      _pendingViewType = ViewType.FrontOnly;

        // --- Active product sprite state (needed to restore after rendering saved instances) ---
        private SharpDX.Direct3D11.ShaderResourceView _activeTextureSRV;
        private SharpDX.Direct3D11.ShaderResourceView _activeDispSRV;
        private SharpDX.Direct3D11.ShaderResourceView _activeSideSRV;
        private System.Numerics.Vector4               _activeContentBounds = new System.Numerics.Vector4(0f, 0f, 1f, 1f);
        private ViewType                              _activeViewType = ViewType.FrontOnly;

        // Last known head position/forward — used to position the box when a product finishes loading.
        private Vector3                     _lastHeadPosition = new Vector3(0f, 0f, 0f);
        private Vector3                     _lastHeadForward  = new Vector3(0f, 0f, -1f);

        // --- Pinch-drag state ---
        private bool    _isDraggingProduct = false;
        private Vector3 _dragHandOffset    = Vector3.Zero;  // handPos - boxCenter at drag start
        private float   _dragGazeDistance  = 2f;            // fallback depth when hand pos unavailable
        private Vector3 _productPosition   = new Vector3(0f, 0f, -2f);  // cached for hit-test
        private Vector3 _productDims       = Vector3.Zero;  // zero until first product loads

        // --- Bookmarks panel drag state ---
        private bool    _isDraggingBookmarks     = false;
        private Vector3 _bookmarksDragHandOffset = Vector3.Zero;
        private float   _bookmarksDragGazeDistance = 1.5f;

        // --- Rotation state ---
        private Quaternion _productRotation      = Quaternion.Identity;
        private bool       _isRotating           = false;
        private Vector3    _rotationAxis         = Vector3.UnitY;
        private Vector3    _rotationStartGazeDir = Vector3.Zero;
        private Quaternion _rotationStartQuat    = Quaternion.Identity;

        // --- Manipulation handles (edge visual indicators) ---
        private ProductManipulationHandles _manipulationHandles;

        // --- Dimension labels (show sizes in mm) ---
        private ProductDimensionLabels     _dimensionLabels;

        // --- Voice input for product IDs ---
        private SpeechCommandHandler       _speechHandler;

        // --- Multi-product support ---
        private List<ProductInstance>      _productInstances = new List<ProductInstance>();
        private int                        _pendingProductElnummer = 0;  // elnummer being loaded
        private string                     _pendingBookmarkGlbUrl = "";  // direct GLB URL from bookmark, if any
        private RenderableProduct          _currentProduct = null;  // product being displayed (for mesh fallback)

        // --- Gaze state for handle visibility ---
        private bool                       _gazeOnProduct = false;

        // --- Search support ---
        private ProductSearchService           _productSearchService;
        private SearchResultsDialog            _searchResultsDialog;

        // --- Bookmarks support ---
        private BookmarksService               _bookmarksService;
        private BookmarksDialog                _bookmarksDialog;
        private bool                           _bookmarksLoading = false;
        private Queue<Bookmark>                _bookmarkLoadQueue = new Queue<Bookmark>();

        // --- IKEA GLB model support ---
        private ModelService3D                  _modelService3D = new ModelService3D();
        private GltfMeshRenderer               _gltfMeshRenderer;
        private GltfMeshData                   _activeMeshData;
        private Task<GltfMeshData>             _pending3DModelLoad;
        private bool                           _activeProductRequiresMesh;
        private enum ModelCommandMode { Direct, Move, Rotate }
        private ModelCommandMode               _modelCommandMode = ModelCommandMode.Direct;

        // --- 3D mesh independent transform ---
        private Vector3    _meshPosition = new Vector3(0f, 0f, -2f);
        private Quaternion _meshRotation = Quaternion.Identity;
        private Vector3    _meshDims     = Vector3.Zero;  // bounding box in meters for hit-test
        private bool       _isDraggingMesh = false;
        private Vector3    _meshDragHandOffset = Vector3.Zero;
        private float      _meshDragGazeDistance = 2f;
        private bool       _isRotatingMesh = false;
        private Vector3    _meshRotationAxis = Vector3.UnitY;
        private Vector3    _meshRotStartGazeDir = Vector3.Zero;
        private Quaternion _meshRotStartQuat = Quaternion.Identity;
        private bool       _gazeOnMesh = false;
        private bool       _meshLoadFailed = false;  // true when mesh download/parsing fails

        // --- Delete confirmation dialog state ---
        private bool                       _deleteDialogShowing = false;
        private int                        _deleteTargetInstanceIndex = -1;  // -1 = active mesh

        // --- Double-tap for trashcan toggle ---
        private DateTime      _lastMeshTapTime = DateTime.MinValue;
        private const int     DoubleTapWindowMs = 400;

        // Cached reference to device resources.
        private DeviceResources             deviceResources;

        // Render loop timer.
        private StepTimer                   timer = new StepTimer();

        // Represents the holographic space around the user.
        HolographicSpace                    holographicSpace;

        // SpatialLocator that is attached to the default HolographicDisplay.
        SpatialLocator                      spatialLocator;

        // A stationary reference frame based on spatialLocator.
        SpatialStationaryFrameOfReference   stationaryReferenceFrame;

        // Keep track of gamepads.
        private class GamepadWithButtonState
        {
            public Windows.Gaming.Input.Gamepad gamepad;
            public bool buttonAWasPressedLastFrame;
            public GamepadWithButtonState(
                Windows.Gaming.Input.Gamepad gamepad,
                bool buttonAWasPressedLastFrame)
            {
                this.gamepad = gamepad;
                this.buttonAWasPressedLastFrame = buttonAWasPressedLastFrame;
            }
        };
        List<GamepadWithButtonState>        gamepads = new List<GamepadWithButtonState>();

        // Keep track of mouse input.
        bool                                pointerPressed = false;

        // Cache whether or not the HolographicCamera.Display property can be accessed.
        bool                                canGetHolographicDisplayForCamera = false;

        // Cache whether or not the HolographicDisplay.GetDefault() method can be called.
        bool                                canGetDefaultHolographicDisplay = false;

        // Cache whether or not the HolographicCameraRenderingParameters.CommitDirect3D11DepthBuffer() method can be called.
        bool                                canCommitDirect3D11DepthBuffer = false;

        /// <summary>
        /// Loads and initializes application assets when the application is loaded.
        /// </summary>
        /// <param name="deviceResources"></param>
        public HolographicTemplateAppMain(DeviceResources deviceResources)
        {
            this.deviceResources = deviceResources;

            // Register to be notified if the Direct3D device is lost.
            this.deviceResources.DeviceLost     += this.OnDeviceLost;
            this.deviceResources.DeviceRestored += this.OnDeviceRestored;

            // If connected, a game controller can also be used for input.
            Gamepad.GamepadAdded += this.OnGamepadAdded;
            Gamepad.GamepadRemoved += this.OnGamepadRemoved;

            foreach (var gamepad in Gamepad.Gamepads)
            {
                OnGamepadAdded(null, gamepad);
            }
            
            canGetHolographicDisplayForCamera = Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Holographic.HolographicCamera", "Display");
            canGetDefaultHolographicDisplay = Windows.Foundation.Metadata.ApiInformation.IsMethodPresent("Windows.Graphics.Holographic.HolographicDisplay", "GetDefault");
            canCommitDirect3D11DepthBuffer = Windows.Foundation.Metadata.ApiInformation.IsMethodPresent("Windows.Graphics.Holographic.HolographicCameraRenderingParameters", "CommitDirect3D11DepthBuffer");
        }

        public void SetHolographicSpace(HolographicSpace holographicSpace)
        {
            this.holographicSpace = holographicSpace;

            // 
            // TODO: Add code here to initialize your content.
            // 

#if DRAW_SAMPLE_CONTENT
            // Initialize renderers.
            spinningCubeRenderer  = new SpinningCubeRenderer(deviceResources);
            productBoxRenderer    = new ProductBoxRenderer(deviceResources);
            productSpriteRenderer = new ProductSpriteRenderer(deviceResources);
            _gltfMeshRenderer     = new GltfMeshRenderer(deviceResources);
            _gltfMeshRenderer.CreateDeviceDependentResourcesAsync();
            keyboardInputHandler  = new KeyboardInputHandler(deviceResources);
            imageLoader           = new ProductImageLoader(deviceResources.D3DDevice);
            _manipulationHandles  = new ProductManipulationHandles(deviceResources);
            _dimensionLabels      = new ProductDimensionLabels(deviceResources);
            _speechHandler        = new SpeechCommandHandler();

            // Initialize speech recognition
            InitializeSpeechAsync();

            // 3D keyboard: text changes update the buffer
            keyboardInputHandler.OnTextChanged += (text) => {
                inputBuffer = text;
                Debug.WriteLine("[Input] Text: " + text);
            };
            // 3D keyboard: Enter key (\u21b5) fires submit
            keyboardInputHandler.OnSubmit += (text) => {
                if (appState == AppState.InputMode || appState == AppState.ShowingProduct)
                {
                    inputBuffer = text;
                    StartProductLoad();
                }
            };

            // URL entry is performed with the holographic keyboard. The legacy
            // numeric product voice event is intentionally not connected here.
            // Voice input: clear all products
            _speechHandler.OnClearAllProducts += () => {
                Debug.WriteLine("[Voice] Clear all products");
                ClearAllProducts();
            };
            // Voice input: dismiss dialog
            _speechHandler.OnDismissDialog += () => {
                Debug.WriteLine("[Voice] Dismiss dialog");
                if (_searchResultsDialog != null) _searchResultsDialog.Hide();
                if (_bookmarksDialog != null) _bookmarksDialog.Hide();
            };
            // Voice input: show bookmarks
            _speechHandler.OnShowBookmarks += () => {
                Debug.WriteLine("[Voice] Show bookmarks");
                ShowBookmarksDialog();
            };
            // Voice input: search bookmarks by name
            _speechHandler.OnSearchBookmarks += (query) => {
                Debug.WriteLine("[Voice] Search bookmarks: " + query);
                ShowBookmarksSearch(query);
            };
            _speechHandler.OnBookmarkProductRequested += (recognizedText) => {
                var bookmark = BookmarkVoiceCommandResolver.FindBookmark(
                    recognizedText, _bookmarksService?.Bookmarks);
                if (bookmark != null)
                {
                    Debug.WriteLine("[Voice] Queue bookmark: " + bookmark.Name);
                    QueueBookmarkProductLoad(bookmark);
                }
            };
            // Voice input: digits spoken while gazing at keyboard → type them
            _speechHandler.OnTextForKeyboard += (text) => {
                Debug.WriteLine("[Voice→Keyboard] " + text);
                keyboardInputHandler.InsertText(text);
            };
            // Voice input: non-numeric text while gazing at keyboard → search
            // saved IKEA bookmarks by name (StartProductSearch queried a
            // third-party electrical-parts catalog left over from the
            // project this app was forked from, and is no longer used).
            _speechHandler.OnSearchQuery += (query) => {
                Debug.WriteLine("[Voice→Search] " + query);
                ShowBookmarksSearch(query);
            };
            // Voice input: status changed
            _speechHandler.OnStatusChanged += (status) => {
                Debug.WriteLine("[Voice] Status: " + status);
            };

            // Search service and results dialog
            _productSearchService = new ProductSearchService();
            _searchResultsDialog  = new SearchResultsDialog(deviceResources);
            _searchResultsDialog.OnProductSelected += (produktnr) => {
                Debug.WriteLine("[Search] Selected product: " + produktnr);
                _searchResultsDialog.Hide();
                inputBuffer = produktnr;
                StartProductLoad();
            };

            // Bookmarks service and dialog
            _bookmarksService = new BookmarksService();
            _bookmarksDialog = new BookmarksDialog(deviceResources);
            _bookmarksDialog.OnBookmarkSelected += (bookmark) => {
                Debug.WriteLine("[Bookmarks] Selected: " + bookmark.Name);
                QueueBookmarkProductLoad(bookmark);
            };
            LoadBookmarksAsync();

            spatialInputHandler  = new SpatialInputHandler();
            productRepository    = new IkeaProductRepository();

            // The bookmarks list (loaded above) is now the only way to pick a
            // product; the legacy "type a product number" keyboard is no
            // longer shown.
            appState = AppState.InputMode;
#endif

            if (canGetDefaultHolographicDisplay)
            {
                // Subscribe for notifications about changes to the state of the default HolographicDisplay 
                // and its SpatialLocator.
                HolographicSpace.IsAvailableChanged += this.OnHolographicDisplayIsAvailableChanged;
            }

            // Acquire the current state of the default HolographicDisplay and its SpatialLocator.
            OnHolographicDisplayIsAvailableChanged(null, null);

            // Respond to camera added events by creating any resources that are specific
            // to that camera, such as the back buffer render target view.
            // When we add an event handler for CameraAdded, the API layer will avoid putting
            // the new camera in new HolographicFrames until we complete the deferral we created
            // for that handler, or return from the handler without creating a deferral. This
            // allows the app to take more than one frame to finish creating resources and
            // loading assets for the new holographic camera.
            // This function should be registered before the app creates any HolographicFrames.
            holographicSpace.CameraAdded += this.OnCameraAdded;

            // Respond to camera removed events by releasing resources that were created for that
            // camera.
            // When the app receives a CameraRemoved event, it releases all references to the back
            // buffer right away. This includes render target views, Direct2D target bitmaps, and so on.
            // The app must also ensure that the back buffer is not attached as a render target, as
            // shown in DeviceResources.ReleaseResourcesForBackBuffer.
            holographicSpace.CameraRemoved += this.OnCameraRemoved;

            // Notes on spatial tracking APIs:
            // * Stationary reference frames are designed to provide a best-fit position relative to the
            //   overall space. Individual positions within that reference frame are allowed to drift slightly
            //   as the device learns more about the environment.
            // * When precise placement of individual holograms is required, a SpatialAnchor should be used to
            //   anchor the individual hologram to a position in the real world - for example, a point the user
            //   indicates to be of special interest. Anchor positions do not drift, but can be corrected; the
            //   anchor will use the corrected position starting in the next frame after the correction has
            //   occurred.
        }

        public void Dispose()
        {
#if DRAW_SAMPLE_CONTENT
            if (spinningCubeRenderer != null)
            {
                spinningCubeRenderer.Dispose();
                spinningCubeRenderer = null;
            }
            if (productBoxRenderer != null)
            {
                productBoxRenderer.Dispose();
                productBoxRenderer = null;
            }
            if (productSpriteRenderer != null)
            {
                productSpriteRenderer.Dispose();
                productSpriteRenderer = null;
            }
            if (keyboardInputHandler != null)
            {
                keyboardInputHandler.Dispose();
                keyboardInputHandler = null;
            }
            if (_manipulationHandles != null)
            {
                _manipulationHandles.Dispose();
                _manipulationHandles = null;
            }
            if (_dimensionLabels != null)
            {
                _dimensionLabels.Dispose();
                _dimensionLabels = null;
            }
            if (_speechHandler != null)
            {
                _speechHandler.Dispose();
                _speechHandler = null;
            }
            if (_searchResultsDialog != null)
            {
                _searchResultsDialog.Dispose();
                _searchResultsDialog = null;
            }
            if (_bookmarksDialog != null)
            {
                _bookmarksDialog.Dispose();
                _bookmarksDialog = null;
            }
            _productSearchService = null;
            // Dispose all product instances
            foreach (var inst in _productInstances)
            {
                inst.DisposeTextures();
            }
            _productInstances.Clear();
#endif
        }

        /// <summary>
        /// Updates the application state once per frame.
        /// </summary>
        public HolographicFrame Update()
        {
            // Before doing the timer update, there is some work to do per-frame
            // to maintain holographic rendering. First, we will get information
            // about the current frame.

            // The HolographicFrame has information that the app needs in order
            // to update and render the current frame. The app begins each new
            // frame by calling CreateNextFrame.
            HolographicFrame holographicFrame = holographicSpace.CreateNextFrame();

            // Get a prediction of where holographic cameras will be when this frame
            // is presented.
            HolographicFramePrediction prediction = holographicFrame.CurrentPrediction;

            // Back buffers can change from frame to frame. Validate each buffer, and recreate
            // resource views and depth buffers as needed.
            deviceResources.EnsureCameraResources(holographicFrame, prediction);

#if DRAW_SAMPLE_CONTENT
            if (stationaryReferenceFrame != null)
            {
                // Check for new input state since the last frame.
                for (int i = 0; i < gamepads.Count; ++i)
                {
                    bool buttonDownThisUpdate = (gamepads[i].gamepad.GetCurrentReading().Buttons & GamepadButtons.A) == GamepadButtons.A;
                    if (buttonDownThisUpdate && !gamepads[i].buttonAWasPressedLastFrame)
                    {
                        pointerPressed = true;
                    }
                    gamepads[i].buttonAWasPressedLastFrame = buttonDownThisUpdate;
                }

                SpatialInteractionSourceState pointerState = spatialInputHandler.CheckForInput();
                SpatialPointerPose pose = null;
                if (null != pointerState)
                {
                    pose = pointerState.TryGetPointerPose(stationaryReferenceFrame.CoordinateSystem);
                }
                else if (pointerPressed)
                {
                    pose = SpatialPointerPose.TryGetAtTimestamp(stationaryReferenceFrame.CoordinateSystem, prediction.Timestamp);
                }
                pointerPressed = false;

                // Get head pose every frame (independent of hand/controller input) for keyboard placement.
                SpatialPointerPose headPose = SpatialPointerPose.TryGetAtTimestamp(stationaryReferenceFrame.CoordinateSystem, prediction.Timestamp);

                // Keep a running copy of the head transform so we can position new holograms
                // even when no air-tap is in progress (e.g. when a product finishes loading).
                if (headPose != null)
                {
                    var hp = headPose.Head.Position;
                    var hd = headPose.Head.ForwardDirection;
                    _lastHeadPosition = new Vector3(hp.X, hp.Y, hp.Z);
                    _lastHeadForward  = new Vector3(hd.X, hd.Y, hd.Z);
                }

                // Update keyboard handler gaze hit-testing
                if (keyboardInputHandler.IsVisible && headPose != null)
                {
                    var headPos = headPose.Head.Position;
                    var headDir = headPose.Head.ForwardDirection;
                    
                    // Position keyboard in front of user on first visibility
                    if (!keyboardPositioned)
                    {
                        keyboardInputHandler.PlaceInFrontOfUser(
                            new Vector3(headPos.X, headPos.Y, headPos.Z),
                            new Vector3(headDir.X, headDir.Y, headDir.Z));
                        keyboardPositioned = true;
                        Debug.WriteLine("[Input] Keyboard positioned in front of user");
                    }
                    
                    keyboardInputHandler.Update(
                        new Vector3(headPos.X, headPos.Y, headPos.Z),
                        new Vector3(headDir.X, headDir.Y, headDir.Z));

                    // Tell speech handler whether user is gazing at keyboard
                    if (_speechHandler != null)
                        _speechHandler.IsGazingAtKeyboard = keyboardInputHandler.IsGazeOnPanel;
                }

                // Update search results dialog gaze hit-testing
                if (_searchResultsDialog != null && _searchResultsDialog.IsVisible && headPose != null)
                {
                    var headPos2 = headPose.Head.Position;
                    var headDir2 = headPose.Head.ForwardDirection;
                    _searchResultsDialog.UpdateGaze(
                        new Vector3(headPos2.X, headPos2.Y, headPos2.Z),
                        new Vector3(headDir2.X, headDir2.Y, headDir2.Z));
                }

                // Update bookmarks dialog gaze hit-testing
                if (_bookmarksDialog != null && _bookmarksDialog.IsVisible && headPose != null)
                {
                    var headPos3 = headPose.Head.Position;
                    var headDir3 = headPose.Head.ForwardDirection;
                    _bookmarksDialog.UpdateGaze(
                        new Vector3(headPos3.X, headPos3.Y, headPos3.Z),
                        new Vector3(headDir3.X, headDir3.Y, headDir3.Z));
                }

                // Update manipulation handle highlights based on current gaze (runs every frame).
                // Handles are only visible when gazing at the product.
                if (appState == AppState.ShowingProduct && headPose != null
                    && !_isDraggingProduct && !_isRotating && !_isDraggingMesh && !_isRotatingMesh)
                {
                    var gO = new Vector3(headPose.Head.Position.X,
                                         headPose.Head.Position.Y,
                                         headPose.Head.Position.Z);
                    var gD = new Vector3(headPose.Head.ForwardDirection.X,
                                         headPose.Head.ForwardDirection.Y,
                                         headPose.Head.ForwardDirection.Z);
                    float hd = 0f;
                    var hz = ManipulationZone.None;
                    var activePosition = _activeMeshData != null ? _meshPosition : _productPosition;
                    var activeDimensions = _activeMeshData != null ? _meshDims : _productDims;
                    var activeRotation = _activeMeshData != null ? _meshRotation : _productRotation;
                    _gazeOnMesh = _activeMeshData != null &&
                        GazeHitsBox(gO, gD, activePosition, activeDimensions * 0.5f, 10f, out hd);
                    _gazeOnProduct = _activeMeshData == null && !_activeProductRequiresMesh &&
                        GazeHitsBox(gO, gD, activePosition, activeDimensions * 0.5f, 10f, out hd);
                    _manipulationHandles.IsVisible = _gazeOnMesh || _gazeOnProduct;
                    _manipulationHandles.CommandBarVisible = _activeMeshData != null;
                    // Show trashcan only when gazing at a product that has a loaded 3D mesh.
                    // Trashcan stays visible once toggled on via double-tap (independent of gaze).
                    _manipulationHandles.ShowTrashcan = _gazeOnMesh;
                    if (_manipulationHandles.IsVisible)
                    {
                        var localOff = Vector3.Transform(
                            gO + gD * hd - activePosition,
                            Quaternion.Inverse(activeRotation));
                        float nx = localOff.X / (activeDimensions.X * 0.5f);
                        float ny = localOff.Y / (activeDimensions.Y * 0.5f);
                        const float edge = 0.62f;
                        if      (nx < -edge) hz = ManipulationZone.RotateLeft;
                        else if (nx >  edge) hz = ManipulationZone.RotateRight;
                        else if (ny >  edge) hz = ManipulationZone.RotateTop;
                        else if (ny < -edge) hz = ManipulationZone.RotateBottom;
                        else                 hz = ManipulationZone.MoveCenter;
                    }
                    _manipulationHandles.SetHighlight(hz);
                    // Update trashcan world bounds for hit-testing.
                    // Must update even when not gazing (trashcan may be toggled on via double-tap).
                    if (_activeMeshData != null)
                    {
                        _manipulationHandles.UpdateTrashcanBounds(
                            activePosition, activeDimensions, activeRotation);
                    }
                }

                // Air-tap / pinch-drag dispatch.
                //
                // Priority:
                //   0. Bookmarks panel title bar → start dragging the panel
                //   1. ShowingProduct + trashcan hit → delete confirmation dialog
                //   2. ShowingProduct + gaze hits product edge zone  → start rotation
                //   3. ShowingProduct + gaze hits product center zone → start drag
                //   4. ShowingProduct + gaze hits 3D mesh → start mesh drag/rotate
                //   5. Keyboard visible → route tap to keyboard
                //   6. Otherwise → position spinning cube
                if (pointerState != null && !_isDraggingBookmarks && _bookmarksDialog != null
                    && _bookmarksDialog.IsVisible && _bookmarksDialog.IsGazeOnTitleBar)
                {
                    var loc = pointerState.Properties.TryGetLocation(stationaryReferenceFrame.CoordinateSystem);
                    if (loc?.Position != null)
                    {
                        var hp = loc.Position.Value;
                        _bookmarksDragHandOffset = new Vector3(hp.X, hp.Y, hp.Z) - _bookmarksDialog.Position;
                    }
                    else
                    {
                        _bookmarksDragHandOffset = Vector3.Zero;
                    }
                    if (headPose != null)
                    {
                        var gO = new Vector3(headPose.Head.Position.X, headPose.Head.Position.Y, headPose.Head.Position.Z);
                        _bookmarksDragGazeDistance = Vector3.Distance(gO, _bookmarksDialog.Position);
                    }
                    _isDraggingBookmarks = true;
                    Debug.WriteLine("[BookmarksDrag] Started");
                }
                else if (pointerState != null && appState == AppState.ShowingProduct
                    && !_isDraggingProduct && !_isRotating && !_isDraggingMesh && !_isRotatingMesh)
                {
                    if (pose != null)
                    {
                        var rayOrigin = new Vector3(pose.Head.Position.X,
                                                    pose.Head.Position.Y,
                                                    pose.Head.Position.Z);
                        var rayDir    = new Vector3(pose.Head.ForwardDirection.X,
                                                    pose.Head.ForwardDirection.Y,
                                                    pose.Head.ForwardDirection.Z);
                        float hitDist = 0f;
                        float meshHitDist = 0f;
                        bool hitProduct = _activeMeshData == null && !_activeProductRequiresMesh &&
                                          GazeHitsBox(rayOrigin, rayDir, _productPosition,
                                        _productDims * 0.5f, 10f, out hitDist);
                        bool hitMesh = _activeMeshData != null &&
                                       GazeHitsBox(rayOrigin, rayDir, _meshPosition,
                                       _meshDims * 0.5f, 10f, out meshHitDist);

                        // Priority 1: dedicated command bar; it is independent of
                        // the edge handles and remains visible below the mesh.
                        bool commandTapped = false;
                        if (_activeMeshData != null && _manipulationHandles.CommandBarVisible)
                        {
                            for (int command = 0; command < 3; command++)
                            {
                                float commandHitDist;
                                if (!GazeHitsBox(rayOrigin, rayDir,
                                    _manipulationHandles.CommandWorldPos[command],
                                    _manipulationHandles.CommandHalfExt[command], 10f, out commandHitDist))
                                    continue;
                                if (command == 0) _modelCommandMode = ModelCommandMode.Move;
                                else if (command == 1) _modelCommandMode = ModelCommandMode.Rotate;
                                else ShowDeleteMeshDialog(-1);
                                commandTapped = true;
                                Debug.WriteLine("[Input] Command bar: " + (command == 0 ? "Move" : command == 1 ? "Rotate" : "Delete"));
                                break;
                            }
                        }

                        // Priority 2: legacy trashcan hit → delete confirmation dialog.
                        bool trashcanTapped = commandTapped;
                        if (_manipulationHandles.ShowTrashcan && _activeMeshData != null)
                        {
                            float tcHitDist;
                            if (GazeHitsBox(rayOrigin, rayDir,
                                _manipulationHandles.TrashcanWorldPos,
                                _manipulationHandles.TrashcanHalfExt, 10f, out tcHitDist))
                            {
                                Debug.WriteLine("[Input] Air-tap on trashcan — show delete dialog");
                                ShowDeleteMeshDialog(/* instanceIndex */ -1);
                                trashcanTapped = true;
                            }
                        }

                        if (!trashcanTapped)
                        {
                            if (hitProduct && (!hitMesh || hitDist <= meshHitDist))
                            {
                                // Determine zone: outer 38% of each axis = rotation handle.
                                var localOff = Vector3.Transform(
                                    rayOrigin + rayDir * hitDist - _productPosition,
                                    Quaternion.Inverse(_productRotation));
                                float nx = localOff.X / (_productDims.X * 0.5f);
                                float ny = localOff.Y / (_productDims.Y * 0.5f);
                                const float edge   = 0.62f;
                                bool  inEdge = Math.Abs(nx) > edge || Math.Abs(ny) > edge;

                                if (inEdge)
                                {
                                    // Start rotation — Y-axis for horizontal edges, X-axis for vertical.
                                    _rotationAxis         = Math.Abs(nx) > Math.Abs(ny)
                                        ? Vector3.UnitY : Vector3.UnitX;
                                    _rotationStartGazeDir = rayDir;
                                    _rotationStartQuat    = _productRotation;
                                    _isRotating           = true;
                                    var rotZone = nx < -edge ? ManipulationZone.RotateLeft  :
                                                  nx >  edge ? ManipulationZone.RotateRight :
                                                  ny >  edge ? ManipulationZone.RotateTop   :
                                                               ManipulationZone.RotateBottom;
                                    _manipulationHandles.SetHighlight(rotZone);
                                    Debug.WriteLine("[Rotate] Started axis=" +
                                        (_rotationAxis == Vector3.UnitY ? "Y" : "X"));
                                }
                                else
                                {
                                    // Start drag (move).
                                    var loc = pointerState.Properties.TryGetLocation(
                                                  stationaryReferenceFrame.CoordinateSystem);
                                    if (loc?.Position != null)
                                    {
                                        var hp = loc.Position.Value;
                                        _dragHandOffset = new Vector3(hp.X, hp.Y, hp.Z) - _productPosition;
                                    }
                                    else
                                    {
                                        _dragHandOffset = Vector3.Zero;
                                    }
                                    _dragGazeDistance  = hitDist;
                                    _isDraggingProduct = true;
                                    _manipulationHandles.SetHighlight(ManipulationZone.MoveCenter);
                                    Debug.WriteLine("[Drag] Started at dist=" + hitDist.ToString("F2"));
                                }
                            }
                            else if (hitMesh)
                            {
                                // Hit the 3D mesh model — detect double-tap for trashcan toggle
                                var now = DateTime.UtcNow;
                                bool isDoubleTap = (now - _lastMeshTapTime).TotalMilliseconds <= DoubleTapWindowMs;
                                _lastMeshTapTime = now;

                                if (isDoubleTap && _activeMeshData != null)
                                {
                                    // Double-tap: delete the model directly.
                                    Debug.WriteLine("[Input] Double-tap on mesh — show delete dialog");
                                    ShowDeleteMeshDialog(-1);
                                }
                                else if (_manipulationHandles.TrashcanVisible)
                                {
                                    // Single-tap on mesh while trashcan is visible → hide it
                                    _manipulationHandles.SetTrashcanVisible(false);
                                    Debug.WriteLine("[Input] Single-tap on mesh — hiding trashcan");
                                }
                                else
                                {
                                    // Normal single-tap: start mesh drag or rotation
                                    meshHitDist = 0f;
                                    GazeHitsBox(rayOrigin, rayDir, _meshPosition, _meshDims * 0.5f, 10f, out meshHitDist);
                                    var localOff = Vector3.Transform(
                                        rayOrigin + rayDir * meshHitDist - _meshPosition,
                                        Quaternion.Inverse(_meshRotation));
                                    float nx = _meshDims.X > 0 ? localOff.X / (_meshDims.X * 0.5f) : 0;
                                    float ny = _meshDims.Y > 0 ? localOff.Y / (_meshDims.Y * 0.5f) : 0;
                                    const float edge = 0.62f;
                                    bool inEdge = _modelCommandMode == ModelCommandMode.Rotate ||
                                                  (_modelCommandMode != ModelCommandMode.Move &&
                                                   (Math.Abs(nx) > edge || Math.Abs(ny) > edge));

                                    if (inEdge)
                                    {
                                        // Start mesh rotation — also hide trashcan
                                        _manipulationHandles.SetTrashcanVisible(false);
                                        _meshRotationAxis    = Math.Abs(nx) > Math.Abs(ny) ? Vector3.UnitY : Vector3.UnitX;
                                        _meshRotStartGazeDir = rayDir;
                                        _meshRotStartQuat    = _meshRotation;
                                        _isRotatingMesh      = true;
                                        _modelCommandMode     = ModelCommandMode.Direct;
                                        Debug.WriteLine("[MeshRotate] Started axis=" +
                                            (_meshRotationAxis == Vector3.UnitY ? "Y" : "X"));
                                    }
                                    else
                                    {
                                        // Start mesh drag — also hide trashcan
                                        _manipulationHandles.SetTrashcanVisible(false);
                                        var loc = pointerState.Properties.TryGetLocation(
                                                      stationaryReferenceFrame.CoordinateSystem);
                                        if (loc?.Position != null)
                                        {
                                            var hp = loc.Position.Value;
                                            _meshDragHandOffset = new Vector3(hp.X, hp.Y, hp.Z) - _meshPosition;
                                        }
                                        else
                                        {
                                            _meshDragHandOffset = Vector3.Zero;
                                        }
                                        _meshDragGazeDistance = meshHitDist;
                                        _isDraggingMesh = true;
                                        _modelCommandMode = ModelCommandMode.Direct;
                                        Debug.WriteLine("[MeshDrag] Started at dist=" + meshHitDist.ToString("F2"));
                                    }
                                }
                            }
                            else
                            {
                                // Check saved instances for 3D mesh hits
                                for (int i = 0; i < _productInstances.Count; i++)
                                {
                                    var inst = _productInstances[i];
                                    if (inst.MeshData != null)
                                    {
                                        float instHitDist;
                                        if (GazeHitsBox(rayOrigin, rayDir, inst.MeshPosition,
                                            inst.HalfExtents, 10f, out instHitDist))
                                        {
                                            // Tap on a saved instance mesh — show its delete dialog.
                                            ShowDeleteMeshDialog(i);
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        else if (_searchResultsDialog != null && _searchResultsDialog.IsVisible)
                        {
                            // Tap missed the product box — check search results dialog
                            Debug.WriteLine("[Input] Air-tap on search dialog");
                            _searchResultsDialog.HandleAirTap();
                        }
                        else if (_bookmarksDialog != null && _bookmarksDialog.IsVisible)
                        {
                            Debug.WriteLine("[Input] Air-tap on bookmarks dialog");
                            _bookmarksDialog.HandleAirTap();
                        }
                        else if (keyboardInputHandler.IsVisible)
                        {
                            // Tap missed the product box — route to keyboard.
                            Debug.WriteLine("[Input] Air-tap on keyboard (missed product)");
                            keyboardInputHandler.HandleAirTap();
                        }
                    }
                }
                else if (pose != null && !_isDraggingProduct && !_isRotating && !_isDraggingMesh && !_isRotatingMesh)
                {
                    if (_searchResultsDialog != null && _searchResultsDialog.IsVisible)
                    {
                        Debug.WriteLine("[Input] Air-tap on search dialog");
                        _searchResultsDialog.HandleAirTap();
                    }
                    else if (_bookmarksDialog != null && _bookmarksDialog.IsVisible)
                    {
                        Debug.WriteLine("[Input] Air-tap on bookmarks dialog");
                        _bookmarksDialog.HandleAirTap();
                    }
                    else if (keyboardInputHandler.IsVisible && pointerState != null)
                    {
                        Debug.WriteLine("[Input] Air-tap on keyboard");
                        keyboardInputHandler.HandleAirTap();
                    }
                    else if (appState != AppState.ShowingProduct)
                    {
                        spinningCubeRenderer.PositionHologram(pose);
                    }
                }

                // Drag update: follow hand (or gaze at fixed depth as fallback).
                var updatedState = spatialInputHandler.CheckForUpdate();

                // Bookmarks panel drag update: follow hand, or gaze at fixed depth.
                if (_isDraggingBookmarks && _bookmarksDialog != null)
                {
                    if (updatedState != null)
                    {
                        var loc = updatedState.Properties.TryGetLocation(stationaryReferenceFrame.CoordinateSystem);
                        if (loc?.Position != null)
                        {
                            var hp = loc.Position.Value;
                            _bookmarksDialog.SetPosition(new Vector3(hp.X, hp.Y, hp.Z) - _bookmarksDragHandOffset);
                        }
                        else if (headPose != null)
                        {
                            var gO = new Vector3(headPose.Head.Position.X, headPose.Head.Position.Y, headPose.Head.Position.Z);
                            var gD = new Vector3(headPose.Head.ForwardDirection.X, headPose.Head.ForwardDirection.Y, headPose.Head.ForwardDirection.Z);
                            _bookmarksDialog.SetPosition(gO + gD * _bookmarksDragGazeDistance);
                        }
                    }
                }

                if (_isDraggingProduct && updatedState != null)
                {
                    var loc = updatedState.Properties.TryGetLocation(
                                  stationaryReferenceFrame.CoordinateSystem);
                    if (loc?.Position != null)
                    {
                        var hp     = loc.Position.Value;
                        var newPos = new Vector3(hp.X, hp.Y, hp.Z) - _dragHandOffset;
                        _productPosition = newPos;
                        productBoxRenderer.SetPosition(newPos);
                        productSpriteRenderer.SetPosition(newPos);
                    }
                    else if (headPose != null)
                    {
                        // Fallback: maintain fixed depth along the current gaze ray.
                        var gO     = new Vector3(headPose.Head.Position.X,
                                                 headPose.Head.Position.Y,
                                                 headPose.Head.Position.Z);
                        var gD     = new Vector3(headPose.Head.ForwardDirection.X,
                                                 headPose.Head.ForwardDirection.Y,
                                                 headPose.Head.ForwardDirection.Z);
                        var newPos = gO + gD * _dragGazeDistance;
                        _productPosition = newPos;
                        productBoxRenderer.SetPosition(newPos);
                        productSpriteRenderer.SetPosition(newPos);
                    }
                }

                // Rotation update: map gaze azimuth/elevation delta to product rotation.
                if (_isRotating && headPose != null)
                {
                    var curDir = new Vector3(headPose.Head.ForwardDirection.X,
                                             headPose.Head.ForwardDirection.Y,
                                             headPose.Head.ForwardDirection.Z);
                    float delta;
                    if (_rotationAxis == Vector3.UnitY)
                    {
                        // Horizontal gaze movement → Y-axis spin.
                        float az0 = (float)Math.Atan2(_rotationStartGazeDir.X, -_rotationStartGazeDir.Z);
                        float az1 = (float)Math.Atan2(curDir.X, -curDir.Z);
                        delta = (az1 - az0) * 2.5f;
                    }
                    else
                    {
                        // Vertical gaze movement → X-axis tilt.
                        float el0 = (float)Math.Asin(Math.Max(-1f, Math.Min(1f, _rotationStartGazeDir.Y)));
                        float el1 = (float)Math.Asin(Math.Max(-1f, Math.Min(1f, curDir.Y)));
                        delta = (el1 - el0) * 2.5f;
                    }
                    _productRotation = Quaternion.Normalize(
                        _rotationStartQuat * Quaternion.CreateFromAxisAngle(_rotationAxis, delta));
                    productBoxRenderer.SetRotation(_productRotation);
                    productSpriteRenderer.SetRotation(_productRotation);
                }

                // Mesh drag update: follow hand or gaze at fixed depth.
                if (_isDraggingMesh && updatedState != null)
                {
                    var loc = updatedState.Properties.TryGetLocation(
                                  stationaryReferenceFrame.CoordinateSystem);
                    if (loc?.Position != null)
                    {
                        var hp = loc.Position.Value;
                        _meshPosition = new Vector3(hp.X, hp.Y, hp.Z) - _meshDragHandOffset;
                    }
                    else if (headPose != null)
                    {
                        var gO = new Vector3(headPose.Head.Position.X,
                                             headPose.Head.Position.Y,
                                             headPose.Head.Position.Z);
                        var gD = new Vector3(headPose.Head.ForwardDirection.X,
                                             headPose.Head.ForwardDirection.Y,
                                             headPose.Head.ForwardDirection.Z);
                        _meshPosition = gO + gD * _meshDragGazeDistance;
                    }
                }

                // Mesh rotation update: same gaze-based logic as product rotation.
                if (_isRotatingMesh && headPose != null)
                {
                    var curDir = new Vector3(headPose.Head.ForwardDirection.X,
                                             headPose.Head.ForwardDirection.Y,
                                             headPose.Head.ForwardDirection.Z);
                    float delta;
                    if (_meshRotationAxis == Vector3.UnitY)
                    {
                        float az0 = (float)Math.Atan2(_meshRotStartGazeDir.X, -_meshRotStartGazeDir.Z);
                        float az1 = (float)Math.Atan2(curDir.X, -curDir.Z);
                        delta = (az1 - az0) * 2.5f;
                    }
                    else
                    {
                        float el0 = (float)Math.Asin(Math.Max(-1f, Math.Min(1f, _meshRotStartGazeDir.Y)));
                        float el1 = (float)Math.Asin(Math.Max(-1f, Math.Min(1f, curDir.Y)));
                        delta = (el1 - el0) * 2.5f;
                    }
                    _meshRotation = Quaternion.Normalize(
                        _meshRotStartQuat * Quaternion.CreateFromAxisAngle(_meshRotationAxis, delta));
                }

                // Release: end drag or rotation and reset highlight.
                if (spatialInputHandler.CheckForRelease())
                {
                    if (_isDraggingBookmarks)
                    {
                        _isDraggingBookmarks = false;
                        Debug.WriteLine("[BookmarksDrag] Dropped");
                    }
                    if (_isDraggingProduct)
                    {
                        _isDraggingProduct = false;
                        Debug.WriteLine("[Drag] Dropped at " + _productPosition);
                    }
                    if (_isRotating)
                    {
                        _isRotating = false;
                        Debug.WriteLine("[Rotate] Released");
                    }
                    if (_isDraggingMesh)
                    {
                        _isDraggingMesh = false;
                        Debug.WriteLine("[MeshDrag] Dropped at " + _meshPosition);
                    }
                    if (_isRotatingMesh)
                    {
                        _isRotatingMesh = false;
                        Debug.WriteLine("[MeshRotate] Released");
                    }
                    _manipulationHandles.SetHighlight(ManipulationZone.None);
                }
            }
#endif

#if DRAW_SAMPLE_CONTENT
            // Poll for completed product load (safe to do from render thread).
            if (appState == AppState.Loading && pendingProductLoad != null && pendingProductLoad.IsCompleted)
            {
                if (pendingProductLoad.IsFaulted)
                {
                    Debug.WriteLine("Product load failed: " + pendingProductLoad.Exception?.GetBaseException()?.Message);
                    appState    = AppState.InputMode;
                    inputBuffer = "";
                }
                else
                {
                    var product = pendingProductLoad.Result;
                    _currentProduct = product;

                    // ── Save current product as a frozen instance before overwriting ──
                    if (_productDims.X > 0 && _productDims.Y > 0 && _productDims.Z > 0 &&
                        (!_activeProductRequiresMesh || _activeMeshData != null))
                    {
                        var instance = new ProductInstance
                        {
                            Position = _productPosition,
                            Rotation = _productRotation,
                            Dimensions = _productDims,
                            TextureSRV = _activeTextureSRV,
                            DisplacementSRV = _activeDispSRV,
                            SideFaceSRV = _activeSideSRV,
                            ViewType = _activeViewType,
                            MeshData = _activeMeshData,
                            MeshPosition = _meshPosition,
                            MeshRotation = _meshRotation,
                        };
                        instance.ContentBoundsVec = _activeContentBounds;
                        _productInstances.Add(instance);

                        // Transfer ownership to instance — don't dispose
                        _activeTextureSRV = null;
                        _activeDispSRV = null;
                        _activeSideSRV = null;
                        _activeMeshData = null;
                        _activeProductRequiresMesh = false;

                        Debug.WriteLine("[Multi] Saved product instance #" + _productInstances.Count);
                    }

                    // Position the box 2 m in front of the user's current gaze.
                    var boxPos = _lastHeadPosition + 2.0f * _lastHeadForward;
                    productBoxRenderer.SetPosition(boxPos);
                    productBoxRenderer.SetDimensions(product.WidthMeters, product.HeightMeters, product.DepthMeters);

                    // Reset active sprite textures for the new product (placeholder).
                    _activeTextureSRV?.Dispose();
                    _activeTextureSRV = null;
                    _activeDispSRV?.Dispose();
                    _activeDispSRV = null;
                    _activeSideSRV?.Dispose();
                    _activeSideSRV = null;
                    _activeContentBounds = new System.Numerics.Vector4(0f, 0f, 1f, 1f);
                    _activeViewType = ViewType.FrontOnly;

                    // Cache position and dimensions for drag hit-testing.
                    _productPosition = boxPos;
                    _productDims     = new Vector3(product.WidthMeters, product.HeightMeters, product.DepthMeters);

                    // Reset rotation for the new product.
                    _productRotation = Quaternion.Identity;
                    _isRotating      = false;
                    _modelCommandMode = ModelCommandMode.Direct;
                    productBoxRenderer.SetRotation(Quaternion.Identity);
                    productSpriteRenderer.SetRotation(Quaternion.Identity);
                    _manipulationHandles.SetHighlight(ManipulationZone.None);

                    // Keep the keyboard visible so the user can immediately look up another product.
                    keyboardInputHandler.ClearText();
                    inputBuffer = "";

                    appState = AppState.ShowingProduct;
                    Debug.WriteLine("Loaded: " + product.ProductName +
                        " W=" + product.WidthMeters + " H=" + product.HeightMeters + " D=" + product.DepthMeters);

                    // Resolve and download the GLB from the bookmarked IKEA page at runtime.
                    _activeMeshData = null;
                    _pending3DModelLoad = null;
                    _meshLoadFailed = false;
                    _activeProductRequiresMesh = product.Has3DModel;
                    // Try the bookmark's direct GLB URL first (fast, no page scraping).
                    // Fall back to scraping the product page for the model URL.
                    if (!string.IsNullOrEmpty(_pendingBookmarkGlbUrl))
                    {
                        Debug.WriteLine("[IKEA] Fetching 3D model from bookmark GlbUrl " + _pendingBookmarkGlbUrl);
                        _pending3DModelLoad = _modelService3D.FetchModelFromGlbUrlAsync(_pendingBookmarkGlbUrl, CancellationToken.None);
                    }
                    else if (product.Has3DModel && !string.IsNullOrEmpty(product.ModelUrl))
                    {
                        Debug.WriteLine("[IKEA] Fetching 3D model from product page " + product.ModelUrl);
                        _pending3DModelLoad = _modelService3D.FetchModelAsync(product.ModelUrl, CancellationToken.None);
                    }

                    // Start background image download + depth analysis.
                    if (!_activeProductRequiresMesh && !string.IsNullOrEmpty(product.ImageUrl))
                        StartImageLoad(product);
                }
                pendingProductLoad = null;
                StartNextBookmarkLoad();
            }

            // Poll for completed IKEA GLB model load.
            if (_pending3DModelLoad != null && _pending3DModelLoad.IsCompleted)
            {
                if (!_pending3DModelLoad.IsFaulted && _pending3DModelLoad.Result != null)
                {
                    _activeMeshData = _pending3DModelLoad.Result;
                    _gltfMeshRenderer.SetMeshData(_activeMeshData);
                    _meshDims = _activeMeshData.BoundsMeters;

                    // A bookmark's real mesh replaces the hidden product-card placeholder.
                    _meshPosition = _productPosition;
                    _meshRotation = _productRotation;

                    Debug.WriteLine("[IKEA] 3D model loaded: " + _activeMeshData.Positions.Length + " verts, " + (_activeMeshData.Indices.Length / 3) + " tris");
                    Debug.WriteLine("[IKEA] Mesh bounds (m): " + _meshDims.X.ToString("F3") + " x " + _meshDims.Y.ToString("F3") + " x " + _meshDims.Z.ToString("F3"));
                    Debug.WriteLine("[IKEA] Product dims (m): " + _productDims.X.ToString("F3") + " x " + _productDims.Y.ToString("F3") + " x " + _productDims.Z.ToString("F3"));
                }
                else if (_pending3DModelLoad.IsFaulted)
                {
                    Debug.WriteLine("[IKEA] 3D model load failed: " + _pending3DModelLoad.Exception?.GetBaseException()?.Message);
                    _meshLoadFailed = true;
                    // Fall back: load the product image for the placeholder box
                    if (_currentProduct != null && !string.IsNullOrEmpty(_currentProduct.ImageUrl))
                    {
                        StartImageLoad(_currentProduct);
                    }
                }
                else
                {
                    // Task completed but result was null (e.g. GLB parsing failed)
                    Debug.WriteLine("[IKEA] 3D model load returned null (parsing failed or no mesh)");
                    _meshLoadFailed = true;
                    if (_currentProduct != null && !string.IsNullOrEmpty(_currentProduct.ImageUrl))
                    {
                        StartImageLoad(_currentProduct);
                    }
                }
                _pending3DModelLoad = null;
                StartNextBookmarkLoad();
            }
#endif

            timer.Tick(() =>
            {
#if DRAW_SAMPLE_CONTENT
                // Apply any image/displacement SRVs that arrived from the background task.
                lock (_pendingImageLock)
                {
                    if (_pendingImageSRV != null)
                    {
                        _activeTextureSRV?.Dispose();
                        _activeTextureSRV = _pendingImageSRV;
                        _activeContentBounds = new System.Numerics.Vector4(
                            _pendingBounds.MinU, _pendingBounds.MinV,
                            _pendingBounds.MaxU, _pendingBounds.MaxV);
                        _pendingImageSRV = null;
                    }
                    if (_pendingDispSRV != null)
                    {
                        _activeDispSRV?.Dispose();
                        _activeDispSRV = _pendingDispSRV;
                        _pendingDispSRV = null;
                    }
                    if (_pendingSideSRV != null)
                    {
                        _activeSideSRV?.Dispose();
                        _activeSideSRV = _pendingSideSRV;
                        _activeViewType = _pendingViewType;
                        _pendingSideSRV = null;
                    }
                    else if (_pendingViewType == ViewType.FrontOnly)
                    {
                        _activeSideSRV?.Dispose();
                        _activeSideSRV = null;
                        _activeViewType = ViewType.FrontOnly;
                    }
                }

                if (appState == AppState.ShowingProduct)
                {
                    productBoxRenderer.Update(timer);
                    productSpriteRenderer.Update(timer);
                    var activePosition = _activeMeshData != null ? _meshPosition : _productPosition;
                    var activeDimensions = _activeMeshData != null ? _meshDims : _productDims;
                    var activeRotation = _activeMeshData != null ? _meshRotation : _productRotation;
                    _manipulationHandles.CommandBarVisible = _activeMeshData != null;
                    _manipulationHandles.SetTransform(activePosition, activeDimensions, activeRotation);
                    _manipulationHandles.Update();
                    // Update dimension labels to follow the visible product representation.
                    _dimensionLabels.SetDimensions(activeDimensions.X, activeDimensions.Y, activeDimensions.Z);
                    _dimensionLabels.SetPosition(activePosition);
                    _dimensionLabels.SetRotation(activeRotation);
                    _dimensionLabels.Update();
                }
                else
                    spinningCubeRenderer.Update(timer);
#endif
            });

            if (!canCommitDirect3D11DepthBuffer)
            {
                // On versions of the platform that do not support the CommitDirect3D11DepthBuffer API, we can control
                // image stabilization by setting a focus point with optional plane normal and velocity.
                foreach (var cameraPose in prediction.CameraPoses)
                {
#if DRAW_SAMPLE_CONTENT
                    // The HolographicCameraRenderingParameters class provides access to set
                    // the image stabilization parameters.
                    HolographicCameraRenderingParameters renderingParameters = holographicFrame.GetRenderingParameters(cameraPose);
 
                    // SetFocusPoint informs the system about a specific point in your scene to
                    // prioritize for image stabilization. The focus point is set independently
                    // for each holographic camera. When setting the focus point, put it on or 
                    // near content that the user is looking at.
                    // In this example, we put the focus point at the center of the sample hologram.
                    // You can also set the relative velocity and facing of the stabilization
                    // plane using overloads of this method.
                    if (stationaryReferenceFrame != null)
                    {
                        renderingParameters.SetFocusPoint(
                            stationaryReferenceFrame.CoordinateSystem,
                            spinningCubeRenderer.Position
                            );
                    }
#endif
                }
            }

            // The holographic frame will be used to get up-to-date view and projection matrices and
            // to present the swap chain.
            return holographicFrame;
        }

        /// <summary>
        /// Renders the current frame to each holographic display, according to the 
        /// current application and spatial positioning state. Returns true if the 
        /// frame was rendered to at least one display.
        /// </summary>
        public bool Render(HolographicFrame holographicFrame)
        {
            // Don't try to render anything before the first Update.
            if (timer.FrameCount == 0)
            {
                return false;
            }

            //
            // TODO: Add code for pre-pass rendering here.
            //
            // Take care of any tasks that are not specific to an individual holographic
            // camera. This includes anything that doesn't need the final view or projection
            // matrix, such as lighting maps.
            //

            // Up-to-date frame predictions enhance the effectiveness of image stablization and
            // allow more accurate positioning of holograms.
            holographicFrame.UpdateCurrentPrediction();
            HolographicFramePrediction prediction = holographicFrame.CurrentPrediction;

            // Lock the set of holographic camera resources, then draw to each camera
            // in this frame.
            return deviceResources.UseHolographicCameraResources(
                (Dictionary<uint, CameraResources> cameraResourceDictionary) =>
            {
                bool atLeastOneCameraRendered = false;

                foreach (var cameraPose in prediction.CameraPoses)
                {
                    // This represents the device-based resources for a HolographicCamera.
                    CameraResources cameraResources = cameraResourceDictionary[cameraPose.HolographicCamera.Id];

                    // Get the device context.
                    var context = deviceResources.D3DDeviceContext;
                    var renderTargetView = cameraResources.BackBufferRenderTargetView;
                    var depthStencilView = cameraResources.DepthStencilView;

                    // Set render targets to the current holographic camera.
                    context.OutputMerger.SetRenderTargets(depthStencilView, renderTargetView);

                    // Placeholder view-projection matrix (keyboard handler simplified version doesn't use it)
                    Matrix4x4 viewProjection = Matrix4x4.Identity;

                    // Clear the back buffer and depth stencil view.
                    if (canGetHolographicDisplayForCamera && 
                        cameraPose.HolographicCamera.Display.IsOpaque)
                    {
                        SharpDX.Mathematics.Interop.RawColor4 cornflowerBlue = new SharpDX.Mathematics.Interop.RawColor4(0.392156899f, 0.58431375f, 0.929411829f, 1.0f);
                        context.ClearRenderTargetView(renderTargetView, cornflowerBlue);
                    }
                    else
                    {
                        SharpDX.Mathematics.Interop.RawColor4 transparent = new SharpDX.Mathematics.Interop.RawColor4(0.0f, 0.0f, 0.0f, 0.0f);
                        context.ClearRenderTargetView(renderTargetView, transparent);
                    }
                    context.ClearDepthStencilView(
                        depthStencilView,
                        SharpDX.Direct3D11.DepthStencilClearFlags.Depth | SharpDX.Direct3D11.DepthStencilClearFlags.Stencil,
                        1.0f,
                        0);

                    //
                    // TODO: Replace the sample content with your own content.
                    //
                    // Notes regarding holographic content:
                    //    * For drawing, remember that you have the potential to fill twice as many pixels
                    //      in a stereoscopic render target as compared to a non-stereoscopic render target
                    //      of the same resolution. Avoid unnecessary or repeated writes to the same pixel,
                    //      and only draw holograms that the user can see.
                    //    * To help occlude hologram geometry, you can create a depth map using geometry
                    //      data obtained via the surface mapping APIs. You can use this depth map to avoid
                    //      rendering holograms that are intended to be hidden behind tables, walls,
                    //      monitors, and so on.
                    //    * On HolographicDisplays that are transparent, black pixels will appear transparent 
                    //      to the user. On such devices, you should clear the screen to Transparent as shown 
                    //      above. You should still use alpha blending to draw semitransparent holograms. 
                    //


                    // The view and projection matrices for each holographic camera will change
                    // every frame. This function refreshes the data in the constant buffer for
                    // the holographic camera indicated by cameraPose.
                    if (stationaryReferenceFrame != null)
                    {
                        cameraResources.UpdateViewProjectionBuffer(deviceResources, cameraPose, stationaryReferenceFrame.CoordinateSystem);
                    }

                    // Attach the view/projection constant buffer for this camera to the graphics pipeline.
                    bool cameraActive = cameraResources.AttachViewProjectionBuffer(deviceResources);

#if DRAW_SAMPLE_CONTENT
                    // Only render world-locked content when positional tracking is active.
                    if (cameraActive)
                    {
                        if (appState == AppState.ShowingProduct)
                        {
                            // Render previously saved product instances (box + sprite)
                            foreach (var inst in _productInstances)
                            {
                                // The box/sprite are a placeholder for products without a real
                                // GLB model (ProductServices.cs has no real dimensions/photo for
                                // them); once a 3D mesh is available it fully replaces the box.
                                if (inst.MeshData == null)
                                {
                                    productBoxRenderer.SetPosition(inst.Position);
                                    productBoxRenderer.SetDimensions(inst.Dimensions.X, inst.Dimensions.Y, inst.Dimensions.Z);
                                    productBoxRenderer.SetRotation(inst.Rotation);
                                    productBoxRenderer.Update(timer);  // upload transform to GPU
                                    productBoxRenderer.Render();

                                    // Render sprite for this instance if it has a texture
                                    if (inst.TextureSRV != null)
                                    {
                                        productSpriteRenderer.ApplyInstanceState(
                                            inst.Position, inst.Dimensions.X, inst.Dimensions.Y, inst.Dimensions.Z,
                                            inst.Rotation,
                                            inst.TextureSRV, inst.DisplacementSRV, inst.SideFaceSRV,
                                            inst.ContentBoundsVec, inst.ViewType);
                                        productSpriteRenderer.Update(timer);
                                        productSpriteRenderer.Render();
                                    }
                                }

                                // Render 3D mesh for this saved instance
                                if (inst.MeshData != null)
                                {
                                    _gltfMeshRenderer.SetMeshData(inst.MeshData);
                                    _gltfMeshRenderer.SetPosition(inst.MeshPosition);
                                    _gltfMeshRenderer.SetRotation(inst.MeshRotation);
                                    _gltfMeshRenderer.Update(timer);
                                    _gltfMeshRenderer.Render();
                                }
                            }

                            // Render the active (most recent) product
                            // Always show the placeholder box as a fallback while the
                            // 3D mesh loads, or if the mesh never arrives. The mesh
                            // replaces the box once _activeMeshData is set.
                            if (_activeMeshData == null && _productDims.X > 0f && _productDims.Y > 0f && _productDims.Z > 0f)
                            {
                                productBoxRenderer.SetPosition(_productPosition);
                                productBoxRenderer.SetDimensions(_productDims.X, _productDims.Y, _productDims.Z);
                                productBoxRenderer.SetRotation(_productRotation);
                                productBoxRenderer.Update(timer);  // upload transform to GPU
                                productBoxRenderer.Render();
                                // Render active product sprite with tracked SRVs
                                if (_activeTextureSRV != null)
                                {
                                    productSpriteRenderer.ApplyInstanceState(
                                        _productPosition, _productDims.X, _productDims.Y, _productDims.Z,
                                        _productRotation,
                                        _activeTextureSRV, _activeDispSRV, _activeSideSRV,
                                        _activeContentBounds, _activeViewType);
                                    productSpriteRenderer.Update(timer);
                                    productSpriteRenderer.Render();
                                }
                            }

                            // Render active 3D mesh at its independent position
                            if (_activeMeshData != null)
                            {
                                _gltfMeshRenderer.SetMeshData(_activeMeshData);
                                _gltfMeshRenderer.SetPosition(_meshPosition);
                                _gltfMeshRenderer.SetRotation(_meshRotation);
                                _gltfMeshRenderer.Update(timer);
                                _gltfMeshRenderer.Render();
                            }

                            _manipulationHandles.Render();  // edge rotation handles (only visible when gazing)
                            if (_activeMeshData != null || _productDims.X > 0f)
                                _dimensionLabels.Render();      // dimension labels in mm
                        }
                        else
                        {
                            // Even when not in ShowingProduct state, render saved instances
                            foreach (var inst in _productInstances)
                            {
                                if (inst.MeshData == null)
                                {
                                    productBoxRenderer.SetPosition(inst.Position);
                                    productBoxRenderer.SetDimensions(inst.Dimensions.X, inst.Dimensions.Y, inst.Dimensions.Z);
                                    productBoxRenderer.SetRotation(inst.Rotation);
                                    productBoxRenderer.Update(timer);  // upload transform to GPU
                                    productBoxRenderer.Render();

                                    if (inst.TextureSRV != null)
                                    {
                                        productSpriteRenderer.ApplyInstanceState(
                                            inst.Position, inst.Dimensions.X, inst.Dimensions.Y, inst.Dimensions.Z,
                                            inst.Rotation,
                                            inst.TextureSRV, inst.DisplacementSRV, inst.SideFaceSRV,
                                            inst.ContentBoundsVec, inst.ViewType);
                                        productSpriteRenderer.Update(timer);
                                        productSpriteRenderer.Render();
                                    }
                                }

                                // Render 3D mesh for saved instances even outside ShowingProduct
                                if (inst.MeshData != null)
                                {
                                    _gltfMeshRenderer.SetMeshData(inst.MeshData);
                                    _gltfMeshRenderer.SetPosition(inst.MeshPosition);
                                    _gltfMeshRenderer.SetRotation(inst.MeshRotation);
                                    _gltfMeshRenderer.Update(timer);
                                    _gltfMeshRenderer.Render();
                                }
                            }
                            spinningCubeRenderer.Render(); // loading / input mode indicator
                        }
                        // Render keyboard handler (overlay on top of cube/product)
                        if (keyboardInputHandler.IsVisible)
                        {
                            keyboardInputHandler.Render(context);
                        }
                        // Render search results dialog if visible
                        if (_searchResultsDialog != null && _searchResultsDialog.IsVisible)
                        {
                            _searchResultsDialog.Render();
                        }
                        // Render bookmarks dialog if visible
                        if (_bookmarksDialog != null && _bookmarksDialog.IsVisible)
                        {
                            _bookmarksDialog.Update();
                            _bookmarksDialog.Render();
                        }

                        if (canCommitDirect3D11DepthBuffer)
                        {
                            // On versions of the platform that support the CommitDirect3D11DepthBuffer API, we can 
                            // provide the depth buffer to the system, and it will use depth information to stabilize 
                            // the image at a per-pixel level.
                            HolographicCameraRenderingParameters renderingParameters = holographicFrame.GetRenderingParameters(cameraPose);
                            SharpDX.Direct3D11.Texture2D depthBuffer = cameraResources.DepthBufferTexture2D;

                            // Direct3D interop APIs are used to provide the buffer to the WinRT API.
                            SharpDX.DXGI.Resource1 depthStencilResource = depthBuffer.QueryInterface<SharpDX.DXGI.Resource1>();
                            SharpDX.DXGI.Surface2 depthDxgiSurface = new SharpDX.DXGI.Surface2(depthStencilResource, 0);
                            IDirect3DSurface depthD3DSurface = InteropStatics.CreateDirect3DSurface(depthDxgiSurface.NativePointer);
                            if (depthD3DSurface != null)
                            {
                                // Calling CommitDirect3D11DepthBuffer causes the system to queue Direct3D commands to 
                                // read the depth buffer. It will then use that information to stabilize the image as
                                // the HolographicFrame is presented.
                                renderingParameters.CommitDirect3D11DepthBuffer(depthD3DSurface);
                            }
                        }
                    }
#endif
                    atLeastOneCameraRendered = true;
                }

                return atLeastOneCameraRendered;
            });
        }

        public void SaveAppState()
        {
            //
            // TODO: Insert code here to save your app state.
            //       This method is called when the app is about to suspend.
            //
            //       For example, store information in the SpatialAnchorStore.
            //
        }

        public void LoadAppState()
        {
            //
            // TODO: Insert code here to load your app state.
            //       This method is called when the app resumes.
            //
            //       For example, load information from the SpatialAnchorStore.
            //
        }

        public void OnPointerPressed()
        {
            this.pointerPressed = true;
        }

        /// <summary>
        /// Notifies renderers that device resources need to be released.
        /// </summary>
        public void OnDeviceLost(Object sender, EventArgs e)
        {
#if DRAW_SAMPLE_CONTENT
            spinningCubeRenderer.ReleaseDeviceDependentResources();
            productBoxRenderer.ReleaseDeviceDependentResources();
            if (productSpriteRenderer != null)
                productSpriteRenderer.ReleaseDeviceDependentResources();
            if (keyboardInputHandler != null)
            {
                keyboardInputHandler.ReleaseDeviceDependentResources();
            }
            if (_manipulationHandles != null)
                _manipulationHandles.ReleaseDeviceDependentResources();
            if (_dimensionLabels != null)
                _dimensionLabels.ReleaseDeviceDependentResources();
#endif
        }

        /// <summary>
        /// Notifies renderers that device resources may now be recreated.
        /// </summary>
        public void OnDeviceRestored(Object sender, EventArgs e)
        {
#if DRAW_SAMPLE_CONTENT
            spinningCubeRenderer.CreateDeviceDependentResourcesAsync();
            productBoxRenderer.CreateDeviceDependentResourcesAsync();
            if (productSpriteRenderer != null)
                productSpriteRenderer.CreateDeviceDependentResourcesAsync();
            if (keyboardInputHandler != null)
            {
                keyboardInputHandler.CreateDeviceDependentResourcesAsync();
            }
            if (_manipulationHandles != null)
                _manipulationHandles.CreateDeviceDependentResourcesAsync();
            if (_dimensionLabels != null)
                _dimensionLabels.CreateDeviceDependentResourcesAsync();
#endif
        }

        /// <summary>Receives a typed character from AppView when in InputMode.
        /// HolographicKeyboard now owns the text buffer via CoreWindow.CharacterReceived,
        /// so this only handles the case where the system keyboard is not active.</summary>
        public void OnCharacterReceived(char c)
        {
            // HolographicKeyboard.CharacterReceived already fires for the same event
            // and calls TextChanged which sets inputBuffer. Nothing to do here.
        }

        /// <summary>Receives a key-down event from AppView for Enter / Backspace / Escape.</summary>
        public void OnKeyDown(Windows.System.VirtualKey key)
        {
            if (appState != AppState.InputMode && appState != AppState.ShowingProduct)
                return;

            if (key == Windows.System.VirtualKey.Enter || key == Windows.System.VirtualKey.Accept)
            {
                StartProductLoad();
            }
            else if (key == Windows.System.VirtualKey.Back)
            {
                if (inputBuffer.Length > 0)
                    inputBuffer = inputBuffer.Substring(0, inputBuffer.Length - 1);
            }
            else if (key == Windows.System.VirtualKey.Escape)
            {
                inputBuffer = "";
            }
        }

        /// <summary>
        /// Slab-method ray vs. AABB test.  Returns true when the ray hits the box within
        /// <paramref name="maxDist"/> metres and writes the entry distance to
        /// <paramref name="hitDist"/>.
        /// </summary>
        private static bool GazeHitsBox(Vector3 rayOrigin, Vector3 rayDir,
                                         Vector3 boxCenter, Vector3 halfExtents,
                                         float maxDist, out float hitDist)
        {
            hitDist = 0f;
            float tMin = 0f, tMax = maxDist;
            float[] o = { rayOrigin.X, rayOrigin.Y, rayOrigin.Z };
            float[] d = { rayDir.X,    rayDir.Y,    rayDir.Z    };
            float[] c = { boxCenter.X, boxCenter.Y, boxCenter.Z };
            float[] e = { halfExtents.X, halfExtents.Y, halfExtents.Z };
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(d[i]) < 1e-6f)
                {
                    if (o[i] < c[i] - e[i] || o[i] > c[i] + e[i]) return false;
                }
                else
                {
                    float t1 = (c[i] - e[i] - o[i]) / d[i];
                    float t2 = (c[i] + e[i] - o[i]) / d[i];
                    if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                    tMin = Math.Max(tMin, t1);
                    tMax = Math.Min(tMax, t2);
                    if (tMin > tMax) return false;
                }
            }
            hitDist = tMin;
            return true;
        }

        private void StartProductLoad()
        {
            var productUrl = inputBuffer == null ? "" : inputBuffer.Trim();
            if (!productUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !productUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine("Invalid IKEA product URL: '" + productUrl + "'");
                return;
            }
            Debug.WriteLine("Loading IKEA product page: " + productUrl);
            appState           = AppState.Loading;
            inputBuffer        = "";
            var cts            = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            pendingProductLoad = productRepository.GetProductAsync(productUrl, cts.Token);
        }

        /// <summary>
        /// Fires a search query and displays results in the search dialog.
        /// </summary>
        private void StartProductSearch(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || _productSearchService == null)
                return;

            Task.Run(async () =>
            {
                try
                {
                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var results = await _productSearchService.SearchAsync(query, 10, cts.Token);
                    Debug.WriteLine("[Search] Got " + results.Count + " results for: " + query);
                    if (results.Count > 0 && _searchResultsDialog != null)
                    {
                        // Position dialog near keyboard
                        var pos = keyboardInputHandler.IsVisible
                            ? _productPosition + new Vector3(0.4f, 0, 0)
                            : _productPosition;
                        _searchResultsDialog.Show(query, results, pos, Quaternion.Identity);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[Search] Error: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// Fire-and-forget: downloads the product image, runs depth analysis,
        /// uploads both textures to the GPU, then stores them for pickup by
        /// the render thread in Update().
        /// </summary>
        private void StartImageLoad(RenderableProduct product)
        {
            var url      = product.ImageUrl;
            var depthM   = product.DepthMeters;
            Task.Run(async () =>
            {
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try
                {
                    Debug.WriteLine("[Sprite] Downloading image: " + url);
                    var loadResult = await imageLoader.DownloadAndDecodeAsync(url, cts.Token)
                                                     .ConfigureAwait(false);
                    if (loadResult == null)
                    {
                        Debug.WriteLine("[Sprite] Image download failed.");
                        return;
                    }

                    // Depth displacement disabled: luminance-based pseudo-depth is unreliable for
                    // mechanical products and causes smearing artifacts on the additive HoloLens display.
                    SharpDX.Direct3D11.ShaderResourceView dispSrv = null;

                    Debug.WriteLine("[Sprite] Depth analysis skipped (displacement disabled).");

                    // Classify view angle and build per-face corrected textures
                    var classification = ProductViewClassifier.Classify(
                        loadResult.TightBgra, loadResult.Width, loadResult.Height);

                    // Front face always uses the full original image so the complete product is
                    // visible when looking straight on.  For 3/4-view images, additionally extract
                    // the side-panel portion and render it on the +X / -X box face for depth effect.
                    SharpDX.Direct3D11.ShaderResourceView frontSrv    = loadResult.Srv;
                    ContentBounds                          frontBounds = loadResult.Bounds;
                    SharpDX.Direct3D11.ShaderResourceView sideSrv     = null;

                    if (classification.ViewType != ViewType.FrontOnly)
                    {
                        var faceTextures = ProductFaceTextureBuilder.Build(
                            loadResult.TightBgra,
                            (int)loadResult.Width,
                            (int)loadResult.Height,
                            classification,
                            product.DepthMeters,
                            product.HeightMeters);

                        if (faceTextures.Side != null)
                            sideSrv = imageLoader.UploadBGRA(
                                faceTextures.Side.BgraPix,
                                (uint)faceTextures.Side.Width,
                                (uint)faceTextures.Side.Height);
                    }

                    // Hand all pending results to the render thread in one atomic assignment.
                    lock (_pendingImageLock)
                    {
                        _pendingImageSRV = frontSrv;
                        _pendingBounds   = frontBounds;
                        _pendingDispSRV  = dispSrv;
                        _pendingSideSRV  = sideSrv;
                        _pendingViewType = classification.ViewType;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[Sprite] Image load error: " + ex.Message);
                }
            });
        }

        // ShowVirtualKeyboard() replaced by holographicKeyboard.Show()

        void OnLocatabilityChanged(SpatialLocator sender, Object args)
        {
            switch (sender.Locatability)
            {
                case SpatialLocatability.Unavailable:
                    // Holograms cannot be rendered.
                    {
                        String message = "Warning! Positional tracking is " + sender.Locatability + ".";
                        Debug.WriteLine(message);
                    }
                    break;

                // In the following three cases, it is still possible to place holograms using a
                // SpatialLocatorAttachedFrameOfReference.
                case SpatialLocatability.PositionalTrackingActivating:
                // The system is preparing to use positional tracking.

                case SpatialLocatability.OrientationOnly:
                // Positional tracking has not been activated.

                case SpatialLocatability.PositionalTrackingInhibited:
                    // Positional tracking is temporarily inhibited. User action may be required
                    // in order to restore positional tracking.
                    break;

                case SpatialLocatability.PositionalTrackingActive:
                    // Positional tracking is active. World-locked content can be rendered.
                    break;
            }
        }

        public void OnCameraAdded(
            HolographicSpace sender,
            HolographicSpaceCameraAddedEventArgs args
            )
        {
            Deferral deferral = args.GetDeferral();
            HolographicCamera holographicCamera = args.Camera;

            Task task1 = new Task(() =>
            {
                //
                // TODO: Allocate resources for the new camera and load any content specific to
                //       that camera. Note that the render target size (in pixels) is a property
                //       of the HolographicCamera object, and can be used to create off-screen
                //       render targets that match the resolution of the HolographicCamera.
                //

                // Create device-based resources for the holographic camera and add it to the list of
                // cameras used for updates and rendering. Notes:
                //   * Since this function may be called at any time, the AddHolographicCamera function
                //     waits until it can get a lock on the set of holographic camera resources before
                //     adding the new camera. At 60 frames per second this wait should not take long.
                //   * A subsequent Update will take the back buffer from the RenderingParameters of this
                //     camera's CameraPose and use it to create the ID3D11RenderTargetView for this camera.
                //     Content can then be rendered for the HolographicCamera.
                deviceResources.AddHolographicCamera(holographicCamera);

                // Holographic frame predictions will not include any information about this camera until
                // the deferral is completed.
                deferral.Complete();
            });
            task1.Start();
        }

        public void OnCameraRemoved(
            HolographicSpace sender,
            HolographicSpaceCameraRemovedEventArgs args
            )
        {
            Task task2 = new Task(() =>
            {
                //
                // TODO: Asynchronously unload or deactivate content resources (not back buffer 
                //       resources) that are specific only to the camera that was removed.
                //
            });
            task2.Start();

            // Before letting this callback return, ensure that all references to the back buffer 
            // are released.
            // Since this function may be called at any time, the RemoveHolographicCamera function
            // waits until it can get a lock on the set of holographic camera resources before
            // deallocating resources for this camera. At 60 frames per second this wait should
            // not take long.
            deviceResources.RemoveHolographicCamera(args.Camera);
        }

        public void OnGamepadAdded(Object o, Gamepad args)
        {
            foreach (var gamepadWithButtonState in gamepads)
            {
                if (args == gamepadWithButtonState.gamepad)
                {
                    // This gamepad is already in the list.
                    return;
                }
            }

            gamepads.Add(new GamepadWithButtonState(args, false));
        }

        public void OnGamepadRemoved(Object o, Gamepad args)
        {
            foreach (var gamepadWithButtonState in gamepads)
            {
                if (args == gamepadWithButtonState.gamepad)
                {
                    // This gamepad is in the list; remove it.
                    gamepads.Remove(gamepadWithButtonState);
                    return;
                }
            }
        }

        void OnHolographicDisplayIsAvailableChanged(Object o, Object args)
        {
            // Get the spatial locator for the default HolographicDisplay, if one is available.
            SpatialLocator spatialLocator = null;
            if (canGetDefaultHolographicDisplay)
            {
                HolographicDisplay defaultHolographicDisplay = HolographicDisplay.GetDefault();
                if (defaultHolographicDisplay != null)
                {
                    spatialLocator = defaultHolographicDisplay.SpatialLocator;
                }
            }
            else
            {
                spatialLocator = SpatialLocator.GetDefault();
            }

            if (this.spatialLocator != spatialLocator)
            {
                // If the spatial locator is disconnected or replaced, we should discard any state that was
                // based on it.
                if (this.spatialLocator != null)
                {
                    this.spatialLocator.LocatabilityChanged -= this.OnLocatabilityChanged;
                    this.spatialLocator = null;
                }

                this.stationaryReferenceFrame = null;

                if (spatialLocator != null)
                {
                    // Use the SpatialLocator from the default HolographicDisplay to track the motion of the device.
                    this.spatialLocator = spatialLocator;

                    // Respond to changes in the positional tracking state.
                    this.spatialLocator.LocatabilityChanged += this.OnLocatabilityChanged;

                    // The simplest way to render world-locked holograms is to create a stationary reference frame
                    // based on a SpatialLocator. This is roughly analogous to creating a "world" coordinate system
                    // with the origin placed at the device's position as the app is launched.
                    this.stationaryReferenceFrame = this.spatialLocator.CreateStationaryFrameOfReferenceAtCurrentLocation();
                }
            }
        }

        // ── Voice input initialization ────────────────────────────────────────

        /// <summary>
        /// Initializes the speech recognition system and starts listening.
        /// Called from SetHolographicSpace after renderers are created.
        /// </summary>
        private async void InitializeSpeechAsync()
        {
            try
            {
                await _speechHandler.InitializeAsync();
                await _speechHandler.StartListeningAsync();
                Debug.WriteLine("[Speech] Voice input ready - type or dictate an IKEA product URL");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Speech] Initialization failed: " + ex.Message);
            }
        }

        // ── Multi-product support ─────────────────────────────────────────────
        /// <summary>
        /// Clears all products from the scene.
        /// </summary>
        private void ClearAllProducts()
        {
            foreach (var inst in _productInstances)
            {
                inst.DisposeTextures();
            }
            _productInstances.Clear();
            _productDims = Vector3.Zero;
            _bookmarkLoadQueue.Clear();
            _currentProduct = null;
            _meshLoadFailed = false;
            _pendingBookmarkGlbUrl = "";

            // Dispose active product textures
            _activeTextureSRV?.Dispose(); _activeTextureSRV = null;
            _activeDispSRV?.Dispose();    _activeDispSRV = null;
            _activeSideSRV?.Dispose();    _activeSideSRV = null;
            
            // Reset to input mode
            appState = AppState.InputMode;
            inputBuffer = "";
            ShowBookmarksDialog();

            Debug.WriteLine("[Multi] All products cleared");
        }

        private void QueueBookmarkProductLoad(Bookmark bookmark)
        {
            if (bookmark == null || string.IsNullOrWhiteSpace(bookmark.Url))
                return;

            _bookmarkLoadQueue.Enqueue(bookmark);
            StartNextBookmarkLoad();
        }

        private void StartNextBookmarkLoad()
        {
            if (_bookmarkLoadQueue.Count == 0 || pendingProductLoad != null ||
                _pending3DModelLoad != null || appState == AppState.Loading)
                return;

            var bookmark = _bookmarkLoadQueue.Dequeue();
            _pendingBookmarkGlbUrl = bookmark.GlbUrl ?? "";
            inputBuffer = bookmark.Url;
            StartProductLoad();
        }

        // ── Bookmarks support ───────────────────────────────────────────────

        /// <summary>
        /// Loads bookmarks from the embedded bookmarks.json file.
        /// </summary>
        private async void LoadBookmarksAsync()
        {
            if (_bookmarksLoading) return;
            _bookmarksLoading = true;
            
            try
            {
                await _bookmarksService.LoadAsync();
                Debug.WriteLine($"[Bookmarks] Loaded {_bookmarksService.Count} bookmarks");
                // Show the bookmarks list as the default landing screen so the
                // curated IKEA products are visible without a voice command.
                ShowBookmarksDialog();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Bookmarks] Error loading: {ex.Message}");
            }
            finally
            {
                _bookmarksLoading = false;
            }
        }

        /// <summary>
        /// Shows the bookmarks dialog with all available bookmarks.
        /// </summary>
        private void ShowBookmarksDialog()
        {
            if (_bookmarksService == null || _bookmarksService.Count == 0)
            {
                Debug.WriteLine("[Bookmarks] No bookmarks available");
                return;
            }

            var bookmarks = new List<HololensIKEA.Models.Bookmark>(_bookmarksService.Bookmarks);
            _bookmarksDialog.Show(bookmarks);
            Debug.WriteLine($"[Bookmarks] Showing {bookmarks.Count} bookmarks");
        }

        /// <summary>
        /// Searches bookmarks and shows the dialog with results.
        /// </summary>
        private void ShowBookmarksSearch(string query)
        {
            if (_bookmarksService == null)
            {
                Debug.WriteLine("[Bookmarks] Service not loaded yet");
                return;
            }

            var results = _bookmarksService.Search(query);
            if (results.Count > 0)
            {
                _bookmarksDialog.Show(results);
                Debug.WriteLine($"[Bookmarks] Showing {results.Count} results for '{query}'");
            }
            else
            {
                Debug.WriteLine($"[Bookmarks] No results for '{query}'");
            }
        }

        // ── Delete mesh confirmation dialog ─────────────────────────────────

        /// <summary>
        /// Shows a native Windows UWP confirmation dialog asking whether to delete
        /// the 3D model at the given instance index. Pass -1 to target the active mesh.
        /// </summary>
        private async void ShowDeleteMeshDialog(int instanceIndex)
        {
            if (_deleteDialogShowing)
                return;
            _deleteDialogShowing = true;
            _deleteTargetInstanceIndex = instanceIndex;

            var productName = instanceIndex >= 0
                ? (_productInstances[instanceIndex].Product?.ProductName ?? "Product")
                : "Product";

            var dialog = new MessageDialog(
                $"Delete {productName} from the scene?",
                "Delete 3D Model");
            dialog.Commands.Add(new UICommand("Yes", async (cmd) => await ConfirmDeleteMeshAsync()));
            dialog.Commands.Add(new UICommand("No",  async (cmd) => { _deleteDialogShowing = false; }));
            dialog.DefaultCommandIndex = 1;  // No = default
            dialog.CancelCommandIndex  = 1;

            try
            {
                var result = await dialog.ShowAsync();
                // Handled in the "Yes" callback above.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Delete] Dialog error: {ex.Message}");
            }
            finally
            {
                // Reset flag if the "No" callback already did (it sets it false).
                _deleteDialogShowing = false;
            }
        }

        private async Task ConfirmDeleteMeshAsync()
        {
            _deleteDialogShowing = false;
            int idx = _deleteTargetInstanceIndex;
            _deleteTargetInstanceIndex = -1;

            Debug.WriteLine($"[Delete] Confirming delete, instanceIndex={idx}");

            if (idx == -1)
            {
                // Delete the active (most recent) mesh.
                // Revert to box+sprite state for the current product.
                _activeMeshData = null;
                _activeProductRequiresMesh = false;
                _pending3DModelLoad = null;  // prevent re-loading after delete
                _meshPosition = _productPosition;
                _meshRotation = _productRotation;
                _meshDims = Vector3.Zero;
                // Hide the placeholder box — it was only a stand-in for the mesh.
                _productDims = Vector3.Zero;
                _isDraggingMesh = false;
                _isRotatingMesh = false;
                _manipulationHandles.SetHighlight(ManipulationZone.None);
                _manipulationHandles.CommandBarVisible = false;
                _activeTextureSRV?.Dispose(); _activeTextureSRV = null;
                _activeDispSRV?.Dispose();    _activeDispSRV = null;
                _activeSideSRV?.Dispose();    _activeSideSRV = null;
                _currentProduct = null;
                _pendingBookmarkGlbUrl = "";

                // Nothing is being shown anymore — return to the bookmarks list so
                // the user can immediately pick another product.
                appState = AppState.InputMode;
                inputBuffer = "";
                ShowBookmarksDialog();
                Debug.WriteLine("[Delete] Active mesh deleted — returning to bookmarks");
            }
            else if (idx >= 0 && idx < _productInstances.Count)
            {
                var inst = _productInstances[idx];
                inst.MeshData = null;
                Debug.WriteLine($"[Delete] Instance #{idx + 1} 3D model removed ({_productInstances.Count} instances remain)");
            }
        }
    }
}
