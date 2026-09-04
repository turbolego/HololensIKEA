using System;
using System.Diagnostics;
using System.Numerics;

namespace HololensIKEA.Services
{
    /// <summary>Output of a single-face unwarp operation.</summary>
    public sealed class FaceTextureData
    {
        public byte[] BgraPix { get; }
        public int    Width   { get; }
        public int    Height  { get; }

        public FaceTextureData(byte[] pix, int w, int h)
        { BgraPix = pix; Width = w; Height = h; }
    }

    /// <summary>All face textures extracted from one product image.</summary>
    public sealed class MultiFaceTextures
    {
        /// <summary>Perspective-corrected front-face texture. Always present.</summary>
        public FaceTextureData Front { get; set; }

        /// <summary>Perspective-corrected side-face texture. Null for FrontOnly images.</summary>
        public FaceTextureData Side  { get; set; }

        /// <summary>Which side the Side texture belongs to.</summary>
        public ViewType ViewType { get; set; } = ViewType.FrontOnly;
    }

    /// <summary>
    /// Performs per-face CPU-side perspective correction on a product image.
    ///
    /// Uses a full projective homography (DLT) to unwarp each detected quad
    /// region into a clean rectangle.  Side-face width is set from the product's
    /// real depth:height ratio so it maps exactly to the physical box face.
    /// </summary>
    public static class ProductFaceTextureBuilder
    {
        /// <summary>Maximum output texture dimension per axis. Keep ≤ 1024 for HoloLens 1.</summary>
        public const int MaxTexSize = 512;

