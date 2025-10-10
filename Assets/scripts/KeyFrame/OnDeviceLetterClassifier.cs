using System;
using System.Collections.Generic;
using Unity.Sentis;
using UnityEngine;

namespace LetterDojo.Dictation.OnDevice
{
    [Serializable]
    public sealed class LetterPrediction
    {
        public static readonly LetterPrediction NoWriting = new LetterPrediction(false, string.Empty, 0f, Array.Empty<(string letter, float probability)>());

        public bool HasWriting { get; }
        public string TopLetter { get; }
        public float TopConfidence { get; }
        public IReadOnlyList<(string letter, float probability)> Ranked { get; }

        public LetterPrediction(bool hasWriting, string topLetter, float topConfidence, IReadOnlyList<(string letter, float probability)> ranked)
        {
            HasWriting = hasWriting;
            TopLetter = topLetter;
            TopConfidence = topConfidence;
            Ranked = ranked;
        }
    }

    /// <summary>
    /// Performs on-device grading of the dictation board capture using a Sentis model exported from the handwriting endpoint.
    /// </summary>
    public sealed class OnDeviceLetterClassifier : MonoBehaviour
    {
        private const float Mean = 0.1307f;
        private const float Std = 0.3081f;
        private const float DefaultForegroundThreshold = 32f / 255f;

        private static readonly string[] LetterLabels = CreateLetterLabels();

        [Header("Sentis Model")]
        [SerializeField] private ModelAsset modelAsset;
        [SerializeField] private BackendType preferredBackend = BackendType.GPUCompute;
        [SerializeField] private bool preloadOnAwake = true;

        [Header("Preprocessing")]
        [SerializeField, Range(0f, 1f)] private float foregroundThreshold = DefaultForegroundThreshold;
        [SerializeField, Range(0f, 0.15f)] private float paddingFraction = 0.05f;
        [SerializeField] private int minimumPaddingPixels = 2;

        private readonly object workerLock = new();
        private Model runtimeModel;
        private IWorker worker;

        public IReadOnlyList<string> Letters => LetterLabels;
        public bool HasModelAsset => modelAsset != null;

        private void Awake()
        {
            if (preloadOnAwake && modelAsset != null)
            {
                try
                {
                    EnsureModelReady();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[OnDeviceLetterClassifier] Failed to preload model: {ex.Message}");
                }
            }
        }

        private void OnDestroy()
        {
            DisposeWorker();
        }

        public bool IsReady
        {
            get
            {
                lock (workerLock)
                {
                    return worker != null;
                }
            }
        }

        public void WarmupModel()
        {
            lock (workerLock)
            {
                if (modelAsset == null)
                {
                    Debug.LogWarning("[OnDeviceLetterClassifier] Warmup requested but model asset is not assigned.");
                    return;
                }

                EnsureModelReady();
            }
        }

        public void Configure(ModelAsset newModelAsset, BackendType backendType, bool preload, float fgThreshold, float padFraction, int minPad)
        {
            lock (workerLock)
            {
                bool changed = false;

                if (modelAsset != newModelAsset)
                {
                    modelAsset = newModelAsset;
                    changed = true;
                }

                if (preferredBackend != backendType)
                {
                    preferredBackend = backendType;
                    changed = true;
                }

                if (Mathf.Abs(foregroundThreshold - fgThreshold) > 1e-6f)
                {
                    foregroundThreshold = Mathf.Clamp01(fgThreshold);
                }

                float clampedPad = Mathf.Clamp(padFraction, 0f, 0.25f);
                if (Mathf.Abs(paddingFraction - clampedPad) > 1e-6f)
                {
                    paddingFraction = clampedPad;
                }

                int clampedMinPad = Mathf.Max(0, minPad);
                if (minimumPaddingPixels != clampedMinPad)
                {
                    minimumPaddingPixels = clampedMinPad;
                }

                preloadOnAwake = preload;

                if (changed)
                {
                    DisposeWorkerInternal();
                }
            }
        }

        public LetterPrediction Predict(Texture2D capture)
        {
            if (capture == null) throw new ArgumentNullException(nameof(capture));

            lock (workerLock)
            {
                EnsureModelReady();

                Texture2D readable = capture;
                if (!capture.isReadable)
                {
                    readable = DuplicateTexture(capture);
                }

                if (!TryBuildInput(readable, out float[] tensorData, out bool hasWriting))
                {
                    if (readable != capture)
                    {
                        Destroy(readable);
                    }
                    return LetterPrediction.NoWriting;
                }

                float[] logits = ExecuteModel(tensorData);

                if (readable != capture)
                {
                    Destroy(readable);
                }

                return BuildPrediction(logits, hasWriting);
            }
        }

