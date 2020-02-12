#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Diagnostics;
using Sirenix.OdinInspector;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Sirenix.Serialization;
using UnityEditor.Build.Reporting;
using UnityEditor.ShortcutManagement;
using Debug = UnityEngine.Debug;

namespace RogerBarton
{
    /// <summary>
    /// A build pipeline to create several builds at once. Configure and execute in the inspector of an instance.
    /// Use 'Create > Build Pipeline' to create an instance.
    /// Done as a ScriptableObject so data is persistent. Inspector UI done with Odin
    /// </summary>
    [CreateAssetMenu(menuName = "Build Pipeline")]
    public class BuildPipeline : SerializedScriptableObject
    {
        [OdinSerialize, HideInInspector]
        private bool initialized;
        private void Awake()
        {
            if (initialized)
                return;
            initialized = true;
            
            appName = Application.productName;
            AddBuildSettingsScenes();
        }

        #region General
        [TitleGroup("Build Pipeline")]
        [DetailedInfoBox("Use this to produce several builds at once...",
            "Choose the scenes to be compiled and to which platforms. Then Build All.\n" +
            "You can deactivate certain scenes/platforms with the checkbox.\n" +
            "You can assign shortcuts in Edit > Shortcuts > Build, " +
            "these are not uniquely assigned to a specific instance of a build pipeline but more event based.")]
        [Tooltip("The name of your executable (no file extension)")]
        public string appName;

        [TitleGroup("Build Pipeline")]
        [LabelText("Output Folder"), Tooltip("Where all build of this pipeline will be saved.\n " +
                                             "Relative to the project root, can include slashes")]
        public string pipelineRootRel = "Builds";

        [TitleGroup("Scenes")] [Tooltip("List of all scenes to be included, in order")] //TODO: allow overrides? pass to callbacks to allow for modifications?
        public List<SceneBuildData> scenes = new List<SceneBuildData>();
        #endregion

        
        #region Scenes
        [TitleGroup("Scenes")]
        [Button]
        public void AddAllScenes()
        {
            foreach (string sceneGuids in AssetDatabase.FindAssets("t:Scene"))
                scenes.Add(new SceneBuildData(true,
                    AssetDatabase.LoadAssetAtPath<SceneAsset>(AssetDatabase.GUIDToAssetPath(sceneGuids))));
        }

        [TitleGroup("Scenes")]
        [Button("Add Scenes in Build Settings")]
        public void AddBuildSettingsScenes()
        {
            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
                scenes.Add(new SceneBuildData(s.enabled, AssetDatabase.LoadAssetAtPath<SceneAsset>(s.path)));
        }

        [TitleGroup("Scenes")]
        [Button, LabelText("Apply To Build Settings")]
        public void ApplyScenesToBuildSettings()
        {
            var tmp = new List<EditorBuildSettingsScene>();
            foreach (var s in scenes)
                tmp.Add(new EditorBuildSettingsScene(AssetDatabase.GetAssetPath(s.scene), s.enabled));

            EditorBuildSettings.scenes = tmp.ToArray();
        }
        
        private string[] GetActiveScenes()
        {
            var activeScenes = new List<string>();
            foreach (SceneBuildData s in scenes)
                if (s.enabled)
                    activeScenes.Add(AssetDatabase.GetAssetPath(s.scene));

            return activeScenes.ToArray();
        }
        #endregion

        
        #region OptionOverrides
        [TitleGroup("Option Overrides", "Apply to all configs")] [OnValueChanged("ToggleDevelopmentOverride")]
        public bool developmentBuild = true;

        [OnValueChanged("ToggleAutoProfileOverride")]
        public bool autoconnectProfiler = true;

        [OnValueChanged("ToggleStrictOverride")]
        public bool strictMode = true;

        [OnValueChanged("OnOverrideOptionChanged"), Tooltip(
             "Contains all options, bools above are just for quick access to common options.\n" +
             "Note: deprecated options will be enabled if set to None")]
        public BuildOptions overrideOptions = BuildOptions.StrictMode;

