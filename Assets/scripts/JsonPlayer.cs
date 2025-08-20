using UnityEngine;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

public class JsonPlayer : MonoBehaviour
{
    public string jsonFileName = "landmarks.json";
    private List<List<Vector3>> frames = new List<List<Vector3>>();
    private List<GameObject> spheres = new List<GameObject>();
    private int currentFrame = 0;
    private float timer = 0.0f;
    public float frameRate = 0.05f; // Time in seconds between frames
    private GameObject parentObject; // Parent object for all spheres

    void Start()
    {
        LoadJsonData();
        CreateParentObject();
        CreateSpheres();
    }

    void Update()
    {
        if (frames.Count > 0)
        {
            timer += Time.deltaTime;
            if (timer >= frameRate)
            {
                PlayFrame();
                timer = 0;
            }
        }
    }

    void LoadJsonData()
    {
        string filePath = Path.Combine(Application.dataPath, "scripts", jsonFileName);
        string jsonData = File.ReadAllText(filePath);
        var data = JsonConvert.DeserializeObject<List<Dictionary<string, List<Dictionary<string, float>>>>>(jsonData);

        foreach (var frameData in data)
        {
            List<Vector3> frame = new List<Vector3>();
            foreach (var landmark in frameData["landmarks"])
            {
                Vector3 position = new Vector3(landmark["x"], landmark["y"], landmark["z"]);
                frame.Add(position);
            }
            frames.Add(frame);
        }
    }

    void CreateParentObject()
    {
        parentObject = new GameObject("LandmarkParent");
    }

    void CreateSpheres()
    {
        if (frames.Count > 0)
        {
            foreach (Vector3 position in frames[0])
            {
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.transform.position = position; // Set initial global position
                sphere.transform.SetParent(parentObject.transform, true); // Set parent with worldPositionStays to true initially
                sphere.transform.localScale = new Vector3(0.02f, 0.02f, 0.02f); // Small sphere size
                spheres.Add(sphere);
            }
        }
    }

    void PlayFrame()
    {
        currentFrame = (currentFrame + 1) % frames.Count;
        for (int i = 0; i < spheres.Count; i++)
        {
            if (i < frames[currentFrame].Count)
            {
                // Calculate local position relative to the parent
                spheres[i].transform.localPosition = frames[currentFrame][i] - parentObject.transform.position;
            }
        }
    }
}