        private void EnsureModelReady()
        {
            if (worker != null)
            {
                return;
            }

            if (modelAsset == null)
            {
                throw new InvalidOperationException("OnDeviceLetterClassifier: Model asset not assigned.");
            }

            if (runtimeModel == null)
            {
                runtimeModel = ModelLoader.Load(modelAsset);
            }

            try
            {
                worker = WorkerFactory.CreateWorker(preferredBackend, runtimeModel);
                Debug.Log($"[OnDeviceLetterClassifier] Sentis worker initialised with backend {preferredBackend}.");
            }
            catch (Exception backendEx)
            {
                if (preferredBackend == BackendType.CPU)
                {
                    DisposeWorker();
                    throw;
                }

                Debug.LogWarning($"[OnDeviceLetterClassifier] Backend {preferredBackend} failed ({backendEx.Message}). Falling back to CPU.");
                DisposeWorkerInternal();
                runtimeModel ??= ModelLoader.Load(modelAsset);
                worker = WorkerFactory.CreateWorker(BackendType.CPU, runtimeModel);
                Debug.Log("[OnDeviceLetterClassifier] Sentis worker initialised with backend CPU.");
            }
        }

        private void DisposeWorker()
        {
            lock (workerLock)
            {
                DisposeWorkerInternal();
            }
        }

        private void DisposeWorkerInternal()
        {
            worker?.Dispose();
            worker = null;
            runtimeModel = null;
        }

        private static Texture2D DuplicateTexture(Texture2D source)
        {
            var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(source, rt);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(source.width, source.height, TextureFormat.RGB24, false, false);
            tex.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            tex.Apply(false, false);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }

