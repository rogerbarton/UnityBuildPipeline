using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace RogerBarton
{
    public static class BuildPipelineCustomization
    {
        /// <returns>e.g. win64/myApp.exe</returns>
        public static string GetBuildName(this BuildPipeline pipeline, BuildConfig config)
        {
            return config.name + "/" + pipeline.appName + "-" + Application.version + config.fileExt;
        }
        
        /// <returns>Name of the folder for the current build iteration of the whole pipeline</returns>
        public static string GetBuildIterationName(this BuildPipeline pipeline)
        {
            DateTime currentDate = DateTime.Now;
            return Application.productName + " (" + currentDate.Day + "-" + currentDate.Month +
                   "-" + currentDate.Year.ToString().Substring(currentDate.Year.ToString().Length - 2) + ')';
        }
        
        #region Callbacks
        public static void InitCallbacks()
        {
            BuildPipeline.OnPreBuild += OnPreBuildTest;
            BuildPipeline.OnPostBuild += OnPostBuildTest;
            BuildPipeline.OnPreBuildAll += OnPreBuildAllTest;
            BuildPipeline.OnPostBuildAll += OnPostBuildAllTest;
        }
        
        /// Example Callbacks
        private static void OnPreBuildTest(BuildConfig config, string path, StreamWriter log)
        {
            log.WriteLine("Pre-Build: " + config.name);
        }
        
        private static void OnPostBuildTest(BuildConfig config, BuildReport report, string path, StreamWriter log)
        {
            log.WriteLine("Post-Build: " + config.name + ", result: " + report.summary.result);
        }
        
        private static void OnPreBuildAllTest(string buildRoot, StreamWriter log)
        {
            log.WriteLine("Pre-Build All path: " + buildRoot);
        }

        private static void OnPostBuildAllTest(List<Tuple<BuildConfig, BuildReport, string>> data, string buildRoot,
            StreamWriter log)
        {
            log.WriteLine("Post-Build All path: " + buildRoot + ", reports: " + data.Count);
        }

        #endregion
    }
}