        public void ToggleDevelopmentOverride()
        {
            if (developmentBuild)
                overrideOptions |= BuildOptions.Development;
            else
                overrideOptions &= ~BuildOptions.Development;
        }

        public void ToggleAutoProfileOverride()
        {
            if (autoconnectProfiler)
                overrideOptions |= BuildOptions.ConnectWithProfiler;
            else
                overrideOptions &= ~BuildOptions.ConnectWithProfiler;
        }

        public void ToggleStrictOverride()
        {
            if (strictMode)
                overrideOptions |= BuildOptions.StrictMode;
            else
                overrideOptions &= ~BuildOptions.StrictMode;
        }

        public void OnOverrideOptionChanged()
        {
            developmentBuild = (overrideOptions & BuildOptions.Development) != 0;
            autoconnectProfiler = (overrideOptions & BuildOptions.ConnectWithProfiler) != 0;
            strictMode = (overrideOptions & BuildOptions.StrictMode) != 0;
        }
        #endregion

        
        #region BuildConfigs
        [TitleGroup("Configurations"), Tooltip("Each element represents one build/platform")]
        public List<BuildConfig> buildConfigs;

        [Tooltip("Will switch back to the build target active before building."), LabelWidth(140)]
        public bool returnToActiveTarget;

        /// <summary>
        /// Adds the current build settings in the Unity build window to our buildConfigs
        /// </summary>
        [TitleGroup("Configurations")]
        [Button]
        public void TryAddCurrentBuildSettings()
        {
            var options = GetBuildPlayerOptions();
            buildConfigs.Add(new BuildConfig(options.targetGroup, options.target, options.options));
        }
        #endregion
        
        
        #region Build
        public static bool isBuilding;
        
        [TitleGroup("Build")] 
        public bool openExplorerAfter = true;

        private string pipelineRoot = "";
        
        [TitleGroup("Build")]
        [FolderPath, ReadOnly, OdinSerialize, LabelText("Last Build"), LabelWidth(60), Tooltip("Right-click to copy")]
        private string buildRoot = "";

        [TitleGroup("Build")]
        [Button, DisableIf("@buildRoot.Length == 0 || !System.IO.Directory.Exists(buildRoot)")]
        public void DeleteLastBuild()
        {
            var projectRoot =
                Application.dataPath.Remove(Application.dataPath.LastIndexOf("/Assets"), "/Assets".Length);
            if (buildRoot.Length > 0 && System.IO.Directory.Exists(buildRoot) &&
                buildRoot.Substring(0, projectRoot.Length).Equals(projectRoot)) //check if inside project
            {
                FileUtil.DeleteFileOrDirectory(buildRoot);
                buildRoot = "";
            }
            else
                Debug.LogWarning("Invalid last build directory: " + buildRoot);
        }

        /// <summary>
        /// A flag to propagate cancelling when running multiple pipelines (via a shortcut)
        /// </summary>
        private static bool cancelledPipeline;
        
