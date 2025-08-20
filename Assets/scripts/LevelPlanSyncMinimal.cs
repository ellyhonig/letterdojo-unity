using UnityEngine;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

public class LevelPlanPythonSync : MonoBehaviour
{
    public float syncInterval = 3f;
    private float timer;

    private const int PYTHON_TIMEOUT_MS = 3000;

    // Cache the last JSON so we can compare
    private string lastPlanJson = null;

    void Start()
    {
        RunPythonScriptAsync(); // fetch_levelplan.py once at start
        timer = 0f;
    }

    void Update()
    {
        timer += Time.deltaTime;
        if (timer >= syncInterval)
        {
            RunPythonScriptAsync(); // fetch_levelplan.py again asynchronously
            timer = 0f;
        }
    }

    private async void RunPythonScriptAsync()
    {
        // Call the method in a separate thread
        await Task.Run(() => RunPythonScript());
    }

    private void RunPythonScript()
    {
        string pythonScriptPath = Path.Combine(Application.streamingAssetsPath, "fetch_levelplan.py");
        if (!File.Exists(pythonScriptPath))
        {
            UnityEngine.Debug.LogError($"Python script not found at: {pythonScriptPath}");
            return;
        }

        try
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{pythonScriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (Process proc = new Process { StartInfo = psi })
            {
                proc.Start();

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();

                bool exited = proc.WaitForExit(PYTHON_TIMEOUT_MS);

                if (!exited)
                {
                    proc.Kill();
                    UnityEngine.Debug.LogWarning("Python process took too long and was killed!");
                }

                if (proc.ExitCode == 0 && exited)
                {
                    string newPlanJson = TryReadLevelPlanJson();
                    if (!string.IsNullOrEmpty(newPlanJson))
                    {
                        if (newPlanJson == lastPlanJson)
                        {
                            UnityEngine.Debug.Log("Level plan has not changed; skipping reload.");
                        }
                        else
                        {
                            lastPlanJson = newPlanJson;
                            LevelManager manager = FindObjectOfType<LevelManager>();
                            if (manager != null)
                            {
                                manager.LoadLevelPlan();
                                UnityEngine.Debug.Log("Level plan reloaded successfully.");
                                manager.SetLevelByLetter(manager.currentLetter);
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(stdout))
                    UnityEngine.Debug.Log($"[Python STDOUT] {stdout}");
                if (!string.IsNullOrEmpty(stderr))
                    UnityEngine.Debug.LogError($"[Python STDERR] {stderr}");
            }
        }
        catch (System.Exception e)
        {
            UnityEngine.Debug.LogError($"Error running Python script: {e}");
        }
    }

    private string TryReadLevelPlanJson()
    {
        string filePath = Path.Combine(Application.streamingAssetsPath, "LevelPlan.json");
        if (!File.Exists(filePath))
        {
            UnityEngine.Debug.LogWarning("No LevelPlan.json to read after fetch!");
            return null;
        }
        return File.ReadAllText(filePath);
    }
}
