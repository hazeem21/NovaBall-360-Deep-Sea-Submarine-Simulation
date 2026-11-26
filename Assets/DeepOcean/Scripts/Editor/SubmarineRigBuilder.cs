using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// EDITOR-ONLY one-click tool:  Tools > Deep Ocean > Build Submarine Rig
///
/// It does NOT change your submarine model file. It places a copy (prefab instance) of
/// Assets/Models/Submarine.glb into the scene inside a new parent object:
///
///   Submarine_Rig            <- Rigidbody + Box Collider + SubmarineController
///   |                           origin = pilot's HIP, axes x right / y up / z forward (NOVA requirement)
///   |-- Submarine_Model      <- your model, turned to face +Z and shifted so the hip sits at the rig origin
///   |-- Main Camera          <- moved here, at the model's "Pilot_Eye_Position" (cockpit view)
///
/// Then it places the rig at Submarine_SpawnPoint and makes the Ocean Zone Manager and
/// Ocean Audio Manager follow the submarine instead of the camera.
/// Everything can be undone with Ctrl+Z.
/// </summary>
public static class SubmarineRigBuilder
{
    const string ModelGuid = "596d518e95ceea94e8542f570f607d25";   // Assets/Models/Submarine.glb (works even if renamed)
    const string RigName = "Submarine_Rig";

    // A seated adult's eyes are roughly 0.75 m above the hip joint.
    const float EyeAboveHip = 0.75f;

    [MenuItem("Tools/Deep Ocean/Build Submarine Rig")]
    static void Build()
    {
        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();

        GameObject oldRig = GameObject.Find(RigName);
        if (oldRig != null)
        {
            if (!EditorUtility.DisplayDialog("Submarine Rig",
                    $"'{RigName}' already exists. Rebuild it?\n\nThe Main Camera is kept (moved out first), the old rig is removed and a new one is built.",
                    "Rebuild", "Cancel"))
                return;

            // Rescue the camera before deleting the old rig.
            Camera rescued = oldRig.GetComponentInChildren<Camera>(true);
            if (rescued != null)
                Undo.SetTransformParent(rescued.transform, null, "Rescue camera");
            Undo.DestroyObjectImmediate(oldRig);
        }

        string modelPath = AssetDatabase.GUIDToAssetPath(ModelGuid);
        var modelAsset = string.IsNullOrEmpty(modelPath) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        if (modelAsset == null)
        {
            EditorUtility.DisplayDialog("Submarine Rig", "Could not find the submarine model (Assets/Models/Submarine.glb).", "OK");
            return;
        }

        // ---------- 1. Rig + model ----------
        var rig = new GameObject(RigName);
        Undo.RegisterCreatedObjectUndo(rig, "Build Submarine Rig");

        var model = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset);
        Undo.RegisterCreatedObjectUndo(model, "Build Submarine Rig");
        model.name = "Submarine_Model";
        model.transform.SetParent(rig.transform, false);

        Transform eye  = FindDeep(model.transform, "Pilot_Eye_Position");
        Transform rear = FindDeep(model.transform, "Rear_Thruster");
        Transform dash = FindDeep(model.transform, "Dashboard");
        if (eye == null || rear == null || dash == null)
        {
            Debug.LogError("[Submarine Rig] Could not find Pilot_Eye_Position / Rear_Thruster / Dashboard inside the model.");
            Undo.RevertAllDownToGroup(undoGroup);
            return;
        }

        // ---------- 2. Face +Z (rear thruster -> dashboard = forward) ----------
        Vector3 forward = dash.position - rear.position;
        forward.y = 0f;
        float yawFix = Vector3.SignedAngle(forward, Vector3.forward, Vector3.up);
        model.transform.rotation = Quaternion.Euler(0f, yawFix, 0f) * model.transform.rotation;

        // ---------- 3. Put the pilot's hip at the rig origin ----------
        Vector3 hip = eye.position + Vector3.down * EyeAboveHip;
        model.transform.position -= hip;   // rig is at (0,0,0) while building