        private bool TryBuildInput(Texture2D texture, out float[] tensor, out bool hasWriting)
        {
            tensor = Array.Empty<float>();
            hasWriting = false;

            int width = texture.width;
            int height = texture.height;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            Color32[] pixels = texture.GetPixels32();
            if (pixels == null || pixels.Length == 0)
            {
                return false;
            }

            float[] grayscale = new float[pixels.Length];
            double total = 0d;
            // Unity's GetPixels32 returns pixels in left-to-right, bottom-to-top order.
            // Convert to top-to-bottom so y=0 corresponds to the top row (matches PIL and training).
            for (int y = 0; y < height; y++)
            {
                int srcRow = (height - 1 - y) * width; // flip vertically
                int dstRow = y * width;
                for (int x = 0; x < width; x++)
                {
                    Color32 c = pixels[srcRow + x];
                    float g = (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) * (1f / 255f);
                    grayscale[dstRow + x] = g;
                    total += g;
                }
            }

            float mean = (float)(total / pixels.Length);
            bool shouldInvert = mean > 0.5f;

            if (shouldInvert)
            {
                for (int i = 0; i < grayscale.Length; i++)
                {
                    grayscale[i] = 1f - grayscale[i];
                }
            }

            int minX = width;
            int minY = height;
            int maxX = -1;
            int maxY = -1;
            float thresh = Mathf.Clamp01(foregroundThreshold);

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    float v = grayscale[rowOffset + x];
                    if (v > thresh)
                    {
                        hasWriting = true;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            if (!hasWriting)
            {
                tensor = new float[28 * 28];
                return true;
            }

            int widthRect = Mathf.Clamp(maxX - minX + 1, 1, width);
            int heightRect = Mathf.Clamp(maxY - minY + 1, 1, height);

            int padX = Mathf.Max(Mathf.FloorToInt(widthRect * Mathf.Clamp01(paddingFraction)), minimumPaddingPixels);
            int padY = Mathf.Max(Mathf.FloorToInt(heightRect * Mathf.Clamp01(paddingFraction)), minimumPaddingPixels);

            minX = Mathf.Max(0, minX - padX);
            minY = Mathf.Max(0, minY - padY);
            maxX = Mathf.Min(width - 1, maxX + padX);
            maxY = Mathf.Min(height - 1, maxY + padY);

            widthRect = Mathf.Clamp(maxX - minX + 1, 1, width);
            heightRect = Mathf.Clamp(maxY - minY + 1, 1, height);

            // Extract the tight patch
            float[] patch = new float[widthRect * heightRect];
            for (int srcY = 0; srcY < heightRect; srcY++)
            {
                int sourceRow = (minY + srcY) * width;
                int destRow = srcY * widthRect;
                Array.Copy(grayscale, sourceRow + minX, patch, destRow, widthRect);
            }

            int squareSide = Mathf.Max(widthRect, heightRect);
            float[] square = new float[squareSide * squareSide];
            int padLeft = Mathf.Max(0, (squareSide - widthRect) / 2);
            int padTop = Mathf.Max(0, (squareSide - heightRect) / 2);

            for (int y = 0; y < heightRect; y++)
            {
                int srcRow = y * widthRect;
                int dstRow = (y + padTop) * squareSide + padLeft;
                Array.Copy(patch, srcRow, square, dstRow, widthRect);
            }

            tensor = new float[1 * 28 * 28];
            ResampleSquareToTensor(square, squareSide, tensor);
            return true;
        }

        private static void ResampleSquareToTensor(IReadOnlyList<float> square, int side, float[] tensor)
        {
            if (side <= 0)
            {
                Array.Clear(tensor, 0, tensor.Length);
                return;
            }

            float scale = side / 28f;
            for (int y = 0; y < 28; y++)
            {
                float srcY = (y + 0.5f) * scale - 0.5f;

                for (int x = 0; x < 28; x++)
                {
                    float srcX = (x + 0.5f) * scale - 0.5f;
                    float value = SampleBicubic(square, side, side, srcX, srcY);
                    int tensorIndex = y * 28 + x;
                    tensor[tensorIndex] = (value - Mean) / Std;
                }
            }
        }

        private static float SampleBicubic(IReadOnlyList<float> image, int width, int height, float srcX, float srcY)
        {
            int xInt = Mathf.FloorToInt(srcX);
            int yInt = Mathf.FloorToInt(srcY);
            float xFrac = srcX - xInt;
            float yFrac = srcY - yInt;

            float Sample(int ix, int iy)
            {
                ix = Mathf.Clamp(ix, 0, width - 1);
                iy = Mathf.Clamp(iy, 0, height - 1);
                return image[iy * width + ix];
            }

            float Row(float t, int row)
            {
                float p0 = Sample(xInt - 1, row);
                float p1 = Sample(xInt + 0, row);
                float p2 = Sample(xInt + 1, row);
                float p3 = Sample(xInt + 2, row);
                return CubicHermite(p0, p1, p2, p3, t);
            }

            float r0 = Row(xFrac, yInt - 1);
            float r1 = Row(xFrac, yInt + 0);
            float r2 = Row(xFrac, yInt + 1);
            float r3 = Row(xFrac, yInt + 2);

            float value = CubicHermite(r0, r1, r2, r3, yFrac);
            return Mathf.Clamp01(value);
        }

        private static float CubicHermite(float p0, float p1, float p2, float p3, float t)
        {
            // Catmull-Rom spline (matches PIL bicubic interpolation)
            float t2 = t * t;
            float t3 = t2 * t;

            return 0.5f * ((2f * p1) +
                           (-p0 + p2) * t +
                           (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                           (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        private float[] ExecuteModel(float[] tensorData)
        {
            if (worker == null)
            {
                throw new InvalidOperationException("OnDeviceLetterClassifier: Worker not initialised.");
            }

            // Feed as NCHW (C=1) to match PyTorch-exported ONNX
            using var inputTensor = new TensorFloat(new TensorShape(1, 1, 28, 28), tensorData);
            worker.Execute(inputTensor);

            if (worker.PeekOutput() is not TensorFloat outputTensor)
            {
                throw new InvalidOperationException("OnDeviceLetterClassifier: Sentis output was not TensorFloat.");
            }

            outputTensor.CompleteAllPendingOperations();
            float[] logits = outputTensor.ToReadOnlyArray();
            outputTensor.Dispose();
            return logits;
        }

        private static LetterPrediction BuildPrediction(float[] logits, bool hasWriting)
        {
            if (logits == null || logits.Length == 0)
            {
                return LetterPrediction.NoWriting;
            }

            float[] probabilities = Softmax(logits);
            var ranked = new List<(string letter, float probability)>(probabilities.Length);
            int letters = Mathf.Min(probabilities.Length, LetterLabels.Length);
            for (int i = 0; i < letters; i++)
            {
                ranked.Add((LetterLabels[i], probabilities[i]));
            }

            ranked.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            var top = ranked.Count > 0 ? ranked[0] : (string.Empty, 0f);

            if (!hasWriting)
            {
                return new LetterPrediction(false, string.Empty, 0f, ranked);
            }

            return new LetterPrediction(true, top.Item1, top.Item2, ranked);
        }

        private static float[] Softmax(float[] logits)
        {
            float max = logits[0];
            for (int i = 1; i < logits.Length; i++)
            {
                if (logits[i] > max) max = logits[i];
            }

            double sum = 0d;
            float[] probs = new float[logits.Length];
            for (int i = 0; i < logits.Length; i++)
            {
                double value = Math.Exp(logits[i] - max);
                probs[i] = (float)value;
                sum += value;
            }

            if (sum <= 0d) sum = 1d;
            for (int i = 0; i < probs.Length; i++)
            {
                probs[i] = (float)(probs[i] / sum);
            }

            return probs;
        }

        private static string[] CreateLetterLabels()
        {
            var labels = new string[26];
            for (int i = 0; i < labels.Length; i++)
            {
                labels[i] = ((char)('a' + i)).ToString();
            }
            return labels;
        }
    }
}
