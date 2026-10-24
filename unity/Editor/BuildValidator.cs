using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Relict.EditorTools
{
    // Stops a build whose scenes contain components Unity cannot serialize into a player
    // (a MonoBehaviour must live in a .cs file with the same name, otherwise level0 is "corrupted" at launch).
    public class BuildValidator : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            var problems = Check();
            if (problems.Length > 0)
                throw new BuildFailedException("Relict: scene components whose script file name doesn't match the class (the built game would crash):\n" + string.Join("\n", problems));
        }

        [MenuItem("Tools/Relict/Validate Scene For Build")]
        public static void Menu()
        {
            var p = Check();
            Debug.Log(p.Length == 0 ? "Relict: scene OK for build" : "Relict: problems:\n" + string.Join("\n", p));
        }

        public static string[] Check()
        {
            return Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include)
                .Where(m => m != null)
                .GroupBy(m => m.GetType())
                .Where(g =>
                {
                    var ms = MonoScript.FromMonoBehaviour(g.First());
                    return ms == null || Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(ms)) != g.Key.Name;
                })
                .Select(g => g.Key.FullName + " on " + g.Count() + " object(s), e.g. " + g.First().name)
                .ToArray();
        }
    }
}
