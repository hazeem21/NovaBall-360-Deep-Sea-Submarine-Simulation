// Build example - must be placed in the scripts/editor folder (unity requirement)
using UnityEditor;
class ScriptBatch
{
    static void PerformBuild ()
    {
        // Get filename.
        string win_path = "build_win/Virtual NOVA.exe";
        string lin_path = "build_linux/Virtual_NOVA.x86_64";
        string and_path = "build_android/Virtual_NOVA.apk";
        string[] levels = new string[] {"Assets/Scenes/Visualisation Scene.unity"};

        // Build player.
        BuildPipeline.BuildPlayer(levels, win_path, BuildTarget.StandaloneWindows64, BuildOptions.None);
        BuildPipeline.BuildPlayer(levels, lin_path, BuildTarget.StandaloneLinux64, BuildOptions.None);
        BuildPipeline.BuildPlayer(levels, and_path, BuildTarget.Android, BuildOptions.None);
    }
}
