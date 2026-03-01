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
    /// Use <see cref="Default"/> for the shared singleton instance.
    /// </summary>
    public class IconImporter : IIconImporter
    {
        private static readonly Regex CLASS_ATTRIBUTE_REGEX = new(@"\s+class=""[^""]*""", RegexOptions.Compiled);

        /// <summary>
        /// Generates a Unity .meta file for SVG import with configurable parameters.
        /// </summary>
        /// <param name="guid">The GUID for the asset (32-char hex, no dashes).</param>
        /// <param name="svgType">SVG import type: 1 = TexturedSprite, 3 = VectorSprite.</param>
        /// <param name="textureSize">Texture resolution (e.g. 64 for previews, 256 for imports).</param>
        /// <param name="predefinedResolutionIndex">Predefined resolution index (0 or 1).</param>
        /// <param name="targetResolution">Target resolution for vector rendering.</param>
        public static string GenerateMeta(string guid, int svgType = 3, int textureSize = 256,
            int predefinedResolutionIndex = 1, int targetResolution = 1080)
        {
            return $@"fileFormatVersion: 2
guid: {guid}
ScriptedImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 2
  userData:
  assetBundleName:
  assetBundleVariant:
  script: {{fileID: 12408, guid: 0000000000000000e000000000000000, type: 0}}
  svgType: {svgType}
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
  predefinedResolutionIndex: {predefinedResolutionIndex}
  targetResolution: {targetResolution}
  resolutionMultiplier: 1
  stepDistance: 10
  samplingStepDistance: 100
  maxCordDeviationEnabled: 0
  maxCordDeviation: 1
  maxTangentAngleEnabled: 0
  maxTangentAngle: 5
  keepTextureAspectRatio: 1
  textureSize: {textureSize}
  textureWidth: {textureSize}
  textureHeight: {textureSize}
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
        }

        private readonly IIconifyClient _client;
        private readonly IIconManifest _manifest;

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
                File.WriteAllText(metaPath, GenerateMeta(guid));

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
        /// Replaces currentColor attribute values with a concrete color for Unity SVG compatibility.
        /// Handles stroke="currentColor" and fill="currentColor" patterns.
        /// </summary>
        public static string NormalizeSvgColors(string svgBody, string replacementColor = "#FFFFFF")
        {
            svgBody = svgBody.Replace($"stroke=\"currentColor\"", $"stroke=\"{replacementColor}\"");
            svgBody = svgBody.Replace($"fill=\"currentColor\"", $"fill=\"{replacementColor}\"");
            return svgBody;
        }

        /// <summary>
        /// Converts SVG content for Unity compatibility.
        /// </summary>
        public string ConvertForUnity(string svg)
        {
            svg = NormalizeSvgColors(svg);
            svg = svg.Replace("width=\"1em\"", "width=\"24\"");
            svg = svg.Replace("height=\"1em\"", "height=\"24\"");
            svg = CLASS_ATTRIBUTE_REGEX.Replace(svg, "");
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

        #endregion IIconImporter (instance methods)

        private static void ApplyImportSettings(string assetPath)
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

        private static void SetPropertyInt(SerializedObject so, string propName, int value)
        {
            var prop = so.FindProperty(propName);
            if (prop != null)
                prop.intValue = value;
        }
    }
}
