using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using Camera2Forms.Droid;
using Java.Lang;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Size = Android.Util.Size;

namespace Camera2Forms.Camera2
{
    public class CameraDroid : FrameLayout, TextureView.ISurfaceTextureListener
    {
        #region Camera States

        // Camera state: Showing camera preview.
        public const int STATE_PREVIEW = 0;

        // Camera state: Waiting for the focus to be locked.
        public const int STATE_WAITING_LOCK = 1;

        // Camera state: Waiting for the exposure to be precapture state.
        public const int STATE_WAITING_PRECAPTURE = 2;

        //Camera state: Waiting for the exposure state to be something other than precapture.
        public const int STATE_WAITING_NON_PRECAPTURE = 3;

        // Camera state: Picture was taken.
        public const int STATE_PICTURE_TAKEN = 4;

        #endregion

        // The current state of camera state for taking pictures.
        public int mState = STATE_PREVIEW;

        private static readonly SparseIntArray Orientations = new SparseIntArray();

        public event EventHandler<byte[]> Photo;

        public bool OpeningCamera { private get; set; }

        public CameraDevice CameraDevice;

        private readonly CameraStateListener _cameraStateListener;
        private readonly CameraCaptureListener _cameraCaptureListener;

        private CaptureRequest.Builder _previewBuilder;
        private CaptureRequest.Builder _captureBuilder;
        private CaptureRequest _previewRequest;
        private CameraCaptureSession _previewSession;
        private SurfaceTexture _viewSurface;
        private readonly TextureView _cameraTexture;
        private Size _previewSize;
        private readonly Context _context;
        private CameraManager _manager;

        private bool _flashSupported;
        private Size[] _supportedJpegSizes;
        private Size _idealPhotoSize = new Size(480, 640);

        private HandlerThread _backgroundThread;
        private Handler _backgroundHandler;

        private ImageReader _imageReader;
        private string _cameraId;

        public CameraDroid(Context context) : base(context)
        {
            _context = context;

            var inflater = LayoutInflater.FromContext(context);

            if (inflater == null) return;
            var view = inflater.Inflate(Resource.Layout.CameraLayout, this);

            _cameraTexture = view.FindViewById<TextureView>(Resource.Id.cameraTexture);

            _cameraTexture.SurfaceTextureListener = this;

            _cameraStateListener = new CameraStateListener { Camera = this };

            _cameraCaptureListener = new CameraCaptureListener(this);
        }

        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
        {
            _viewSurface = surface;

            StartBackgroundThread();

            OpenCamera(width, height);
        }

        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
        {
            StopBackgroundThread();

            return true;
        }

        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height)
        {

        }

        public void OnSurfaceTextureUpdated(SurfaceTexture surface)
        {

        }

        private void SetUpCameraOutputs(int width, int height)
        {
            _manager = (CameraManager)_context.GetSystemService(Context.CameraService);

            string[] cameraIds = _manager.GetCameraIdList();

            _cameraId = cameraIds[0];

            for (int i = 0; i < cameraIds.Length; i++)
            {
                CameraCharacteristics chararc = _manager.GetCameraCharacteristics(cameraIds[i]);

                var facing = (Integer)chararc.Get(CameraCharacteristics.LensFacing);
                if (facing != null && facing == (Integer.ValueOf((int)LensFacing.Front)))
                    continue;

                _cameraId = cameraIds[i];
            }

            var characteristics = _manager.GetCameraCharacteristics(_cameraId);
            var map = (StreamConfigurationMap)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap);

