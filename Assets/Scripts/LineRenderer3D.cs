using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Jobs;
using Unity.VisualScripting;
using UnityEngine;
using Unity.Burst;
[System.Serializable]
public struct Point{
    public Vector3 position;
    [HideInInspector] public Vector3 direction;
    [HideInInspector] public Vector3 normal;
    [HideInInspector] public Vector3 up;
    [HideInInspector] public Vector3 right;
    public float thickness;
    public Point(Vector3 position, Vector3 direction, Vector3 normal, Vector3 up, Vector3 right, float thickness){
        this.position = position;
        this.direction = direction;
        this.normal = normal;
        this.thickness = thickness;
        this.up = up;
        this.right = right;
    }
    public Point(Vector3 position, float thickness){
        this.position = position;
        this.direction = Vector3.zero;
        this.normal = Vector3.zero;
        this.thickness = thickness;
        this.up = Vector3.zero;
        this.right = Vector3.zero;
    }
}
[BurstCompile] public struct Line3D : IJobParallelFor {
    public int resolution;
    public int iterations;
    public bool uniformScale;
    [ReadOnly] public NativeArray<Point> nodes;
    [ReadOnly] public NativeArray<float> sines;
    [ReadOnly] public NativeArray<float> cosines;
    //[NativeDisableParallelForRestriction] is unsafe and can cause race conditions,
    //but in this case each job works on n=resolution vertices so it's not an issue
    //look at it like at a 2d array of size Points x resolution
    [NativeDisableParallelForRestriction] public NativeArray<Vector3> vertices;
    [NativeDisableParallelForRestriction] public NativeArray<int> indices;
    public void Execute(int i) {
        Vector3 right = nodes[i].right.normalized * nodes[i].thickness;
        Vector3 up = nodes[i].up.normalized * nodes[i].thickness;
        for (int j = 0; j < resolution; j++){
            vertices[i * resolution + j] = nodes[i].position;
            Vector3 vertexOffset = cosines[j] * right + sines[j] * up;
            if(uniformScale) vertexOffset += nodes[i].normal.normalized * Vector3.Dot(nodes[i].normal.normalized, vertexOffset) * (Mathf.Clamp(1/nodes[i].normal.magnitude, 0, 2) - 1);
            vertices[i * resolution + j] += vertexOffset;
            if (i == iterations - 1) continue;
            int offset = i * resolution * 6 + j * 6;
            indices[offset] = j + i * resolution;
            indices[offset + 1] = (j + 1) % resolution + i * resolution;
            indices[offset + 2] = j + resolution + i * resolution;
            indices[offset + 3] = (j + 1) % resolution + i * resolution;
            indices[offset + 4] = (j + 1) % resolution + resolution + i * resolution;
            indices[offset + 5] = j + resolution + i * resolution;
        }
    }
}
[BurstCompile] public struct CalculatePointData : IJobParallelFor{
    [NativeDisableParallelForRestriction] public NativeArray<Point> nodes;
    public void Execute(int i){
        if (i == 0) return;
        Vector3 previous = (nodes[i].position - nodes[i-1].position).normalized;
        Vector3 next = (nodes[i+1].position - nodes[i].position).normalized;
        Vector3 direction = Vector3.Lerp(previous, next, 0.5f).normalized;
        Vector3 normal = (next - previous).normalized * Mathf.Abs(Vector3.Dot(previous, direction)); //length encodes cosine of angle   
        Vector3 right = Vector3.Cross(direction, Vector3.right).normalized;
        if(right.magnitude < 0.05f){
            right = Vector3.Cross(direction, Vector3.forward).normalized;
        }
        Vector3 up = Vector3.Cross(direction, right).normalized;
        nodes[i] = new Point(nodes[i].position, direction, normal, up, right, nodes[i].thickness);
    }
}
[BurstCompile] public struct FixPointsRotation : IJob{
    public NativeArray<Point> nodes;
    public void Execute(){
            for(int i = 0; i < nodes.Length - 1; i++){
            Vector3 fromTo = (nodes[i + 1].position - nodes[i].position).normalized;
            Vector3 firstRight = nodes[i].right - Vector3.Dot(nodes[i].right, fromTo) * fromTo;
            Vector3 secondRight = nodes[i+1].right - Vector3.Dot(nodes[i+1].right, fromTo) * fromTo;
            float angleRight = -Vector3.SignedAngle(firstRight, secondRight, fromTo);
            Quaternion rot = Quaternion.AngleAxis(angleRight, nodes[i + 1].direction);
            nodes[i+1] = new Point(nodes[i+1].position, nodes[i+1].direction, nodes[i+1].normal, rot * nodes[i+1].up, rot * nodes[i+1].right, nodes[i+1].thickness);
        }   
    }
}
public class LineRenderer3D : MonoBehaviour
{
    public bool fixTwisting;
    public bool uniformScale;
    [SerializeField] List<Point> points = new List<Point>();
    [SerializeField] int resolution;
    [SerializeField] MeshFilter meshFilter;
    [SerializeField] Mesh mesh;
    [SerializeField] MeshRenderer meshRenderer;
    public Material material;
    //Vector3[] vertices;
    //-----------------------------------------------------------------------//
    NativeArray<Vector3> vertices;
    NativeArray<Point> nodes;
    NativeArray<int> indices;
    NativeArray<float> sines;
    NativeArray<float> cosines;
    public Vector3[] vert;
    public int[] ind;
    JobHandle jobHandle;
    JobHandle pointsJobHandle;
    JobHandle rotationJobHandle;
    public float rotation;
    void Awake(){
        meshRenderer = gameObject.AddComponent<MeshRenderer>();
        meshRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.BlendProbes;
        meshFilter = gameObject.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = new Mesh();
    }
    void Start()
    {
        mesh = new Mesh();
        Application.targetFrameRate = -1;
        meshRenderer.sharedMaterial = material;
        points.Clear();
        Vector3 direction = Vector3.forward;
        Vector3 position = Vector3.zero;
        Vector3 lastDirection = Vector3.forward;
        for(float i = 0; i < 2048; i++){
            int random = Random.Range(0, 5);
            if(random == 0){
                direction = Vector3.right;
            }else if (random == 1){
                direction = Vector3.up;
            }else if (random == 2){
                direction = Vector3.forward;
            }else if (random == 3){
                direction = Vector3.left;
            }else if (random == 4){
                direction = Vector3.down;
            }else if (random == 5){
                direction = Vector3.back;
            }
            if (Vector3.Dot(lastDirection, direction) < 0) direction = -direction;
            position += direction;
            points.Add(new Point(position, 0.2f));
            lastDirection = direction;

        }
    }

