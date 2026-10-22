using UnityEngine;

namespace Freehand.Spray
{
    /// <summary>
    /// Upgrades the can's placeholder box geometry to a more can-like shape
    /// at runtime using GameObject.CreatePrimitive, so no built-in mesh
    /// fileID has to be hand-authored/guessed in the scene file — Unity
    /// resolves the real Cylinder/Sphere mesh internally. Purely visual:
    /// only swaps MeshFilter.sharedMesh and adds one cosmetic nozzle tip.
    /// Never touches the Transforms used for grab, raycast or particles.
    /// </summary>
    public class SprayCanVisual : MonoBehaviour
    {
        [SerializeField] MeshFilter bodyMeshFilter;
        [SerializeField] MeshFilter capMeshFilter;
        [SerializeField] Transform sprayOrigin;
        [SerializeField] Material nozzleMaterial;

        void Awake()
        {
            SwapToCylinder(bodyMeshFilter);
            SwapToCylinder(capMeshFilter);
            BuildNozzleTip();
        }

        static void SwapToCylinder(MeshFilter target)
        {
            if (target == null)
                return;

            var temp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            target.sharedMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(temp);
        }

        void BuildNozzleTip()
        {
            if (sprayOrigin == null)
                return;

            var nozzle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            nozzle.name = "NozzleTip";
            Destroy(nozzle.GetComponent<Collider>());
            nozzle.transform.SetParent(sprayOrigin, false);
            nozzle.transform.localPosition = Vector3.zero;
            nozzle.transform.localScale = Vector3.one * 0.2f;

            if (nozzleMaterial != null)
                nozzle.GetComponent<MeshRenderer>().sharedMaterial = nozzleMaterial;
        }
    }
}