        // ---------- 4. Collider from the model's visible size ----------
        Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);
        bool first = true;
        foreach (Renderer r in model.GetComponentsInChildren<Renderer>())
        {
            if (first) { bounds = r.bounds; first = false; }
            else bounds.Encapsulate(r.bounds);
        }
        var box = rig.AddComponent<BoxCollider>();
        box.center = bounds.center;
        box.size = bounds.size * 0.95f;

        // ---------- 5. Physics + controller ----------
        var rb = rig.AddComponent<Rigidbody>();
        rb.mass = 20000f;
        rb.useGravity = false;
        rb.freezeRotation = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        var controller = rig.AddComponent<SubmarineController>();
        controller.scaleSettings = FindScaleSettings();

        // Sensible light ranges straight away (the model imports them with 100 km range).
        foreach (Light light in model.GetComponentsInChildren<Light>(true))
        {
            Undo.RecordObject(light, "Light range");
            if (light.name.Contains("Headlight")) light.range = controller.headlightRange;
            else if (light.name.StartsWith("Aux_")) light.range = controller.auxLightRange;
            else light.range = controller.interiorLightRange;
        }

        // ---------- 6. Cockpit camera ----------
        Camera cam = Camera.main;
        if (cam != null)
        {
            Undo.SetTransformParent(cam.transform, rig.transform, "Move camera into cockpit");
            Undo.RecordObject(cam.transform, "Move camera into cockpit");
            cam.transform.localPosition = rig.transform.InverseTransformPoint(eye.position);
            cam.transform.localRotation = Quaternion.identity;
            Undo.RecordObject(cam, "Camera clip planes");
            cam.nearClipPlane = 0.05f;   // see the dashboard up close
            if (cam.farClipPlane < 40000f) cam.farClipPlane = 40000f;

            // The HDRP scene template puts a "Free Camera" fly-cam on Main Camera. It also reacts to WASD/QE
            // and would fly the camera away from the submarine, so switch it off.
            var freeCam = cam.GetComponent<UnityEngine.Rendering.FreeCamera>();
            if (freeCam != null)
            {
                Undo.RecordObject(freeCam, "Disable Free Camera");
                freeCam.enabled = false;
            }
        }
        else
        {
            Debug.LogWarning("[Submarine Rig] No camera tagged MainCamera found. Add a camera at Pilot_Eye_Position yourself.");
        }

        // ---------- 7. Place at the spawn point, FLOATING AT THE SURFACE ----------
        // The experience starts on the surface (hear the waves), then the pilot dives (submerge splash).
        GameObject spawn = GameObject.Find("Submarine_SpawnPoint");
        float surfaceY = controller.scaleSettings != null ? controller.scaleSettings.surfaceY : 0f;
        Vector3 start = spawn != null ? spawn.transform.position : Vector3.zero;
        start.y = surfaceY - controller.minDepthBelowSurface;
        float startYaw = spawn != null ? spawn.transform.eulerAngles.y : 0f;
        rig.transform.SetPositionAndRotation(start, Quaternion.Euler(0f, startYaw, 0f));

        // ---------- 7b. Calm sea, so waves don't wash into the open cockpit at the start ----------
        var ocean = Object.FindFirstObjectByType<UnityEngine.Rendering.HighDefinition.WaterSurface>();
        if (ocean != null)
        {
            Undo.RecordObject(ocean, "Calm sea");
            ocean.largeWindSpeed = 10f;
            ocean.largeBand0Multiplier = 0.3f;
            ocean.largeBand1Multiplier = 0.3f;
            ocean.ripplesWindSpeed = 4f;
        }

        // ---------- 8. Zones + audio follow the submarine ----------
        var zoneManager = Object.FindFirstObjectByType<OceanZoneManager>();
        if (zoneManager != null)
        {
            Undo.RecordObject(zoneManager, "Depth source");
            zoneManager.depthSource = rig.transform;
        }
        var audioManager = Object.FindFirstObjectByType<OceanAudioManager>();
        if (audioManager != null)
        {
            Undo.RecordObject(audioManager, "Depth source");
            audioManager.depthSource = rig.transform;
        }

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(rig.scene);
        Selection.activeGameObject = rig;
        Debug.Log($"[Submarine Rig] Built. Model turned {yawFix:0.#}°, hip moved to rig origin, collider size {box.size}.");
    }

    static Transform FindDeep(Transform parent, string name)
    {
        foreach (Transform t in parent.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    static OceanScaleSettings FindScaleSettings()
    {
        string[] guids = AssetDatabase.FindAssets("t:OceanScaleSettings");
        return guids.Length > 0 ? AssetDatabase.LoadAssetAtPath<OceanScaleSettings>(AssetDatabase.GUIDToAssetPath(guids[0])) : null;
    }
}