    void Update()
    {
        vertices = new NativeArray<Vector3>(points.Count() * resolution, Allocator.TempJob);
        indices = new NativeArray<int>(points.Count() * resolution * 6 - resolution * 6, Allocator.TempJob);
        nodes = new NativeArray<Point>(points.Count(), Allocator.TempJob);
        sines = new NativeArray<float>(resolution, Allocator.TempJob);
        cosines = new NativeArray<float>(resolution, Allocator.TempJob);
        for(int i = 0; i < points.Count(); i++){
            nodes[i] = points[i];
        }
        var pointsJob = new CalculatePointData()
        {
            nodes = nodes
        };
        pointsJobHandle = pointsJob.Schedule(points.Count() - 1, 512);
        for(int i = 0; i < resolution; i++){
            sines[i] = Mathf.Sin(i * Mathf.PI * 2 / resolution);
            cosines[i] = Mathf.Cos(i * Mathf.PI * 2 / resolution);
        }
        pointsJobHandle.Complete();
        RecalculatePoints(); 
        var rotationJob = new FixPointsRotation()
        {
            nodes = nodes
        };
        rotationJobHandle = rotationJob.Schedule();
        rotationJobHandle.Complete();
        var meshJob = new Line3D() {
            resolution = resolution,
            indices = indices,
            vertices = vertices,
            sines = sines,
            nodes = nodes,
            cosines = cosines,
            iterations = points.Count(),
            uniformScale = uniformScale,
        };
        jobHandle = meshJob.Schedule(points.Count(), 16);
        JobHandle.ScheduleBatchedJobs();
    }
    void LateUpdate(){
        jobHandle.Complete();
        mesh.SetVertices(vertices.ToArray());
        mesh.SetTriangles(indices.ToArray(), 0);
        meshFilter.sharedMesh = mesh;

        mesh.RecalculateNormals();
        vertices.Dispose();
        indices.Dispose();
        sines.Dispose();
        cosines.Dispose();
        nodes.Dispose();



    }
    public void RecalculatePoints(){
        /*for(int i = 1; i < points.Count() - 1; i++){
            Vector3 previous = (points[i].position - points[i-1].position).normalized;
            Vector3 next = (points[i+1].position - points[i].position).normalized;
            Vector3 direction = Vector3.Lerp(previous, next, 0.5f).normalized;
            Vector3 normal = (next - previous).normalized * Mathf.Abs(Vector3.Dot(previous, direction)); //length encodes cosine of angle   
            Vector3 right = Vector3.Cross(direction, Vector3.right).normalized;
            if(Mathf.Approximately(0, right.magnitude)){
                right = Vector3.Cross(direction, Vector3.forward).normalized;
            }
            Vector3 up = Vector3.Cross(direction, right).normalized;
            points[i] = new Point(points[i].position, direction, normal, up, right, points[i].thickness);
        }*/
        Vector3 edgeDirection = (nodes[1].position - nodes[0].position).normalized;
        Vector3 edgeRight = Vector3.Cross(edgeDirection, Vector3.right).normalized;
        Vector3 edgeUp = Vector3.Cross(edgeDirection, edgeRight).normalized;
        nodes[0] = new Point(nodes[0].position, edgeDirection, Vector3.zero, edgeUp, edgeRight, nodes[0].thickness);
        edgeDirection = (nodes[nodes.Length - 1].position - nodes[nodes.Length - 2].position).normalized;
        edgeRight = Vector3.Cross(edgeDirection, Vector3.right).normalized;
        edgeUp = Vector3.Cross(edgeDirection, edgeRight).normalized;
        nodes[nodes.Count()-1] = new Point(nodes[nodes.Length-1].position, edgeDirection, Vector3.zero, edgeUp, edgeRight, nodes[nodes.Length-1].thickness); 
    
        /*for(int i = 0; i < nodes.Length; i++){
            if (i == nodes.Length - 1) continue;
            Vector3 fromTo = (nodes[i + 1].position - nodes[i].position).normalized;
            Vector3 firstRight = nodes[i].right - Vector3.Dot(nodes[i].right, fromTo) * fromTo;
            Vector3 secondRight = nodes[i+1].right - Vector3.Dot(nodes[i+1].right, fromTo) * fromTo;
            float angleRight = -Vector3.SignedAngle(firstRight, secondRight, fromTo);
            Quaternion rot = Quaternion.AngleAxis(angleRight + rotation, nodes[i + 1].direction);
            if(fixTwisting) nodes[i+1] = new Point(nodes[i+1].position, nodes[i+1].direction, nodes[i+1].normal, rot * nodes[i+1].up, rot * nodes[i+1].right, nodes[i+1].thickness);
        }   */



    }
}