        /// <summary>
        /// Builds all active configurations in the pipeline instance
        /// </summary>
        /// <returns>If the operation was cancelled by the user</returns>
        [TitleGroup("Build")]
        [Button("Build All", ButtonSizes.Large), DisableIf("@isBuilding")]//, GUIColor(0.4f, 0.8f, 1f)]
        public void BuildAll()
        {
            if (scenes.Count == 0 || buildConfigs.Count == 0)
                return;
            isBuilding = true;
            cancelledPipeline = false;
            
            if (!initializedCallbacks)
            {
                initializedCallbacks = true;
                BuildPipelineCustomization.InitCallbacks();
            }
            
            //Lock inspector so progress is seen
            var focusedInspector = EditorWindow.focusedWindow;
            if(focusedInspector.GetType().Name != "InspectorWindow")
                focusedInspector = EditorWindow.GetWindow(
                    typeof(Editor).Assembly.GetType("UnityEditor.InspectorWindow"), false, null, true);
            var prevLockState = LockInspector(ref focusedInspector);
            
            var initialBuildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
            var initialBuildTarget = EditorUserBuildSettings.activeBuildTarget;
            GetUniqueBuildRoot();

            Directory.CreateDirectory(buildRoot);
            var log = File.AppendText(buildRoot + "/build-log.txt");
            log.AutoFlush = true;
            log.WriteLine("-- Running pipeline: " + name);
            log.WriteLine("Building pipeline to " + pipelineRoot);

            log.WriteLine("OnPreBuildAll\n");
            OnPreBuildAll?.Invoke(buildRoot, log);
            
            var reports = new List<Tuple<BuildConfig, BuildReport, string>>();
            int i = 0;
            foreach (var config in buildConfigs)
            {
                log.WriteLine(i + "/" + buildConfigs.Count + (config.enabled ? " Building " : " Inactive ") + config.name);
                
                var report = Build(config, log);
                if (report != null)
                {
                    if (report.Item1.summary.result == BuildResult.Cancelled) //abort if the task was cancelled
                    {
                        log.Close();
                        Selection.activeObject = this;
                        ResetInspectorLock(focusedInspector, prevLockState);
                        isBuilding = false;
                        cancelledPipeline = true;
                        return;
                    }

                    reports.Add(new Tuple<BuildConfig, BuildReport, string>(config, report.Item1, report.Item2));
                    log.WriteLine("Done.\n");
                }
                ++i;
            }

            log.WriteLine("OnPostBuildAll");
            OnPostBuildAll?.Invoke(reports, buildRoot, log);

            foreach (var config in buildConfigs)
                config.done = false;

            if (openExplorerAfter && !Application.isBatchMode)
                OpenLastInExplorer();

            if (returnToActiveTarget && !Application.isBatchMode)
                EditorUserBuildSettings.SwitchActiveBuildTarget(initialBuildTargetGroup, initialBuildTarget);

            log.WriteLine("Completed Successfully.\n\n");
            log.Close();
            Selection.activeObject = this;
            ResetInspectorLock(focusedInspector, prevLockState);
            isBuilding = false;
        }

        private Tuple<BuildReport, string> Build(BuildConfig config, StreamWriter log)
        {
            if (!config.enabled)
                return null;

            var buildPlayerOptions = new BuildPlayerOptions
            {
                options = config.options | overrideOptions,
                scenes = GetActiveScenes(),
                target = config.target,
                targetGroup = config.targetGroup,
                locationPathName = buildRoot + this.GetBuildName(config)
            };

            log.WriteLine(buildPlayerOptions.locationPathName);
            OnPreBuild?.Invoke(config, buildPlayerOptions.locationPathName, log);
            var report = UnityEditor.BuildPipeline.BuildPlayer(buildPlayerOptions);
            if (report.summary.result == BuildResult.Cancelled)
                log.WriteLine("Cancelled, stopping pipeline...");
            else
            {
                config.done = true;
                log.WriteLine("OnPostBuild");
                OnPostBuild?.Invoke(config, report, buildPlayerOptions.locationPathName, log);
            }

            return new Tuple<BuildReport, string>(report, buildPlayerOptions.locationPathName);
        }
        #endregion

        
        #region Shortcuts
        [System.Flags]
        public enum PipelineGroup
        {
            First  = 1 << 1, 
            Second = 1 << 2,
            Third  = 1 << 3
        }

        [Tooltip("Run this pipeline when the group is triggered.\nTrigger groups via Tools > Build Pipeline or Edit > Shortcuts > Build"), EnumToggleButtons]
        public PipelineGroup pipelineGroup;

        #if UNITY_2019_1_OR_NEWER
        [Shortcut("Build/Build Pipeline 1")]
        #endif
        [MenuItem("Tools/Build Pipeline/Build Pipeline 1")]
        public static void Shortcut1() { BuildPipelineGroup(PipelineGroup.First); }
        #if UNITY_2019_1_OR_NEWER
        [Shortcut("Build/Build Pipeline 2")]
        #endif
        [MenuItem("Tools/Build Pipeline/Build Pipeline 2")]
        public static void Shortcut2() { BuildPipelineGroup(PipelineGroup.Second); }
        #if UNITY_2019_1_OR_NEWER
        [Shortcut("Build/Build Pipeline 3")]
        #endif
        [MenuItem("Tools/Build Pipeline/Build Pipeline 3")]
        public static void Shortcut3() { BuildPipelineGroup(PipelineGroup.Third); }

