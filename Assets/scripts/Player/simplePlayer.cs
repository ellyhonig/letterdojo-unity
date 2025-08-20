using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class simplePlayer : MonoBehaviour
{

    public GameObject conR;
    public GameObject conL;
    public GameObject hmd;
    public GameObject righthand;
    public GameObject lefthand;
    public UpdateDelegate currentUpdate;
    public delegate void UpdateDelegate();
    // Start is called before the first frame update
    void Start()
    {
        lefthand = GameObject.CreatePrimitive(PrimitiveType.Sphere); // Create a sphere to represent the body part
        lefthand.transform.localScale = new Vector3(0.06f,0.06f,0.06f); // Set the scale of the sphere
        lefthand.name = "lefthand"; // Set the name of the GameObject
        
        righthand = GameObject.CreatePrimitive(PrimitiveType.Sphere); // Create a sphere to represent the body part
        righthand.transform.localScale = new Vector3(0.06f,0.06f,0.06f); // Set the scale of the sphere
        righthand.name = "righthand"; // Set the name of the GameObject
        currentUpdate = regularUpdate;
        
    }

    // Update is called once per frame
    void Update()
    {
       currentUpdate?.Invoke(); 

    }
    public void regularUpdate()
    {
       righthand.transform.position = conR.transform.position;     
       lefthand.transform.position = conL.transform.position;
    }
    
}