        public static MultiFaceTextures Build(
            byte[]             srcBgra,
            int                srcWidth,
            int                srcHeight,
            ViewClassification classification,
            float              productDepthM  = 0.1f,
            float              productHeightM = 0.1f)
        {
            var result = new MultiFaceTextures { ViewType = classification.ViewType };

            // Null or zero-sized input can never produce a usable texture.
            // Return an empty result instead of falling through to the
            // exception path, which would emit a 0x0 FaceTextureData.
            if (srcBgra == null || srcBgra.Length == 0 || srcWidth <= 0 || srcHeight <= 0)
            {
                return result;
            }

            try
            {
                // ── Front face ─────────────────────────────────────────────
                int frontW = Math.Min(MaxTexSize, srcWidth);
                int frontH = Math.Min(MaxTexSize, srcHeight);

                float frontAspect = classification.FrontFace.MidWidth
                                  / Math.Max(1f, classification.FrontFace.MidHeight);

                if (frontAspect >= 1f)
                    frontH = Math.Max(1, (int)(frontW / frontAspect));
                else
                    frontW = Math.Max(1, (int)(frontH * frontAspect));

                frontW = Math.Min(frontW, MaxTexSize);
                frontH = Math.Min(frontH, MaxTexSize);

                result.Front = UnwarpFace(srcBgra, srcWidth, srcHeight,
                    classification.FrontFace, frontW, frontH);

                // ── Side face (3/4 views only) ─────────────────────────────
                if (classification.ViewType == ViewType.ThreeQuarterRight ||
                    classification.ViewType == ViewType.ThreeQuarterLeft)
                {
                    float depthHeightRatio = productDepthM / Math.Max(0.001f, productHeightM);
                    int   sideH = frontH;
                    int   sideW = Math.Max(1, Math.Min(MaxTexSize, (int)(sideH * depthHeightRatio)));

                    result.Side = UnwarpFace(srcBgra, srcWidth, srcHeight,
                        classification.SideFace, sideW, sideH);

                    Debug.WriteLine("[FaceTexBuilder] side " + sideW + "×" + sideH +
                        "  (depth:height=" + depthHeightRatio.ToString("F2") + ")");
                }

                Debug.WriteLine("[FaceTexBuilder] front " + frontW + "×" + frontH +
                    "  viewType=" + classification.ViewType);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[FaceTexBuilder] " + ex.Message);
                if (result.Front == null)
                    result.Front = CropEntireImage(srcBgra, srcWidth, srcHeight);
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Core: projective quad → rect unwarp
        // ─────────────────────────────────────────────────────────────────────

        private static FaceTextureData UnwarpFace(
            byte[] src, int srcW, int srcH,
            FaceQuad quad,
            int dstW, int dstH)
        {
            var srcPts = new Vector2[] { quad.TL, quad.TR, quad.BL, quad.BR };
            var dstPts = new Vector2[]
            {
                new Vector2(0,        0       ),
                new Vector2(dstW - 1, 0       ),
                new Vector2(0,        dstH - 1),
                new Vector2(dstW - 1, dstH - 1),
            };

            double[] H    = ComputeHomography(srcPts, dstPts);
            if (H == null)    return CropQuad(src, srcW, srcH, quad, dstW, dstH);
            double[] Hinv = Invert3x3(H);
            if (Hinv == null) return CropQuad(src, srcW, srcH, quad, dstW, dstH);

            var dst = new byte[dstW * dstH * 4];

            for (int dy = 0; dy < dstH; ++dy)
            for (int dx = 0; dx < dstW; ++dx)
            {
                double w_ = Hinv[6] * dx + Hinv[7] * dy + Hinv[8];
                if (Math.Abs(w_) < 1e-9) continue;
                double sx = (Hinv[0] * dx + Hinv[1] * dy + Hinv[2]) / w_;
                double sy = (Hinv[3] * dx + Hinv[4] * dy + Hinv[5]) / w_;

                SampleBilinear(src, srcW, srcH, sx, sy,
                    out byte rb, out byte gb, out byte bb, out byte ab);

                int dstIdx = (dy * dstW + dx) * 4;
                dst[dstIdx]     = bb;
                dst[dstIdx + 1] = gb;
                dst[dstIdx + 2] = rb;
                dst[dstIdx + 3] = ab;
            }

            return new FaceTextureData(dst, dstW, dstH);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Homography (8-point DLT with Gaussian elimination)
        // ─────────────────────────────────────────────────────────────────────

        private static double[] ComputeHomography(Vector2[] src, Vector2[] dst)
        {
            var A = new double[8, 8];
            var b = new double[8];

            for (int i = 0; i < 4; ++i)
            {
                double x = src[i].X, y = src[i].Y;
                double u = dst[i].X, v = dst[i].Y;

                int r0 = i * 2;
                A[r0, 0] = x;  A[r0, 1] = y;  A[r0, 2] = 1;
                A[r0, 3] = 0;  A[r0, 4] = 0;  A[r0, 5] = 0;
                A[r0, 6] = -x * u; A[r0, 7] = -y * u;
                b[r0] = u;

                int r1 = r0 + 1;
                A[r1, 0] = 0;  A[r1, 1] = 0;  A[r1, 2] = 0;
                A[r1, 3] = x;  A[r1, 4] = y;  A[r1, 5] = 1;
                A[r1, 6] = -x * v; A[r1, 7] = -y * v;
                b[r1] = v;
            }

            if (!GaussianEliminate(A, b, 8, out double[] sol))
                return null;

            return new double[]
            {
                sol[0], sol[1], sol[2],
                sol[3], sol[4], sol[5],
                sol[6], sol[7], 1.0
            };
        }

        private static bool GaussianEliminate(double[,] A, double[] b, int n, out double[] x)
        {
            x = new double[n];
            for (int col = 0; col < n; ++col)
            {
                int pivot = col;
                for (int row = col + 1; row < n; ++row)
                    if (Math.Abs(A[row, col]) > Math.Abs(A[pivot, col]))
                        pivot = row;

                if (pivot != col)
                {
                    for (int k = 0; k < n; ++k)
                    { double tmp = A[col, k]; A[col, k] = A[pivot, k]; A[pivot, k] = tmp; }
                    { double tmp = b[col];    b[col]    = b[pivot];    b[pivot]    = tmp; }
                }

                if (Math.Abs(A[col, col]) < 1e-12) return false;

                for (int row = col + 1; row < n; ++row)
                {
                    double f = A[row, col] / A[col, col];
                    for (int k = col; k < n; ++k) A[row, k] -= f * A[col, k];
                    b[row] -= f * b[col];
                }
            }
            for (int row = n - 1; row >= 0; --row)
            {
                x[row] = b[row];
                for (int k = row + 1; k < n; ++k) x[row] -= A[row, k] * x[k];
                x[row] /= A[row, row];
            }
            return true;
        }

        private static double[] Invert3x3(double[] m)
        {
            double a = m[0], b = m[1], c = m[2];
            double d = m[3], e = m[4], f = m[5];
            double g = m[6], h = m[7], k = m[8];

            double det = a * (e * k - f * h) - b * (d * k - f * g) + c * (d * h - e * g);
            if (Math.Abs(det) < 1e-12) return null;
            double inv = 1.0 / det;

            return new double[]
            {
                (e*k - f*h) * inv, (c*h - b*k) * inv, (b*f - c*e) * inv,
                (f*g - d*k) * inv, (a*k - c*g) * inv, (c*d - a*f) * inv,
                (d*h - e*g) * inv, (b*g - a*h) * inv, (a*e - b*d) * inv,
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // Bilinear sampler
        // ─────────────────────────────────────────────────────────────────────

        private static void SampleBilinear(byte[] src, int srcW, int srcH,
            double sx, double sy,
            out byte rb, out byte gb, out byte bb, out byte ab)
        {
            sx = Math.Max(0, Math.Min(srcW - 1.001, sx));
            sy = Math.Max(0, Math.Min(srcH - 1.001, sy));

            int x0 = (int)sx, y0 = (int)sy;
            int x1 = Math.Min(x0 + 1, srcW - 1);
            int y1 = Math.Min(y0 + 1, srcH - 1);
            float tx = (float)(sx - x0), ty = (float)(sy - y0);

            int i00 = (y0 * srcW + x0) * 4;
            int i10 = (y0 * srcW + x1) * 4;
            int i01 = (y1 * srcW + x0) * 4;
            int i11 = (y1 * srcW + x1) * 4;

            bb = Lerp2(src[i00],   src[i10],   src[i01],   src[i11],   tx, ty);
            gb = Lerp2(src[i00+1], src[i10+1], src[i01+1], src[i11+1], tx, ty);
            rb = Lerp2(src[i00+2], src[i10+2], src[i01+2], src[i11+2], tx, ty);
            ab = Lerp2(src[i00+3], src[i10+3], src[i01+3], src[i11+3], tx, ty);
        }

        private static byte Lerp2(byte c00, byte c10, byte c01, byte c11, float tx, float ty)
        {
            float v = c00 * (1 - tx) * (1 - ty)
                    + c10 * tx       * (1 - ty)
                    + c01 * (1 - tx) * ty
                    + c11 * tx       * ty;
            return (byte)Math.Max(0, Math.Min(255, (int)(v + 0.5f)));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Fallbacks
        // ─────────────────────────────────────────────────────────────────────

        private static FaceTextureData CropQuad(
            byte[] src, int srcW, int srcH,
            FaceQuad quad, int dstW, int dstH)
        {
            int x0 = (int)Math.Max(0, Math.Min(quad.TL.X, quad.BL.X));
            int y0 = (int)Math.Max(0, Math.Min(quad.TL.Y, quad.TR.Y));
            int x1 = (int)Math.Min(srcW - 1, Math.Max(quad.TR.X, quad.BR.X));
            int y1 = (int)Math.Min(srcH - 1, Math.Max(quad.BL.Y, quad.BR.Y));

            int cropW = Math.Max(1, x1 - x0 + 1);
            int cropH = Math.Max(1, y1 - y0 + 1);

            var cropped = new byte[cropW * cropH * 4];
            for (int cy = 0; cy < cropH; ++cy)
            for (int cx = 0; cx < cropW; ++cx)
            {
                int si = ((y0 + cy) * srcW + (x0 + cx)) * 4;
                int di = (cy * cropW + cx) * 4;
                cropped[di] = src[si]; cropped[di+1] = src[si+1];
                cropped[di+2] = src[si+2]; cropped[di+3] = src[si+3];
            }
            return NNResize(cropped, cropW, cropH, dstW, dstH);
        }

        private static FaceTextureData CropEntireImage(byte[] src, int srcW, int srcH)
            => NNResize(src, srcW, srcH, Math.Min(srcW, MaxTexSize), Math.Min(srcH, MaxTexSize));

        private static FaceTextureData NNResize(byte[] src, int srcW, int srcH, int dstW, int dstH)
        {
            var dst = new byte[dstW * dstH * 4];
            float sx = srcW / (float)dstW, sy = srcH / (float)dstH;
            for (int dy = 0; dy < dstH; ++dy)
            for (int dx = 0; dx < dstW; ++dx)
            {
                int ox = Math.Min(srcW - 1, (int)(dx * sx));
                int oy = Math.Min(srcH - 1, (int)(dy * sy));
                int si = (oy * srcW + ox) * 4;
                int di = (dy * dstW + dx) * 4;
                dst[di] = src[si]; dst[di+1] = src[si+1];
                dst[di+2] = src[si+2]; dst[di+3] = src[si+3];
            }
            return new FaceTextureData(dst, dstW, dstH);
        }
    }
}
