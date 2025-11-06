using System.Text;
using Unity.Profiling;
using UnityEngine;

namespace Diagnostics
{
    /// <summary>
    /// Displays key runtime counters (FPS, system/GC/Gfx/audio memory) using ProfilerRecorder.
    /// Safe for builds and useful on Quest to spot jitter sources without the Profiler window.
    /// </summary>
    public sealed class RuntimeCountersHUD : MonoBehaviour
    {
        ProfilerRecorder _sysUsed;
        ProfilerRecorder _gcUsed;
        ProfilerRecorder _gcReserved;
        ProfilerRecorder _gfxUsed;
        ProfilerRecorder _audioUsed;
        ProfilerRecorder _profilerUsed;

        float _fpsAccumTime;
        int _fpsFrames;
        float _fps;

        void OnEnable()
        {
            _sysUsed     = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory");
            _gcUsed      = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Used Memory");
            _gcReserved  = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Reserved Memory");
            _gfxUsed     = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Gfx Used Memory");
            _audioUsed   = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Audio Used Memory");
            _profilerUsed = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Profiler Memory Used");
        }

        void OnDisable()
        {
            _sysUsed.Dispose();
            _gcUsed.Dispose();
            _gcReserved.Dispose();
            _gfxUsed.Dispose();
            _audioUsed.Dispose();
            _profilerUsed.Dispose();
        }

        void Update()
        {
            _fpsFrames++;
            _fpsAccumTime += Time.unscaledDeltaTime;
            if (_fpsAccumTime >= 0.5f)
            {
                _fps = _fpsFrames / _fpsAccumTime;
                _fpsFrames = 0;
                _fpsAccumTime = 0f;
            }
        }

        void OnGUI()
        {
            GUI.matrix = Matrix4x4.Scale(new Vector3(1.25f, 1.25f, 1f));

            var sb = new StringBuilder(256);
            sb.Append("FPS ").Append(_fps.ToString("0"));
            sb.Append("  |  Sys ").Append(Format(_sysUsed));
            sb.Append("  Gfx ").Append(Format(_gfxUsed));
            sb.Append("  GC ").Append(Format(_gcUsed)).Append('/').Append(Format(_gcReserved));
            sb.Append("  Audio ").Append(Format(_audioUsed));
            if (_profilerUsed.Valid)
            {
                sb.Append("  Prof ").Append(Format(_profilerUsed));
            }

            GUI.Label(new Rect(10f, 10f, 1800f, 48f), sb.ToString());
        }

        static string Format(ProfilerRecorder recorder)
        {
            if (!recorder.Valid) return "-";

            long bytes = recorder.LastValue;
            const float kb = 1024f;
            const float mb = kb * kb;
            return bytes < mb ? $"{bytes / kb:0.#} KB" : $"{bytes / mb:0.#} MB";
        }
    }
}