            if (_supportedJpegSizes == null && characteristics != null)
            {
                _supportedJpegSizes = ((StreamConfigurationMap)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap)).GetOutputSizes((int)ImageFormatType.Jpeg);
            }

            if (_supportedJpegSizes != null && _supportedJpegSizes.Length > 0)
            {
                _idealPhotoSize = GetOptimalSize(_supportedJpegSizes, 1050, 1400); //MAGIC NUMBER WHICH HAS PROVEN TO BE THE BEST
            }

            _imageReader = ImageReader.NewInstance(_idealPhotoSize.Width, _idealPhotoSize.Height, ImageFormatType.Jpeg, 1);

            var readerListener = new ImageAvailableListener();

            readerListener.Photo += (sender, buffer) =>
            {
                Photo?.Invoke(this, buffer);
            };

            var available = (Java.Lang.Boolean)characteristics.Get(CameraCharacteristics.FlashInfoAvailable);
            if (available == null)
            {
                _flashSupported = false;
            }
            else
            {
                _flashSupported = (bool)available;
            }

            _imageReader.SetOnImageAvailableListener(readerListener, _backgroundHandler);

            _previewSize = GetOptimalSize(map.GetOutputSizes(Class.FromType(typeof(SurfaceTexture))), width, height);
        }

        public void OpenCamera(int width, int height)
        {
            if (_context == null || OpeningCamera)
            {
                return;
            }

            OpeningCamera = true;

            SetUpCameraOutputs(width, height);

            _manager.OpenCamera(_cameraId, _cameraStateListener, null);
        }

        public void TakePhoto()
        {
            if (_context == null || CameraDevice == null) return;

            if (_captureBuilder == null)
                _captureBuilder = CameraDevice.CreateCaptureRequest(CameraTemplate.StillCapture);

            _captureBuilder.AddTarget(_imageReader.Surface);

            _captureBuilder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.ContinuousPicture);
            SetAutoFlash(_captureBuilder);

            _previewSession.StopRepeating();
            _previewSession.Capture(_captureBuilder.Build(),
                new CameraCaptureStillPictureSessionCallback
            {
                OnCaptureCompletedAction = session =>
                {
                    UnlockFocus();
                }
            }, null);
        }

        public void StartPreview()
        {
            if (CameraDevice == null || !_cameraTexture.IsAvailable || _previewSize == null) return;

            var texture = _cameraTexture.SurfaceTexture;

            texture.SetDefaultBufferSize(_previewSize.Width, _previewSize.Height);

            var surface = new Surface(texture);

            _previewBuilder = CameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
            _previewBuilder.AddTarget(surface);

            List<Surface> surfaces = new List<Surface>();
            surfaces.Add(surface);
            surfaces.Add(_imageReader.Surface);

            CameraDevice.CreateCaptureSession(surfaces,
                new CameraCaptureStateListener
                {
                    OnConfigureFailedAction = session =>
                    {
                    },
                    OnConfiguredAction = session =>
                    {
                        _previewSession = session;
                        UpdatePreview();
                    }
                },
                _backgroundHandler);
        }

        private void UpdatePreview()
        {
            if (CameraDevice == null || _previewSession == null) return;

            // Reset the auto-focus trigger
            _previewBuilder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.ContinuousPicture);
            SetAutoFlash(_previewBuilder);

            _previewRequest = _previewBuilder.Build();
            _previewSession.SetRepeatingRequest(_previewRequest, _cameraCaptureListener, _backgroundHandler);
        }

        Size GetOptimalSize(IList<Size> sizes, int h, int w)
        {
            double AspectTolerance = 0.1;
            double targetRatio = (double)w / h;

            if (sizes == null)
            {
                return null;
            }

            Size optimalSize = null;
            double minDiff = double.MaxValue;
            int targetHeight = h;

            while (optimalSize == null)
            {
                foreach (Size size in sizes)
                {
                    double ratio = (double)size.Width / size.Height;

                    if (System.Math.Abs(ratio - targetRatio) > AspectTolerance)
                        continue;
                    if (System.Math.Abs(size.Height - targetHeight) < minDiff)
                    {
                        optimalSize = size;
                        minDiff = System.Math.Abs(size.Height - targetHeight);
                    }
                }

                if (optimalSize == null)
                    AspectTolerance += 0.1f;
            }

            return optimalSize;
        }

        public void SetAutoFlash(CaptureRequest.Builder requestBuilder)
        {
            if (_flashSupported)
            {
                requestBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.OnAutoFlash);
            }
        }

        private void StartBackgroundThread()
        {
            _backgroundThread = new HandlerThread("CameraBackground");
            _backgroundThread.Start();
            _backgroundHandler = new Handler(_backgroundThread.Looper);
        }

        private void StopBackgroundThread()
        {
            _backgroundThread.QuitSafely();
            try
            {
                _backgroundThread.Join();
                _backgroundThread = null;
                _backgroundHandler = null;
            }
            catch (InterruptedException e)
            {
                e.PrintStackTrace();
            }
        }

        public void LockFocus()
        {
            try
            {
                // This is how to tell the camera to lock focus.
                _previewBuilder.Set(CaptureRequest.ControlAfTrigger, (int)ControlAFTrigger.Start);
                // Tell #mCaptureCallback to wait for the lock.
                mState = STATE_WAITING_LOCK;
                _previewSession.Capture(_previewBuilder.Build(), _cameraCaptureListener, _backgroundHandler);
            }
            catch (CameraAccessException e)
            {
                e.PrintStackTrace();
            }
        }

        public void UnlockFocus()
        {
            try
            {
                // Reset the auto-focus trigger
                _previewBuilder.Set(CaptureRequest.ControlAfTrigger, (int)ControlAFTrigger.Cancel);
                SetAutoFlash(_previewBuilder);

                _previewSession.Capture(_previewBuilder.Build(), _cameraCaptureListener, _backgroundHandler);
                // After this, the camera will go back to the normal state of preview.
                mState = STATE_PREVIEW;
                _previewSession.SetRepeatingRequest(_previewRequest, _cameraCaptureListener, _backgroundHandler);
            }
            catch (CameraAccessException e)
            {
                e.PrintStackTrace();
            }
        }

        public void RunPrecaptureSequence()
        {
            try
            {
                _previewBuilder.Set(CaptureRequest.ControlAePrecaptureTrigger, (int)ControlAEPrecaptureTrigger.Start);
                mState = STATE_WAITING_PRECAPTURE;
                _previewSession.Capture(_previewBuilder.Build(), _cameraCaptureListener, _backgroundHandler);
            }
            catch (CameraAccessException e)
            {
                e.PrintStackTrace();
            }
        }
    }
}
