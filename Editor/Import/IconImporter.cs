using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IconBrowser.Data;
using UnityEditor;
using UnityEngine;

namespace IconBrowser.Import
{
    /// <summary>
    /// Handles downloading SVG from Iconify, converting for Unity, and importing with correct settings.
    /// Implements IIconImporter for dependency injection.
    /// Static convenience methods delegate to <see cref="Default"/>.
    /// </summary>
    public class IconImporter : IIconImporter
    {
        static readonly Regex ClassAttributeRegex = new(@"\s+class=""[^""]*""", RegexOptions.Compiled);

        static readonly string META_TEMPLATE = @"fileFormatVersion: 2
guid: {0}
ScriptedImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 2
  userData:
  assetBundleName:
  assetBundleVariant:
  script: {{fileID: 12408, guid: 0000000000000000e000000000000000, type: 0}}
  svgType: 3
  texturedSpriteMeshType: 0
  svgPixelsPerUnit: 100
  gradientResolution: 64
  alignment: 0
  customPivot: {{x: 0, y: 0}}
  generatePhysicsShape: 0
  viewportOptions: 1
  preserveViewport: 0
  advancedMode: 0
  tessellationMode: 0
  predefinedResolutionIndex: 1
  targetResolution: 1080
  resolutionMultiplier: 1
  stepDistance: 10
  samplingStepDistance: 100
  maxCordDeviationEnabled: 0
  maxCordDeviation: 1
  maxTangentAngleEnabled: 0
  maxTangentAngle: 5
  keepTextureAspectRatio: 1
  textureSize: 256
  textureWidth: 256
  textureHeight: 256
  wrapMode: 0
  filterMode: 1
  sampleCount: 4
  preserveSVGImageAspect: 0
  useSVGPixelsPerUnit: 0
  spriteData:
    TessellationDetail: 0
    SpriteName:
    SpritePivot: {{x: 0, y: 0}}
    SpriteAlignment: 0
    SpriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
    SpriteRect:
      serializedVersion: 2
      x: 0
      y: 0
      width: 0
      height: 0
    SpriteID:
    PhysicsOutlines: []
";

        readonly IIconifyClient _client;
        readonly IIconManifest _manifest;

        /// <summary>
        /// Shared default instance for backward compatibility.
        /// </summary>
        public static readonly IconImporter Default = new(IconifyClient.Default, IconManifest.Default);

        /// <summary>
        /// Creates an IconImporter with explicit dependencies.
        /// </summary>
        public IconImporter(IIconifyClient client, IIconManifest manifest)
        {
            _client = client;
            _manifest = manifest;
        }

        #region IIconImporter (instance methods)

        public async Task<bool> ImportIconAsync(string prefix, string name)
        {
            var svg = await _client.GetSvgAsync(prefix, name);
            var converted = ConvertForUnity(svg);

            var iconsDir = $"{IconBrowserSettings.IconsPath}/{prefix}";
            var fullDir = Path.GetFullPath(iconsDir);
            if (!Directory.Exists(fullDir))
                Directory.CreateDirectory(fullDir);

            var assetPath = $"{iconsDir}/{name}.svg";
            var fullPath = Path.GetFullPath(assetPath);
            var metaPath = fullPath + ".meta";

            try
            {
                var guid = System.Guid.NewGuid().ToString("N");
                File.WriteAllText(fullPath, converted);
                File.WriteAllText(metaPath, string.Format(META_TEMPLATE, guid));

                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                ApplyImportSettings(assetPath);

                _manifest.Set(name, prefix);
                Debug.Log($"[IconBrowser] Imported: {name} from {prefix}");
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[IconBrowser] Import failed for {prefix}:{name}: {ex.Message}");
                try { if (File.Exists(fullPath)) File.Delete(fullPath); } catch { /* best effort */ }
                try { if (File.Exists(metaPath)) File.Delete(metaPath); } catch { /* best effort */ }
                return false;
            }
        }

        /// <summary>
        /// Converts SVG content for Unity compatibility.
        /// </summary>
        public string ConvertForUnity(string svg)
        {
            svg = svg.Replace("stroke=\"currentColor\"", "stroke=\"#FFFFFF\"");
            svg = svg.Replace("fill=\"currentColor\"", "fill=\"#FFFFFF\"");
            svg = svg.Replace("width=\"1em\"", "width=\"24\"");
            svg = svg.Replace("height=\"1em\"", "height=\"24\"");
            svg = ClassAttributeRegex.Replace(svg, "");
            return svg;
        }

        public bool DeleteIcon(string name, string prefix)
        {
            var iconsDir = $"{IconBrowserSettings.IconsPath}/{prefix}";
            var assetPath = $"{iconsDir}/{name}.svg";
            if (!File.Exists(Path.GetFullPath(assetPath)))
                return false;

            return AssetDatabase.DeleteAsset(assetPath);
        }

        public bool DeleteIconByPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !File.Exists(Path.GetFullPath(assetPath)))
                return false;

            return AssetDatabase.DeleteAsset(assetPath);
        }

        #endregion

        #region Static convenience methods (backward compatibility)

        public static Task<bool> ImportIconStaticAsync(string prefix, string name)
            => Default.ImportIconAsync(prefix, name);

        public static string ConvertForUnityStatic(string svg)
            => Default.ConvertForUnity(svg);

        public static bool DeleteIconStatic(string name, string prefix)
            => Default.DeleteIcon(name, prefix);

        public static bool DeleteIconByPathStatic(string assetPath)
            => Default.DeleteIconByPath(assetPath);

        #endregion

        static void ApplyImportSettings(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath);
            if (importer == null) return;

            var so = new SerializedObject(importer);
            SetPropertyInt(so, "svgType", 3);
            SetPropertyInt(so, "viewportOptions", 1);
            SetPropertyInt(so, "filterMode", IconBrowserSettings.FilterMode);
            SetPropertyInt(so, "sampleCount", IconBrowserSettings.SampleCount);

            if (so.hasModifiedProperties)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            }
        }

        static void SetPropertyInt(SerializedObject so, string propName, int value)
        {
            var prop = so.FindProperty(propName);
            if (prop != null)
                prop.intValue = value;
        }
    }
}