        /// <summary>
        /// Finds all BuildPipelines that have this shortcut set and executes them
        /// </summary>
        /// <param name="group">Which shortcut was pressed</param>
        private static void BuildPipelineGroup(PipelineGroup group = PipelineGroup.First)
        {
            if (Application.isBatchMode) //Read from command line arguments
            {
                int value = 0;
                Console.Out.WriteLine("Reading args now");
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "-pipelineGroup")
                        value = int.Parse(args[i + 1]);
                }

                group = (PipelineGroup) (1 << value);
                
                Console.Out.WriteLine("Using BuildPipelineGroup: " + value);
            }
            
            var pipelineGuids = AssetDatabase.FindAssets("t:BuildPipeline");
            foreach (var guid in pipelineGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var p = AssetDatabase.LoadAssetAtPath<BuildPipeline>(path);
                if ((p.pipelineGroup & group) != 0)
                {
                    Selection.activeObject = p;
                    p.BuildAll();
                    if (cancelledPipeline)
                        break;
                }
            }
        }
        #endregion
        

        #region Callbacks
        public static bool initializedCallbacks;
        public static Action<BuildConfig, string, StreamWriter> OnPreBuild;
        public static Action<BuildConfig, BuildReport, string, StreamWriter> OnPostBuild;
        public static Action<string, StreamWriter> OnPreBuildAll;
        public static Action<List<Tuple<BuildConfig, BuildReport, string>>, string, StreamWriter> OnPostBuildAll;
        #endregion


        #region HelperFunctions
        /// <summary>
        /// Locks the inspector to be locked, used so that an object is not deselected during scene change/build
        /// </summary>
        /// <returns>Previous lock state</returns>
        private static bool LockInspector(ref EditorWindow inspectorToBeLocked)
        {
            bool prevLockState = false;
            if (inspectorToBeLocked != null && inspectorToBeLocked.GetType().Name == "InspectorWindow")
            {
                Type type = Assembly.GetAssembly(typeof(Editor)).GetType("UnityEditor.InspectorWindow");
                PropertyInfo propertyInfo = type.GetProperty("isLocked");
                prevLockState = (bool) propertyInfo.GetValue(inspectorToBeLocked, null);

                propertyInfo.SetValue(inspectorToBeLocked, true, null);

                inspectorToBeLocked.Focus();
                inspectorToBeLocked.Repaint();
            }
            return prevLockState;
        }

        private static void ResetInspectorLock(EditorWindow inspectorToBeLocked, bool prevLockState)
        {
            //reset inspector lock, optional means object will be deselected
            if (inspectorToBeLocked != null && inspectorToBeLocked.GetType().Name == "InspectorWindow")
            {
                Type type = Assembly.GetAssembly(typeof(Editor)).GetType("UnityEditor.InspectorWindow");
                PropertyInfo propertyInfo = type.GetProperty("isLocked");

                propertyInfo?.SetValue(inspectorToBeLocked, prevLockState, null);

                inspectorToBeLocked.Repaint();
            }
        }

        /// <summary>
        /// Based on http://answers.unity.com/answers/1643460/view.html
        /// </summary>
        /// <returns>BuildPlayerOptions used in Unity Build Menu</returns>
        static BuildPlayerOptions GetBuildPlayerOptions(bool askForLocation = false,
            BuildPlayerOptions defaultOptions = new BuildPlayerOptions())
        {
            // Get static internal "GetBuildPlayerOptionsInternal" method
            MethodInfo method = typeof(BuildPlayerWindow.DefaultBuildMethods).GetMethod(
                "GetBuildPlayerOptionsInternal",
                BindingFlags.NonPublic | BindingFlags.Static);
 
            // invoke internal method
            return (BuildPlayerOptions)method.Invoke(
                null, 
                new object[] { askForLocation, defaultOptions});
        }
        
        /// <summary>
        /// Sets buildRoot to an absolute path
        /// </summary>
        private void GetUniqueBuildRoot()
        {
            //Find pipeline root folder via assets path
            if (pipelineRoot.Equals(""))
                pipelineRoot =
                    Application.dataPath.Remove(Application.dataPath.LastIndexOf("/Assets"), "/Assets".Length) 
                    + "/" + pipelineRootRel + "/";

            var path = pipelineRoot + this.GetBuildIterationName();
            //Make path unique by adding an index to the end if it already exists
            var uniquePath = path;
            for (int i = 1; Directory.Exists(uniquePath); ++i)
                uniquePath = path + " " + i;
            buildRoot = uniquePath + "/";
        }

        private void OpenLastInExplorer()
        {
            //Open File Explorer
            string p = buildRoot.Replace(@"/", @"\"); // explorer doesn't like front slashes
            Process.Start("explorer.exe", "/root," + p);
        }
        #endregion
        
        
        #region CommandlineInterface
        /// <summary>
        /// A static function to run a build pipeline.
        /// This can be used to run a build from the command line in headless mode, potentially on a server.
        /// Example usage start cmd then execute:
        /// "C:\Program Files\Unity\Hub\Editor\2019.3.0f6\Editor\Unity.exe" -quit -batchmode -projectPath Path\To\Project -executeMethod RogerBarton.BuildPipeline.BuildAll -buildPipeline "Assets/Editor/Production Build Pipeline.asset" -logfile unityBuildLog.txt
        /// </summary>
        /// <param name="assetPath">Path to the Build Pipeline ScriptableObject instance to run.
        /// You can copy this from the Project View uri e.g. Assets/BuildPipeline.asset</param>
        public static void BuildAll(string assetPath = "")
        {
            if (assetPath == null) throw new ArgumentNullException(nameof(assetPath));
            if (Application.isBatchMode) //Read from command line arguments
            {
                Console.Out.WriteLine("Reading args now");
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "-buildPipeline")
                        assetPath = args[i + 1];
                }

                if (assetPath == "")
                {
                    Console.Out.WriteLine("No BuildPipeline argument supplied, please add -buildPipeline <AssetPath>");
                    return;
                }

                Console.Out.WriteLine("Using BuildPipeline at: " + assetPath);
            }

            // Will fail if asset does not exist
            AssetDatabase.LoadAssetAtPath<BuildPipeline>(assetPath).BuildAll();
        }
        #endregion
    }

    #region Datastructs
    /// <summary>
    /// Configs for one build
    /// </summary>
    [Serializable]
    public class BuildConfig
    {
        [HideLabel] [HorizontalGroup(Width = 10)]
        public bool enabled = true;

        [HorizontalGroup(LabelWidth = 30), Tooltip("Name of this platform")]
        public string name;

        [HorizontalGroup(LabelWidth = 45), Tooltip("File extension, e.g. .exe, .apk")]
        public string fileExt;

        public BuildTargetGroup targetGroup;
        public BuildTarget target;
        public BuildOptions options;

        [ReadOnly, ShowIf("@BuildPipeline.isBuilding"),
         Tooltip("When building will show if this has completed yet. Updated drawing may be slightly delayed")]
        public bool done;

        public BuildConfig()
        {
            enabled = true;
            name = "win64";
            fileExt = ".exe";
            targetGroup = BuildTargetGroup.Standalone;
            target = BuildTarget.StandaloneWindows64;
            options = BuildOptions.StrictMode;
        }
        
        public BuildConfig(BuildTargetGroup targetGroup, BuildTarget target, BuildOptions options)
        {
            this.name = target.ToString();
            this.targetGroup = targetGroup;
            this.target = target;
            this.options = options;
        }
    }

    /// <summary>
    /// Copy of EditorBuildSettingsScene with a better UI
    /// </summary>
    [Serializable]
    public class SceneBuildData
    {
        [HorizontalGroup(Width = 10)] [HideLabel]
        public bool enabled;

        [HorizontalGroup()] [HideLabel] public SceneAsset scene;

        public SceneBuildData() { enabled = true; }

        public SceneBuildData(bool enabled, SceneAsset scene)
        {
            this.enabled = enabled;
            this.scene = scene;
        }
    }
    #endregion
}
#endif
